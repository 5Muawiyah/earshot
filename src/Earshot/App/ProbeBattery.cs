using System.Globalization;
using Earshot.App;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Widget;

namespace Earshot;

// probe battery: reads Windows' own Hands-Free battery property for the pinned AirPods once, read-only, and says
// what it found: a figure and where it came from, or why there is none, with the raw code of every step that failed.
// The AirPods' own broadcast is not read here: the tray reads that while it runs, and a one-off command cannot say
// how fresh a broadcast value is. The paired objects are left out too: Windows' query for them blocks for about a
// minute each on the machine this was written on, and diag battery-sweep reads them as evidence when asked.
// The battery property is empty with Hands-Free off, which is how Earshot runs by default, so no figure is the usual
// answer.
internal static partial class Program
{
    internal const string BatteryBroadcastNote = "The AirPods' own broadcast is read by the tray while it runs, not by this probe.";

    internal const string BatteryObjectsNote = "The paired objects are not read here: Windows' own query for them takes about a minute each. diag battery-sweep reads them.";

    static partial void ProbeBattery(ProbeContext ctx)
    {
        ServiceRegistry services = ctx.Services;
        EarshotSettings settings = services.Settings.Current;
        IBatteryProvider provider = services.Battery;

        HandsFreeBatteryRead? read = null;
        if (provider is IHandsFreeBatterySource source)
        {
            read = source.Read(settings.PinnedContainerId, settings.PinnedAddress);
        }

        WriteBatteryProbe(ctx, provider.HasSource, read);
        ctx.Handled = true;

        // Nothing pinned is a configuration answer, not a failure of the read: there was no device to ask about.
        ctx.ExitCode = provider.HasSource && NodeMatch.IsValidTargetContainer(settings.PinnedContainerId) ? ExitCodes.Ok : ExitCodes.Config;
    }

    private static void WriteBatteryProbe(ProbeContext ctx, bool hasSource, HandsFreeBatteryRead? read)
    {
        bool hasValue = read?.Percent is not null;
        if (ctx.Json)
        {
            ctx.WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", "battery");
                w.WriteBoolean("hasSource", hasSource);
                w.WriteBoolean("hasValue", hasValue);
                if (read?.Percent is int percent)
                {
                    w.WriteNumber("percent", percent);
                    w.WriteString("origin", read.Origin);
                }

                w.WriteString("note", read?.Note);
                w.WriteString("broadcast", BatteryBroadcastNote);
                w.WriteString("pairedObjects", BatteryObjectsNote);
                w.WriteStartArray("steps");
                foreach (StepOutcome step in read?.Steps ?? [])
                {
                    w.WriteStartObject();
                    w.WriteString("step", step.Step);
                    w.WriteBoolean("ok", step.Ok);
                    w.WriteString("code", "0x" + unchecked((uint)step.Code).ToString("X8", CultureInfo.InvariantCulture));
                    w.WriteString("codeName", step.CodeName);
                    w.WriteString("detail", step.Detail);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            });
            return;
        }

        ctx.Out.WriteLine("Windows' Hands-Free battery: " + (!hasSource
            ? "no source in this build."
            : read?.Percent is int figure
                ? figure.ToString(CultureInfo.InvariantCulture) + "% (from a " + read.Origin + ")"
                : "no figure. " + read?.Note));
        foreach (StepOutcome step in (read?.Steps ?? []).Where(s => !s.Ok))
        {
            ctx.Out.WriteLine("  step failed: " + step.Step + " " + step.CodeName + (step.Detail is null ? string.Empty : " (" + step.Detail + ")"));
        }

        ctx.Out.WriteLine(BatteryBroadcastNote);
        ctx.Out.WriteLine(BatteryObjectsNote);
    }
}
