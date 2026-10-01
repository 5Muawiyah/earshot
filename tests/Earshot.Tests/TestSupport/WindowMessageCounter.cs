using System.Globalization;
using System.Runtime.InteropServices;

namespace Earshot.Tests;

// Counts the messages a real window of this thread receives, by subclassing it (SetWindowSubclass) and passing every message
// on unchanged. It is what turns "the card was not hidden, shown or placed again" from a reading of the code into a count of
// the messages Windows actually sent: WM_SHOWWINDOW, WM_WINDOWPOSCHANGING and WM_WINDOWPOSCHANGED each mean the window manager
// was asked to do that, whatever the code around the call says. UI thread of the window only.
// https://learn.microsoft.com/en-us/windows/win32/controls/subclassing-overview
internal sealed class WindowMessageCounter : IDisposable
{
    public const uint WmShowWindow = 0x0018;
    public const uint WmWindowPosChanging = 0x0046;
    public const uint WmWindowPosChanged = 0x0047;
    public const uint WmPaint = 0x000F;
    public const uint WmEraseBackground = 0x0014;

    private const nuint SubclassId = 0x4552;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc procedure, nuint id, nuint data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc procedure, nuint id);

    [DllImport("comctl32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    private readonly Dictionary<uint, int> _counts = [];
    private readonly SubclassProc _procedure;
    private readonly nint _window;
    private bool _disposed;

    public WindowMessageCounter(nint window)
    {
        _window = window;
        _procedure = OnMessage;
        if (!SetWindowSubclass(window, _procedure, SubclassId, 0))
        {
            throw new InvalidOperationException("SetWindowSubclass failed with Win32 error " + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    public int Count(uint message) => _counts.GetValueOrDefault(message);

    public void Reset() => _counts.Clear();

    private nint OnMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        _counts[message] = _counts.GetValueOrDefault(message) + 1;
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveWindowSubclass(_window, _procedure, SubclassId);
    }
}
