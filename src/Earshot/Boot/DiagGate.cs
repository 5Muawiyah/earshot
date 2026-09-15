using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot;

// diag gate <verb> [address]: LIVE. Starts the installed \Earshot\Gate (or \Earshot\Protect for the protect
// verbs) with RunEx exactly as the tray does, including the read-only task check first, and records the
// node state before and after, the status file, LastTaskResult and the timings as JSON evidence for the
// owner's live tests. Program.RunDiag refuses every diag target in safe mode before this runs.
internal static partial class Program
{
    static partial void DiagGate(DiagContext ctx)
    {
        ctx.Handled = true;
        string verb = ctx.Args[0];
        string? address = verb == GateVerbs.SetDevice ? ctx.Args[1] : null;
        bool protect = verb is GateVerbs.ProtectOn or GateVerbs.ProtectOff;
        string taskName = protect ? TaskPlan.ProtectTaskName : TaskPlan.GateTaskName;
        TimeSpan timeout = protect ? TaskSchedulerGate.ProtectTimeout : TaskSchedulerGate.GateTimeout;

        Paths paths = Paths.Current;
        ILog log = ctx.Services.Log;
        string? sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            sid = identity.User?.Value;
        }

        var store = new GateStore(paths.MachineFolder);
        var gate = new TaskSchedulerGate(new ComScheduledTasks(), store, paths.InstallFolder, sid, AccountSids.Translate,
            TimeProvider.System, TaskSchedulerGate.WaitOrCancelled);
        var reader = new NodeStateReader(new CfgMgr32NodeReader());
        string nonce = Guid.NewGuid().ToString("N");

        DateTimeOffset started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        (NodeReadResult? before, GateRunResult run, NodeReadResult? after, long runMs) result;
        using (var worker = new SystemWorker(log))
        {
            result = worker.RunAsync(ct =>
            {
                GateRead<DeviceIdentity> device = store.ReadDevice();
                NodeReadResult? nodesBefore = device.IsOk ? reader.Read(device.Value!.ContainerId, device.Value.Address) : null;
                long runStart = clock.ElapsedMilliseconds;
                GateRunResult gateRun = gate.Run(taskName, verb, nonce, address, timeout, ct);
                long runMs = clock.ElapsedMilliseconds - runStart;
                device = store.ReadDevice();
                NodeReadResult? nodesAfter = device.IsOk ? reader.Read(device.Value!.ContainerId, device.Value.Address) : null;
                return (nodesBefore, gateRun, nodesAfter, runMs);
            }).GetAwaiter().GetResult();
        }

        DateTimeOffset finished = DateTimeOffset.UtcNow;
        string evidence = ctx.NewEvidenceFile("gate-" + verb);
        string json = ProbeContext.JsonText(w =>
        {
            w.WriteStartObject();
            w.WriteString("target", "gate");
            w.WriteString("verb", verb);
            w.WriteString("address", address);
            w.WriteString("task", TaskPlan.TaskPath(taskName));
            w.WriteString("nonce", nonce);
            w.WriteString("startedUtc", started.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("finishedUtc", finished.ToString("O", CultureInfo.InvariantCulture));
            w.WriteNumber("runMilliseconds", result.runMs);
            w.WriteString("outcome", result.run.Outcome.ToString());
            if (result.run.LastTaskResult is int last)
            {
                w.WriteString("lastTaskResult", GateExitCodes.NameOf(last) ?? NativeCodes.Name(last));
                w.WriteNumber("lastTaskResultCode", last);
            }

            if (result.run.Status is { } status)
            {
                w.WriteStartObject("statusFile");
                w.WriteString("result", status.Result);
                w.WriteNumber("exitCode", status.ExitCode);
                w.WriteString("state", status.State);
                w.WriteString("startedUtc", status.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
                w.WriteString("finishedUtc", status.FinishedUtc.ToString("O", CultureInfo.InvariantCulture));
                WriteSteps(w, status.Steps);
                w.WriteEndObject();
            }

            WriteDiagNodes(w, "nodesBefore", result.before);
            WriteDiagNodes(w, "nodesAfter", result.after);
            WriteSteps(w, result.run.Steps);
            w.WriteEndObject();
        });
        File.WriteAllText(evidence, json);

        ctx.Out.WriteLine("gate " + verb + ": " + result.run.Outcome + " in " + result.runMs.ToString(CultureInfo.InvariantCulture) + " ms");
        if (result.after is not null)
        {
            ctx.Out.WriteLine("Node state after: " + BlockStateClassifier.Classify(tasksInstalled: true, identityKnown: true, result.after));
        }

        ctx.Out.WriteLine("Evidence: " + evidence);
        log.Info("diag gate " + verb + ": " + result.run.Outcome + ", evidence " + evidence);
        ctx.ExitCode = result.run.Outcome == GateRunOutcome.Completed ? ExitCodes.Ok : ExitCodes.Software;
    }

    private static void WriteDiagNodes(Utf8JsonWriter w, string name, NodeReadResult? read)
    {
        if (read is null)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartObject(name);
        w.WriteString("state", BlockStateClassifier.Classify(tasksInstalled: true, identityKnown: true, read).ToString());
        w.WriteStartArray("nodes");
        foreach (BluetoothNode node in read.Nodes)
        {
            w.WriteStartObject();
            w.WriteString("instanceId", node.InstanceId);
            w.WriteBoolean("present", node.IsPresent);
            w.WriteString("status", node.Status.ToString());
            w.WriteNumber("problem", node.ProblemCode);
            w.WriteBoolean("configFlagsDisabled", node.ConfigFlagsDisabledBit);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}
