namespace Earshot.Streaming;

// A copy of the tray's busy state that the streaming coordinator may read from any thread. The tray writes it on
// its UI thread, whenever its presentation is refreshed and again at the click that asks for a start; the coordinator
// reads it wherever StartPlayingAsync happens to run, usually a pool thread, a moment later.
//
// What that guarantees is small and is meant to be: a start is not begun when, as last written, the tray had a
// connect, a disconnect or a menu action in flight, or the block coordinator had an operation in flight. It is a
// second look at an answer the tray already took on the UI thread, not a lock. It can be out of date by the time it
// is read, and nothing stops the AirPods side starting work after it was read (TrayContext.StartPlayingAsync says
// why that is left so). What one radio does when the two overlap is a live-test item.
internal sealed class HostBusyGate : IBusyGate
{
    public const string Reason = "the tray is busy with another change";

    private volatile bool _busy;

    public bool IsBusy => _busy;

    public string BusyReason => _busy ? Reason : "";

    public void Set(bool busy) => _busy = busy;
}
