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

    // A test-only proof. Unlike GaugeWindow/UiaTaskbarReader/WinRtAdvertisementSource, constructing this
    // class does nothing by itself (SHAppBarMessage is not called until Register runs), and a local probe
    // (ProbeAppBarOnPrivateDesktopTests) found ABM_NEW itself refused for a window on a
    // CreateDesktopW private desktop - SHAppBarMessage's own Shell_TrayWnd lookup is scoped to the calling
    // thread's current desktop, the same way FindWindow and EnumWindows are, so it never finds the owner's
    // real taskbar from there and never reaches it. So the proof that matters is not construction but a
    // successful ABM_NEW: only that means this call actually reached and was accepted by the real Explorer.
    // WidgetRealSurfaceGuardTests uses this to tell "a named allow-listed execution reached the real
    // Explorer" from "something else did", across the whole assembly.
    internal static int RealRegistrationCount;

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
    //
    // No Win32 code is read on failure: SHAppBarMessage's own page documents no relationship with
    // GetLastError at all ("This function returns a message-dependent value" is the whole of its Return
    // value section, https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shappbarmessage),
    // and the SHAppBarMessage import here has no SetLastError, so a code read after it would be whatever an
    // unrelated earlier P/Invoke on this thread happened to leave behind, not this call's outcome. Recording
    // that stale value as though it explained the failure would be worse than recording no code at all.
    public StepOutcome Register()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = CallbackMessage };
        nuint result = Shell.SHAppBarMessage(Shell.ABM_NEW, ref data);
        _registered = result != 0;
        if (_registered)
        {
            Interlocked.Increment(ref RealRegistrationCount);
        }

        return StepOutcomes.FromWin32("sh-app-bar-message:abm-new", 0, _registered ? null : "ABM_NEW returned FALSE.", ok: _registered);
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
        bool ok = result != 0;
        // Same reasoning as Register: no Win32 code is documented for SHAppBarMessage, so none is invented.
        return StepOutcomes.FromWin32("sh-app-bar-message:abm-remove", 0, ok ? null : "ABM_REMOVE returned FALSE.", ok: ok);
    }
}
