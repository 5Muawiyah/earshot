using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Widget;

// ABM_NEW on construction, ABM_REMOVE on Dispose, both through SHAppBarMessage on the widget's own hidden
// window with a registered callback message. Never ABM_SETPOS or ABM_QUERYPOS: the gauge reserves no
// taskbar space, so only the notification path (ABN_STATECHANGE, ABN_POSCHANGED, ABN_FULLSCREENAPP) is
// used, delivered to CallbackMessage on hwnd.
internal sealed class AppBarRegistration : IDisposable
{
    // The first application-defined message id: a private appbar callback message, never seen outside
    // this class's own SHAppBarMessage registration.
    public const uint CallbackMessage = NativeMethods.WM_USER + 1;

    private readonly nint _hwnd;
    private readonly ILog _log;
    private bool _registered;
    private bool _disposed;

    public AppBarRegistration(nint hwnd, ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _hwnd = hwnd;
        _log = log;
    }

    // ABM_NEW. Returns the raw outcome so the caller can log it; a failure here loses only the fast
    // notification paths, the poll still runs.
    public StepOutcome Register()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = CallbackMessage };
        nuint result = Shell.SHAppBarMessage(Shell.ABM_NEW, ref data);
        _registered = result != 0;
        return StepOutcomes.FromWin32("sh-app-bar-message:abm-new", result != 0 ? 0u : unchecked((uint)Marshal.GetLastPInvokeError()), ok: _registered);
    }

    // ABM_REMOVE then ABM_NEW: Explorer's internal appbar list is new after it restarts, so the old
    // registration is gone with it; this is the only way documented to get the fast paths back.
    public (StepOutcome Removed, StepOutcome Added) Reregister()
    {
        StepOutcome removed = RemoveCore();
        StepOutcome added = Register();
        return (removed, added);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_registered)
        {
            RemoveCore();
        }
    }

    private StepOutcome RemoveCore()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };
        nuint result = Shell.SHAppBarMessage(Shell.ABM_REMOVE, ref data);
        _registered = false;
        return StepOutcomes.FromWin32("sh-app-bar-message:abm-remove", result != 0 ? 0u : unchecked((uint)Marshal.GetLastPInvokeError()), ok: result != 0);
    }
}
