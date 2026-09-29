using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Widget;

// A change of the foreground window, as the hook reports it: the window that became foreground, the class of
// its root window (never its title) and the thread that owns it.
internal sealed class ForegroundChangedEventArgs(nint hwnd, string rootClassName, uint threadId) : EventArgs
{
    public nint Hwnd { get; } = hwnd;

    public string RootClassName { get; } = rootClassName;

    public uint ThreadId { get; } = threadId;
}

// What the tray needs from a foreground change source. ForegroundChangeHook is the real one; the tray's
// tests supply a fake so no test hooks the desktop.
internal interface IForegroundChangeSource : IDisposable
{
    // Starts listening. The outcome is the raw result of the call that did it.
    StepOutcome Install();

    // Raised on the thread that called Install, once per change of the foreground window.
    event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;
}

// One SetWinEventHook for EVENT_SYSTEM_FOREGROUND alone, out of context: Windows delivers each event to the
// callback as a message on the thread that installed the hook, which is the tray's UI thread, so no other
// thread ever touches what a handler touches. The hook is read only: it observes which window became the
// foreground window and changes nothing.
//
// Every trigger the owner named for the gauge vanishing (Start, a flyout, a taskbar click, a full-screen
// application closing) changes the foreground window, and a foreground change is rare, so this one event
// covers them at no cost. A raise of the gauge changes no foreground window, so it can never cause the event
// that triggers it, and WINEVENT_SKIPOWNPROCESS filters Earshot's own windows besides. The hook is not tied
// to a process (idProcess 0), so an Explorer restart leaves nothing stale.
//
// UI thread only: created, installed and disposed on the thread that pumps messages.
internal sealed class ForegroundChangeHook : IForegroundChangeSource
{
    // The flags production uses: out of context, and no events from this process.
    internal const uint ProductionFlags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;

    // The callback is a static function (an UnmanagedCallersOnly method needs no delegate kept alive) and
    // finds its instance through the hook handle Windows passes back.
    private static readonly ConcurrentDictionary<nint, ForegroundChangeHook> Hooks = new();

    // Test seam only: counts real constructions, so the real-surface guard can prove no test hooks the
    // desktop by accident. Never read or reset in production.
    internal static int ConstructionCount;

    private readonly ILog _log;
    private readonly uint _flags;
    private nint _handle;
    private uint _installThread;
    private int _disposed;

    public ForegroundChangeHook(ILog log, uint flags = ProductionFlags)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _flags = flags;
        Interlocked.Increment(ref ConstructionCount);
    }

    public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    public unsafe StepOutcome Install()
    {
        const string Step = "set-win-event-hook:foreground";
        if (_handle != 0)
        {
            return StepOutcomes.FromWin32(Step, 0);
        }

        _installThread = NativeMethods.GetCurrentThreadId();
        nint callback = (nint)(delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void>)&Callback;
        nint handle = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND, 0, callback, 0, 0, _flags);
        if (handle == 0)
        {
            return StepOutcomes.FromWin32(Step, unchecked((uint)Marshal.GetLastPInvokeError()));
        }

        _handle = handle;
        Hooks[handle] = this;
        return StepOutcomes.FromWin32(Step, 0);
    }

    // Unhooks. UnhookWinEvent must run on the installing thread; a call from any other thread is logged and
    // still attempted, since a hook left behind is worse than a refused call.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _handle == 0)
        {
            return;
        }

        nint handle = _handle;
        _handle = 0;
        _ = Hooks.TryRemove(handle, out _);
        if (NativeMethods.GetCurrentThreadId() != _installThread)
        {
            _log.Warn("Foreground hook: disposed from a different thread than the one that installed it.");
        }

        if (!NativeMethods.UnhookWinEvent(handle))
        {
            StepOutcome outcome = StepOutcomes.FromWin32("unhook-win-event:foreground", unchecked((uint)Marshal.GetLastPInvokeError()));
            _log.Warn("Foreground hook: UnhookWinEvent failed: " + outcome.CodeName + " (" + outcome.Code + ") " + outcome.Detail);
        }
    }

    // An exception leaving an unmanaged callback ends the process, so everything is inside a try that logs
    // and returns.
    [UnmanagedCallersOnly]
    private static void Callback(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint threadId, uint eventTime)
    {
        if (!Hooks.TryGetValue(hook, out ForegroundChangeHook? instance))
        {
            return;
        }

        try
        {
            instance.OnEvent(eventType, hwnd, idObject, idChild, threadId);
        }
        catch (Exception ex)
        {
            instance._log.Error("Foreground hook: the callback threw.", ex);
        }
    }

    private void OnEvent(uint eventType, nint hwnd, int idObject, int idChild, uint threadId)
    {
        if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND || idObject != NativeMethods.OBJID_WINDOW ||
            idChild != NativeMethods.CHILDID_SELF || hwnd == 0)
        {
            return;
        }

        // Belt and braces beside WINEVENT_SKIPOWNPROCESS, and only when that flag was asked for: a hook
        // built without it (a test) wants this process's own windows too.
        if ((_flags & NativeMethods.WINEVENT_SKIPOWNPROCESS) != 0 && GaugeWindowIdentityReader.ProcessOf(hwnd) == (uint)Environment.ProcessId)
        {
            return;
        }

        nint root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        WindowIdentity? identity = GaugeWindowIdentityReader.Read(root != 0 ? root : hwnd, 0);
        ForegroundChanged?.Invoke(this, new ForegroundChangedEventArgs(hwnd, identity?.ClassName ?? "", threadId));
    }
}
