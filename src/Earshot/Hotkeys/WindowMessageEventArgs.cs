namespace Earshot.Hotkeys;

// One window message, exactly as WndProc received it.
public sealed class WindowMessageEventArgs(int message, nint wParam, nint lParam) : EventArgs
{
    public int Message { get; } = message;

    public nint WParam { get; } = wParam;

    public nint LParam { get; } = lParam;
}
