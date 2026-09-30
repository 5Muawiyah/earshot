using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Widget;

// A top-level window of the hooked process was shown or hidden, as the hook reports it: the class of that window
// (never its title) and whether it was shown.
internal sealed class ShellWindowChangedEventArgs(string rootClassName, bool shown) : EventArgs
{
    public string RootClassName { get; } = rootClassName;

    public bool Shown { get; } = shown;
}

// What the tray needs from a source of "Explorer showed or hid a window" events. ShellWindowChangeHook is the real one;
// the tray's tests supply a fake so no test hooks the desktop.
internal interface IShellWindowChangeSource : IDisposable
{
    // Starts listening to Explorer's process. The outcome is the raw result of the call that did it.
    StepOutcome Install();

    // Listens to Explorer's process again, letting go of the one that was hooked: called when a new Explorer has
    // started, since a hook is bound to the process it was installed against.
    StepOutcome Reinstall();

    // Raised on the thread that called Install, once per top-level window shown or hidden.
    event EventHandler<ShellWindowChangedEventArgs>? ShellWindowChanged;
}

// One SetWinEventHook for EVENT_OBJECT_SHOW and EVENT_OBJECT_HIDE, out of context, limited to Explorer's process:
// the flyouts the shell opens over the taskbar (Control Centre, the overflow, Start) are windows of that process,
// and the shell raises the taskbar in the same breath, with no change of the foreground window to say so. Windows
// delivers each event to the callback as a message on the thread that installed the hook, which is the tray's UI
// thread. The hook only observes: it reports the class of a top-level window that appeared or went away, never a
// title, and changes nothing.
//
// A hook belongs to the process it was installed against, so a new Explorer needs a new hook: the tray calls
// Reinstall on TaskbarCreated, which the new Explorer broadcasts once its taskbar exists.
//
// UI thread only: created, installed and disposed on the thread that pumps messages.
internal sealed class ShellWindowChangeHook : IShellWindowChangeSource
{
    private const string Step = "set-win-event-hook:shell-window";
    private const string ShellTrayWndClass = "Shell_TrayWnd";

    // The callback is a static function (an UnmanagedCallersOnly method needs no delegate kept alive) and
    // finds its instance through the hook handle Windows passes back.
    private static readonly ConcurrentDictionary<nint, ShellWindowChangeHook> Hooks = new();

    // Test seam only: counts real constructions, so the tests can prove no harness hooks the desktop by accident.
    // Never read or reset in production.
    internal static int ConstructionCount;

    private readonly ILog _log;
    private readonly Func<uint> _processId;
    private readonly uint _flags;
    private nint _handle;
    private uint _installThread;
    private int _disposed;

    // processId: the process to listen to, asked for at every install; Explorer's (the owner of the taskbar window)
    // when null. A test that shows and hides its own windows passes its own.
    public ShellWindowChangeHook(ILog log, Func<uint>? processId = null, uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _processId = processId ?? ExplorerProcessId;
        _flags = flags;
        Interlocked.Increment(ref ConstructionCount);
    }

    public event EventHandler<ShellWindowChangedEventArgs>? ShellWindowChanged;

    public unsafe StepOutcome Install()
    {
        if (Volatile.Read(ref _disposed) != 0 || _handle != 0)
        {
            return StepOutcomes.FromWin32(Step, 0);
        }

        uint process = _processId();
        if (process == 0)
        {
            return StepOutcomes.FromWin32(Step, 0, "No process was found to listen to (there is no window of the taskbar's class).", ok: false);
        }

        _installThread = NativeMethods.GetCurrentThreadId();
        nint callback = (nint)(delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void>)&Callback;
        nint handle = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_HIDE, 0, callback, process, 0, _flags);
        // Read before anything else can change it.
        uint error = unchecked((uint)Marshal.GetLastPInvokeError());
        StepOutcome outcome = InstallOutcome(handle, error);
        if (handle == 0)
        {
            return outcome;
        }

        _handle = handle;
        Hooks[handle] = this;
        return outcome;
    }

    public StepOutcome Reinstall()
    {
        Unhook();
        return Install();
    }

    // SetWinEventHook returns zero for a hook that was not set, and that is a failure whatever GetLastError holds:
    // the documentation names no error for it, and a stale zero there would otherwise read as success.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook
    internal static StepOutcome InstallOutcome(nint handle, uint lastError) =>
        handle == 0
            ? StepOutcomes.FromWin32(Step, lastError, lastError == 0 ? "SetWinEventHook returned no hook and no error code." : null, ok: false)
            : StepOutcomes.FromWin32(Step, 0);

    // Unhooks. UnhookWinEvent must run on the installing thread; a call from any other thread is logged and
    // still attempted, since a hook left behind is worse than a refused call.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Unhook();
    }

    private void Unhook()
    {
        if (_handle == 0)
        {
            return;
        }

        nint handle = _handle;
        _handle = 0;
        _ = Hooks.TryRemove(handle, out _);
        if (NativeMethods.GetCurrentThreadId() != _installThread)
        {
            _log.Warn("Shell window hook: unhooked from a different thread than the one that installed it.");
        }

        if (!NativeMethods.UnhookWinEvent(handle))
        {
            StepOutcome outcome = StepOutcomes.FromWin32("unhook-win-event:shell-window", unchecked((uint)Marshal.GetLastPInvokeError()));
            _log.Warn("Shell window hook: UnhookWinEvent failed: " + outcome.CodeName + " (" + outcome.Code + ") " + outcome.Detail);
        }
    }

    private static uint ExplorerProcessId() => GaugeWindowIdentityReader.ProcessOf(NativeMethods.FindWindowW(ShellTrayWndClass, null));

    // An exception leaving an unmanaged callback ends the process, so everything is inside a try that logs
    // and returns.
    [UnmanagedCallersOnly]
    private static void Callback(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint eventTime)
    {
        if (!Hooks.TryGetValue(hook, out ShellWindowChangeHook? instance))
        {
            return;
        }

        try
        {
            instance.OnEvent(eventType, hwnd, idObject, idChild);
        }
        catch (Exception ex)
        {
            instance._log.Error("Shell window hook: the callback threw.", ex);
        }
    }

    private void OnEvent(uint eventType, nint hwnd, int idObject, int idChild)
    {
        if ((eventType != NativeMethods.EVENT_OBJECT_SHOW && eventType != NativeMethods.EVENT_OBJECT_HIDE) ||
            idObject != NativeMethods.OBJID_WINDOW || idChild != NativeMethods.CHILDID_SELF || hwnd == 0)
        {
            return;
        }

        // A child window's own show and hide are the noise of every window redrawing; only a window that is its own
        // root can be a flyout.
        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd)
        {
            return;
        }

        WindowIdentity? identity = GaugeWindowIdentityReader.Read(hwnd, 0);
        ShellWindowChanged?.Invoke(this, new ShellWindowChangedEventArgs(identity?.ClassName ?? "", eventType == NativeMethods.EVENT_OBJECT_SHOW));
    }
}
