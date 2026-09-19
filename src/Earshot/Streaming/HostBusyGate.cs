namespace Earshot.Streaming;

// The tray's busy state, as the streaming coordinator may read it from any thread. The tray writes it on its UI
// thread every time its presentation changes, which is every time its busy state does; the coordinator reads it
// wherever StartPlayingAsync happens to run. The tray also checks its own state on the UI thread before it asks
// for a start at all, so this is the second check, not the only one.
internal sealed class HostBusyGate : IBusyGate
{
    public const string Reason = "the tray is busy with another change";

    private volatile bool _busy;

    public bool IsBusy => _busy;

    public string BusyReason => _busy ? Reason : "";

    public void Set(bool busy) => _busy = busy;
}
