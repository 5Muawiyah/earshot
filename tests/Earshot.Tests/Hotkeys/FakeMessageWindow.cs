using Earshot.Hotkeys;

namespace Earshot.Tests.Hotkeys;

// A settable stand-in for the host's hidden window. Raise fires MessageReceived synchronously, as the
// real window procedure does.
internal sealed class FakeMessageWindow : IMessageWindow
{
    public nint Handle { get; set; } = 0x1234;

    public bool IsOwnedByCurrentThread { get; set; } = true;

    public event EventHandler<WindowMessageEventArgs>? MessageReceived;

    public void Raise(int message, nint wParam, nint lParam) =>
        MessageReceived?.Invoke(this, new WindowMessageEventArgs(message, wParam, lParam));
}
