namespace Earshot.TestWindow.Core;

internal enum PowerCycleGateResult
{
    Start,

    // Started, but the row can never read a clean green pass for it (design.md 6.2's amber
    // rules already cover the row text; this is only about whether the half may run at all).
    StartNoted,
    Refuse,
}

// test-gui.md section 9.3's required-transition table. Only PowerCycleVerdict.PowerDown ever
// counts as a confirmed shut down; restart, not-yet and unknown never do, whatever the
// requirement (design.md D7, and the coordinator's own instruction: "an unreadable log is
// unknown, never accepted as a shut down").
internal static class PowerCycleGate
{
    internal static PowerCycleGateResult Evaluate(PowerCycleRequirement requirement, PowerCycleVerdict verdict) => requirement switch
    {
        PowerCycleRequirement.FullShutDown => verdict switch
        {
            PowerCycleVerdict.PowerDown => PowerCycleGateResult.Start,
            PowerCycleVerdict.Restart => PowerCycleGateResult.Refuse,
            PowerCycleVerdict.NotYet => PowerCycleGateResult.Refuse,
            _ => PowerCycleGateResult.StartNoted,
        },
        PowerCycleRequirement.AnyStart => verdict switch
        {
            PowerCycleVerdict.NotYet => PowerCycleGateResult.Refuse,
            PowerCycleVerdict.Unknown => PowerCycleGateResult.StartNoted,
            _ => PowerCycleGateResult.Start,
        },
        PowerCycleRequirement.Restart => verdict switch
        {
            PowerCycleVerdict.PowerDown => PowerCycleGateResult.StartNoted,
            PowerCycleVerdict.Restart => PowerCycleGateResult.Start,
            PowerCycleVerdict.NotYet => PowerCycleGateResult.Refuse,
            _ => PowerCycleGateResult.StartNoted,
        },
        // PowerCycleRequirement.None: test 10 variant 5, a sign-out, no start needed at all.
        _ => PowerCycleGateResult.Start,
    };

    internal static string RefusalMessage(PowerCycleRequirement requirement, PowerCycleVerdict verdict)
    {
        if (requirement == PowerCycleRequirement.FullShutDown && verdict == PowerCycleVerdict.Restart)
        {
            return "That was a restart, not a shut down. This test needs a full shut down, so the second half will not start. " +
                "Shut down now (Start, Power, Shut down), start the PC again and open this window. Your first half is kept.";
        }

        return "Windows has not recorded a start since the first half finished. Do the shut down or restart this test asks for, then open this window again.";
    }
}
