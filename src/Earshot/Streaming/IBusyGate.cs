namespace Earshot.Streaming;

// Whether the application is in the middle of something streaming must not start under: a connect or a
// disconnect of the AirPods, or a change going through the gate. The tray wires this to its own busy state
// (TrayContext), so playing from a phone never starts in the middle of an earbud operation. It is read from
// whatever thread StartPlayingAsync runs on, so an implementation must be safe to read from any thread.
internal interface IBusyGate
{
    bool IsBusy { get; }

    // "" when not busy. For the log and the outcome, never for the owner.
    string BusyReason { get; }
}
