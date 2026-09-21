namespace Earshot.TestWindow.Core;

internal enum PowerCycleGateResult
{
    Start,

    // Started, but only after a deliberate click on its own warning (MainForm's
    // ProceedWithNotedStart, never the ordinary Start path): the row can never read a clean green
    // pass for it either way (the row's own amber qualifiers already cover the row text), but a
    // start that only ever needed noting must never be indistinguishable from one that needed
    // nothing at all.
    StartNoted,
    Refuse,
}

// The required-transition table. Only PowerCycleVerdict.PowerDown ever counts as a confirmed
// shut down; restart, not-yet and unknown never do, whatever the requirement: an unreadable log
// is unknown, never accepted as a shut down. A restart where a full shut down was needed is not
// refused outright, unlike not-yet (nothing happened at all, so there is nothing to override):
// most of the boot path still ran, so the half can still be tried, but only past the owner's own
// deliberate acknowledgement that it does not settle the acceptance question a real shut down would.
internal static class PowerCycleGate
{
    internal static PowerCycleGateResult Evaluate(PowerCycleRequirement requirement, PowerCycleVerdict verdict) => requirement switch
    {
        PowerCycleRequirement.FullShutDown => verdict switch
        {
            PowerCycleVerdict.PowerDown => PowerCycleGateResult.Start,
            PowerCycleVerdict.Restart => PowerCycleGateResult.StartNoted,
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

    // Refuse always means nothing at all was recorded since the first half finished: there is
    // nothing an owner could deliberately carry on past, only a transition still to do.
    internal static string RefusalMessage(PowerCycleRequirement requirement, PowerCycleVerdict verdict) =>
        "Windows has not recorded a start since the first half finished. Do the shut down or restart this test asks for, then open this window again.";

    // StartNoted's own warning, shown before the deliberate click MainForm's own noted-start
    // control waits for; never shown for Start or Refuse, which have nothing to click past.
    internal static string NotedWarning(PowerCycleRequirement requirement, PowerCycleVerdict verdict)
    {
        if (requirement == PowerCycleRequirement.FullShutDown && verdict == PowerCycleVerdict.Restart)
        {
            return "That was a restart, not a shut down. This test needs a full shut down to settle the acceptance question, so this " +
                "half cannot count as a clean pass however it comes out. Shut down now (Start, Power, Shut down) instead if you can; " +
                "carry on only if you want to try it anyway.";
        }

        return "The event log could not be read, so Windows' own record of the shut down or restart is not available. This half " +
            "cannot count as a clean pass however it comes out. Carry on only if you want to try it anyway.";
    }
}
