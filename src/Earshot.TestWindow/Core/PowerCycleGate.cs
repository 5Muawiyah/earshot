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
// is unknown, never accepted as a shut down. A restart where a full shut down was needed is
// refused with no way past it: a restart does not exercise what the power cycle tests exist to
// exercise, and the newest transition decides, so a proper shut down afterwards puts it right.
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

    // A refusal has nothing to click past: either the wrong transition was recorded where only a
    // full shut down will do, or no start has been recorded at all since the first half finished.
    internal static string RefusalMessage(PowerCycleRequirement requirement, PowerCycleVerdict verdict)
    {
        if (requirement == PowerCycleRequirement.FullShutDown && verdict == PowerCycleVerdict.Restart)
        {
            return "That was a restart, not a shut down. This test needs a full shut down, so the second half will not start. " +
                "Shut down now (Start, Power, Shut down), start the PC again and open this window. Your first half is kept.";
        }

        return "Windows has not recorded a start since the first half finished. Do the shut down or restart this test asks for, then open this window again.";
    }

    // StartNoted's own warning, shown before the deliberate click MainForm's own noted-start
    // control waits for; never shown for Start or Refuse, which have nothing to click past.
    // unknownReason (PowerCycle.ReasonForUnknown over the same evidence, null when verdict was not
    // actually Unknown) says which of the two different things happened: the event log itself
    // could not be read, or it read fine but named no shut down or restart record between the
    // first half and the start. The two are not the same claim and must not share one message.
    internal static string NotedWarning(PowerCycleRequirement requirement, PowerCycleVerdict verdict, PowerCycleUnknownReason? unknownReason = null)
    {
        if (requirement == PowerCycleRequirement.Restart && verdict == PowerCycleVerdict.PowerDown)
        {
            return "That was a shut down, not a restart. This test asks for a restart, so this half cannot count as a clean pass " +
                "however it comes out. Carry on only if you want to try it anyway.";
        }

        string reasonText = unknownReason switch
        {
            PowerCycleUnknownReason.NoTransitionRecordFoundBetweenTheFirstHalfAndTheStart =>
                "The event log was read, but it named no shut down or restart between the first half and this start, so " +
                "Windows' own record of it is not available.",
            _ => "The event log could not be read, so Windows' own record of the shut down or restart is not available.",
        };

        return reasonText + " This half cannot count as a clean pass however it comes out. Carry on only if you want to try it anyway.";
    }
}
