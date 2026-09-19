namespace Earshot.Hotkeys;

// The host's own hidden window: it owns the HWND and the thread that pumps it. This feature creates no
// window and no message loop of its own.
public interface IMessageWindow
{
    // The HWND of a window that already exists and whose thread pumps messages.
    nint Handle { get; }

    // True when the calling thread is the thread that created the window.
    bool IsOwnedByCurrentThread { get; }

    // Raised on the window's own thread, for every message it receives.
    event EventHandler<WindowMessageEventArgs>? MessageReceived;
}
