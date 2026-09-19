namespace Earshot.Streaming;

// The submenu item a click landed on, as the coordinator built it.
internal sealed class StreamingMenuItemEventArgs(StreamingMenuItem item) : EventArgs
{
    public StreamingMenuItem Item { get; } = item;
}
