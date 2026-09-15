using Earshot.App;
using Earshot.Contracts;
using Earshot.Contracts.Null;

namespace Earshot;

// probe battery: reports that v1 has no battery source and restates the phase 0 evidence behind
// that decision. It reads nothing from the device and never prints a percentage.
internal static partial class Program
{
    internal const string BatteryEvidenceDate = "15 September 2026";

    internal static readonly IReadOnlyList<string> BatteryEvidence =
    [
        "Get-PnpDeviceProperty, filtered to battery keys, returned nothing for the AirPods device nodes matched by name.",
        "A full property dump of the 13 device nodes in the AirPods container had no battery key.",
        "WinRT returned the battery key {104EA319-6EE2-4701-BD47-8DDBF425BBE5},2 empty for the AirPods endpoint and its device nodes.",
    ];

    internal const string BatteryDisconnectedCheck = "Not run yet. It is live test 11.";

    internal const string BatteryDecision = "No battery source in v1. The tray shows no battery element.";

    static partial void ProbeBattery(ProbeContext ctx)
    {
        var provider = new NoBatterySource();
        BatteryReading reading = provider.Read(Guid.Empty);

        if (ctx.Json)
        {
            ctx.WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", "battery");
                w.WriteBoolean("hasSource", provider.HasSource);
                w.WriteBoolean("hasValue", reading.HasValue);
                w.WriteStartObject("phase0");
                w.WriteString("date", BatteryEvidenceDate);
                w.WriteString("connectedToPc", "yes");
                w.WriteStartArray("findings");
                foreach (string finding in BatteryEvidence)
                {
                    w.WriteStringValue(finding);
                }

                w.WriteEndArray();
                w.WriteString("disconnectedCheck", BatteryDisconnectedCheck);
                w.WriteString("decision", BatteryDecision);
                w.WriteEndObject();
                w.WriteEndObject();
            });
        }
        else
        {
            ctx.Out.WriteLine("Battery source: " + (provider.HasSource ? "yes" : "none"));
            ctx.Out.WriteLine("Reading: " + (reading.HasValue ? "has a value" : "no value"));
            ctx.Out.WriteLine("Phase 0, " + BatteryEvidenceDate + ", AirPods connected to this PC:");
            foreach (string finding in BatteryEvidence)
            {
                ctx.Out.WriteLine("  " + finding);
            }

            ctx.Out.WriteLine("Disconnected check: " + BatteryDisconnectedCheck);
            ctx.Out.WriteLine("Decision: " + BatteryDecision);
        }

        ctx.Handled = true;

        // The phase 0 decision is that there is no source. A provider that claims one contradicts
        // it and needs new evidence first.
        ctx.ExitCode = provider.HasSource || reading.HasValue ? ExitCodes.Software : ExitCodes.Ok;
    }
}
