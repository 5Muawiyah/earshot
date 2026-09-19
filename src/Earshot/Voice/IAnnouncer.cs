using Earshot.Contracts;

namespace Earshot.Voice;

// Speaks Earshot's state changes, one closed VoiceLine at a time. See SpeechAnnouncer for the threading
// rules this interface is built against.
public interface IAnnouncer : IDisposable
{
    bool IsAvailable { get; }

    StepOutcome Start();

    StepOutcome Announce(VoiceLine line);

    // Named StopSpeaking, not Stop: CA1716 flags Stop as a reserved word in other .NET languages (VB's
    // Stop statement) on a public interface member.
    StepOutcome StopSpeaking();

    // Outcomes recorded since the last Drain. Returns a copy and clears the list.
    IReadOnlyList<StepOutcome> Drain();

    // Deviation from the original spec, which gave the host no way to learn about a give-up that
    // happens mid-flight on the worker thread other than polling Drain(). Earshot's host (TrayContext)
    // shows a card the moment speech turns itself off, the same way it reacts to the coordinator's own
    // Changed event, so this fires once, off the worker thread, exactly when the worker gives up on its
    // own (FailuresBeforeGivingUp consecutive Speak failures). It never fires for an explicit Stop().
    event EventHandler<StepOutcome>? Stopped;
}
