using System.Runtime.InteropServices;
using Earshot.Interop;

namespace Earshot.Widget;

// What the gauge's log says about another window: its class and whether it belongs to Explorer. Never
// the title, which can carry a document name or a chat participant. BelongsToThisProcess is true for Earshot's own
// windows (the gauge's tooltip, the card, a menu), which are never a cover to put the gauge above.
internal readonly record struct WindowIdentity(string ClassName, bool BelongsToExplorer, bool BelongsToThisProcess = false);

// Reads a WindowIdentity from a window handle. Read only, safe from any thread.
internal static class GaugeWindowIdentityReader
{
    private const int MaxClassName = 256; // WNDCLASS names are limited to 256 characters.

    // The identity of hwnd, or null when the handle is 0 or the class name cannot be read (a window that
    // closed between the point query and this call). explorerProcessId is the process that owns
    // Shell_TrayWnd, read once per taskbar read. ownProcessId is the process whose windows are Earshot's own:
    // this one when it is 0, which is always so outside a test that plays the shell from this process.
    public static unsafe WindowIdentity? Read(nint hwnd, uint explorerProcessId, uint ownProcessId = 0)
    {
        if (hwnd == 0)
        {
            return null;
        }

        char* buffer = stackalloc char[MaxClassName];
        int length = NativeMethods.GetClassNameW(hwnd, buffer, MaxClassName);
        if (length <= 0)
        {
            return null;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        bool explorer = explorerProcessId != 0 && processId == explorerProcessId;
        uint own = ownProcessId != 0 ? ownProcessId : (uint)Environment.ProcessId;
        return new WindowIdentity(new string(buffer, 0, length), explorer, processId == own);
    }

    // The process that owns a window, or 0 when it cannot be read.
    public static uint ProcessOf(nint hwnd)
    {
        if (hwnd == 0)
        {
            return 0;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        return processId;
    }

    // The foreground window's identity, or null when there is none.
    public static WindowIdentity? Foreground(uint explorerProcessId) => Read(NativeMethods.GetForegroundWindow(), explorerProcessId);
}
