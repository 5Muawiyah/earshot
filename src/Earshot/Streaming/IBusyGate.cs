namespace Earshot.Streaming;

// Whether the application was, when last asked, in the middle of something streaming should not start under: a
// connect or a disconnect of the AirPods, or a change going through the gate. The tray wires this to a copy of
// its own busy state (HostBusyGate, which says how little that promises). It is read from whatever thread
// StartPlayingAsync runs on, so an implementation must be safe to read from any thread.
internal interface IBusyGate
{
    bool IsBusy { get; }

    // "" when not busy. For the log and the outcome, never for the owner.
    string BusyReason { get; }
}
