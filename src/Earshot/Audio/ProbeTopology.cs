using System.Globalization;
using Earshot.App;
using Earshot.Audio;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot;

// probe topology: walks the target device's endpoints to their kernel streaming filters with the production
// walk (TopologyWalk), applies the same guard the connect path uses (adapter ACTIVE and in the target
// container), activates IKsControl and reads KSPROPERTY_PIN_CTYPES, a KSPROPSETID_Pin Get. That proves the
// connect path is reachable while changing nothing. On the owner's machine the expected adapters are the
// A2DP filter (id ending \src) and the Hands-Free filter (id ending \wave).
internal static partial class Program
{
    internal sealed record TopologyAdapterReport(FilterVisit Visit, uint? PinCount);

    internal sealed record TopologyReport(
        bool EnumerationOk,
        DeviceModel? Target,
        TargetResolution Resolution,
        IReadOnlyList<TopologyAdapterReport> Adapters,
        IReadOnlyList<StepOutcome> Steps);

    internal const string TopologyRequestsSent = "KSPROPSETID_Pin KSPROPERTY_PIN_CTYPES Get";

    static partial void ProbeTopology(ProbeContext ctx)
    {
        ctx.Handled = true;
        ServiceRegistry services = ctx.Services;
        if (services.Monitor is not CoreAudioDeviceMonitor monitor || services.Worker is not AudioWorker worker)
        {
            WriteProbeProblem(ctx, "The audio worker is not part of this build.");
            ctx.ExitCode = ExitCodes.Unavailable;
            return;
        }

        TopologyReport report;
        try
        {
            MonitorRefresh refresh = monitor.RefreshDetailedAsync().GetAwaiter().GetResult();
            report = worker.RunAsync(_ => WalkTopology(worker, refresh)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            services.Log.Error("probe topology failed.", ex);
            WriteProbeProblem(ctx, "Walking the audio topology failed: " + ex.Message);
            ctx.ExitCode = ExitCodes.Software;
            return;
        }

        WriteTopologyProbe(ctx, report);
        ctx.ExitCode = report.EnumerationOk ? ExitCodes.Ok : ExitCodes.OsError;
    }

    // Worker thread only.
    private static TopologyReport WalkTopology(AudioWorker worker, MonitorRefresh refresh)
    {
        DeviceModel? target = refresh.Snapshot.Target;
        if (!refresh.EnumerationOk || target is null)
        {
            List<StepOutcome> enumerationSteps = refresh.EnumerationOk ? new List<StepOutcome>() : refresh.Steps.Where(s => !s.Ok).ToList();
            return new TopologyReport(refresh.EnumerationOk, target, refresh.Resolution, Array.Empty<TopologyAdapterReport>(), enumerationSteps);
        }

        // Of the enumeration's failed reads, only those about the target's own endpoints matter here.
        var steps = refresh.Steps
            .Where(s => !s.Ok && target.Endpoints.Any(e => s.Step.EndsWith(":" + e.EndpointId, StringComparison.Ordinal)))
            .ToList();

        int hr = worker.TryGetEnumerator(out IMMDeviceEnumerator? enumerator);
        if (hr < 0 || enumerator is null)
        {
            steps.Add(StepOutcomes.FromHResult(AudioWorker.Steps.CreateEnumerator, hr));
            return new TopologyReport(false, target, refresh.Resolution, Array.Empty<TopologyAdapterReport>(), steps);
        }

        AdapterDiscovery discovery = TopologyWalk.FindAdapters(enumerator, target.Endpoints, target.ContainerId);
        steps.AddRange(discovery.Steps);

        var pinCounts = new Dictionary<string, uint?>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<FilterVisit> visits = TopologyWalk.VisitFilters(enumerator, discovery.Adapters, target.ContainerId, (adapter, control) =>
        {
            StepOutcome read = TopologyWalk.ReadPinCount(control, adapter.AdapterId, out uint? pins);
            pinCounts[adapter.AdapterId] = pins;
            return new[] { read };
        });

        var adapters = new List<TopologyAdapterReport>();
        foreach (FilterVisit visit in visits)
        {
            steps.AddRange(visit.Steps.Where(s => !s.Ok));
            adapters.Add(new TopologyAdapterReport(visit, pinCounts.GetValueOrDefault(visit.Adapter.AdapterId)));
        }

        return new TopologyReport(true, target, refresh.Resolution, adapters, steps);
    }

    internal static void WriteTopologyProbe(ProbeContext ctx, TopologyReport report)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(report);

        if (ctx.Json)
        {
            ctx.WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", "topology");
                w.WriteBoolean("enumerationOk", report.EnumerationOk);
                WriteDevice(w, "device", report.Target, report.Resolution);
                w.WriteStartArray("adapters");
                foreach (TopologyAdapterReport adapter in report.Adapters)
                {
                    FilterVisit visit = adapter.Visit;
                    w.WriteStartObject();
                    w.WriteString("adapterId", visit.Adapter.AdapterId);
                    w.WriteStartArray("fromEndpoints");
                    foreach (AudioEndpoint endpoint in visit.Adapter.FromEndpoints)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", endpoint.EndpointId);
                        w.WriteString("flow", endpoint.Flow.ToString());
                        w.WriteString("state", StateText(endpoint.State));
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    if (visit.State is EndpointState state)
                    {
                        w.WriteString("state", StateText(state));
                    }
                    else
                    {
                        w.WriteNull("state");
                    }

                    if (visit.ContainerId is Guid container)
                    {
                        w.WriteString("containerId", container);
                    }
                    else
                    {
                        w.WriteNull("containerId");
                    }

                    w.WriteBoolean("guardPassed", visit.GuardPassed);
                    w.WriteBoolean("ksControlActivated", visit.ControlActivated);
                    if (adapter.PinCount is uint pins)
                    {
                        w.WriteNumber("pinCount", pins);
                    }
                    else
                    {
                        w.WriteNull("pinCount");
                    }

                    WriteSteps(w, "steps", visit.Steps);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteStartArray("requestsSent");
                if (report.Adapters.Any(a => a.Visit.ControlActivated))
                {
                    w.WriteStringValue(TopologyRequestsSent);
                }

                w.WriteEndArray();
                WriteSteps(w, "failedSteps", report.Steps);
                w.WriteEndObject();
            });
            return;
        }

        TextWriter o = ctx.Out;
        o.WriteLine("Enumeration: " + (report.EnumerationOk ? "ok" : "failed"));
        o.WriteLine(DeviceLine(report.Target, report.Resolution));
        if (report.Target is not null)
        {
            o.WriteLine("Adapters (kernel streaming filters): " + report.Adapters.Count.ToString(CultureInfo.InvariantCulture));
        }

        foreach (TopologyAdapterReport adapter in report.Adapters)
        {
            FilterVisit visit = adapter.Visit;
            o.WriteLine();
            o.WriteLine("Adapter " + visit.Adapter.AdapterId);
            foreach (AudioEndpoint endpoint in visit.Adapter.FromEndpoints)
            {
                o.WriteLine("  from " + endpoint.Flow + " endpoint " + endpoint.EndpointId + " (" + StateText(endpoint.State) + ")");
            }

            o.WriteLine("  state: " + (visit.State is EndpointState state ? StateText(state) : "not read"));
            o.WriteLine("  container: " + (visit.ContainerId is Guid container ? TopologyWalk.Format(container) : "not read"));
            o.WriteLine("  guard: " + (visit.GuardPassed ? "passed" : "not passed"));
            o.WriteLine("  IKsControl: " + (visit.ControlActivated ? "activated" : "not activated"));
            o.WriteLine("  KSPROPERTY_PIN_CTYPES: " + (adapter.PinCount is uint pins ? pins.ToString(CultureInfo.InvariantCulture) + " pins" : "not read"));
        }

        o.WriteLine();
        o.WriteLine("Requests sent: " + (report.Adapters.Any(a => a.Visit.ControlActivated) ? TopologyRequestsSent + " only" : "none"));
        WriteStepLines(o, "Failed or refused steps", report.Steps);
    }
}
