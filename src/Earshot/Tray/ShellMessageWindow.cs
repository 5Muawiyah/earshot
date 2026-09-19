using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Interop;

namespace Earshot.Tray;

// WM_QUERYENDSESSION or WM_ENDSESSION as it arrived.
internal sealed class SessionEndingEventArgs(bool isQuery, bool ending, uint flags) : EventArgs
{
    // True for WM_QUERYENDSESSION, false for WM_ENDSESSION.
    public bool IsQuery { get; } = isQuery;

    // WM_ENDSESSION wParam: true when the session is ending. Always true for a query.
    public bool Ending { get; } = ending;

    // The lParam ENDSESSION_* bits; 0 means shutdown or restart.
    public uint Flags { get; } = flags;
}

// A hidden top-level window that receives the shell broadcasts the tray needs. It is not a
// message-only window: those never receive broadcasts such as TaskbarCreated or WM_SETTINGCHANGE.
// https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features
// https://learn.microsoft.com/en-us/windows/win32/shell/taskbar
//
// WM_QUERYENDSESSION is answered TRUE at once and WM_ENDSESSION returns straight away; both are logged
// with their flags and raised as SessionEnding. A windowless app is ended about five seconds in and a
// forced shutdown sends no query at all, so this is a best-effort signal only.
// https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-queryendsession
// https://learn.microsoft.com/en-us/windows/win32/shutdown/wm-endsession
internal sealed class ShellMessageWindow : NativeWindow, IDisposable, IMessageWindow
{
    private readonly ILog _log;
    private readonly uint _taskbarCreated;

    public ShellMessageWindow(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;

        _taskbarCreated = Shell.RegisterWindowMessage(Shell.TaskbarCreatedMessageName);
        if (_taskbarCreated == 0)
        {
            StepOutcome step = StepOutcomes.FromWin32("register-window-message:TaskbarCreated", unchecked((uint)Marshal.GetLastPInvokeError()));
            _log.Warn("The tray icon will not be rebuilt after Explorer restarts: " + TrayReport.DescribeStep(step));
        }

        // A default CreateParams has no parent and no WS_VISIBLE: a hidden top-level window.
        CreateHandle(new CreateParams());
    }

    public event EventHandler? TaskbarCreated;

    public event EventHandler? SettingChanged;

    public event EventHandler? DisplayChanged;

    public event EventHandler<SessionEndingEventArgs>? SessionEnding;

    // IMessageWindow, for Earshot.Hotkeys.HotkeyManager: raised for every message this window receives,
    // before WndProc falls through to its own handling below or to the base implementation.
    public event EventHandler<Hotkeys.WindowMessageEventArgs>? MessageReceived;

    // IMessageWindow.IsOwnedByCurrentThread: RegisterHotKey "fails if you try to associate a hot key
    // with a window created by another thread", and UnregisterHotKey "Frees a hot key previously
    // registered by the calling thread", so HotkeyManager checks this before either call. Compares the
    // OS thread that owns this window's message queue against the OS thread running now, rather than a
    // captured managed thread id, which is not guaranteed to be the same native thread.
    public bool IsOwnedByCurrentThread => NativeMethods.GetWindowThreadProcessId(Handle, out _) == NativeMethods.GetCurrentThreadId();

    public void Dispose() => DestroyHandle();

    // The ENDSESSION_* bits as words, for the log.
    internal static string DescribeEndSessionFlags(uint flags)
    {
        if (flags == 0)
        {
            return "shutdown or restart";
        }

        var parts = new List<string>();
        uint rest = flags;
        if ((flags & NativeMethods.ENDSESSION_CLOSEAPP) != 0)
        {
            parts.Add("close app");
            rest &= ~NativeMethods.ENDSESSION_CLOSEAPP;
        }

        if ((flags & NativeMethods.ENDSESSION_CRITICAL) != 0)
        {
            parts.Add("critical");
            rest &= ~NativeMethods.ENDSESSION_CRITICAL;
        }

        if ((flags & NativeMethods.ENDSESSION_LOGOFF) != 0)
        {
            parts.Add("logoff");
            rest &= ~NativeMethods.ENDSESSION_LOGOFF;
        }

        if (rest != 0)
        {
            parts.Add("other 0x" + rest.ToString("X8", CultureInfo.InvariantCulture));
        }

        return string.Join(", ", parts);
    }

    protected override void WndProc(ref Message m)
    {
        MessageReceived?.Invoke(this, new Hotkeys.WindowMessageEventArgs(m.Msg, m.WParam, m.LParam));

        if (_taskbarCreated != 0 && unchecked((uint)m.Msg) == _taskbarCreated)
        {
            _log.Info("TaskbarCreated received.");
            TaskbarCreated?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            switch (m.Msg)
            {
                case NativeMethods.WM_SETTINGCHANGE:
                    SettingChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case NativeMethods.WM_DISPLAYCHANGE:
                    DisplayChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case NativeMethods.WM_QUERYENDSESSION:
                {
                    uint flags = unchecked((uint)(long)m.LParam);
                    _log.Info("WM_QUERYENDSESSION received: " + DescribeEndSessionFlags(flags) +
                        " (lParam 0x" + flags.ToString("X8", CultureInfo.InvariantCulture) + ").");
                    SessionEnding?.Invoke(this, new SessionEndingEventArgs(isQuery: true, ending: true, flags));
                    m.Result = 1;
                    return;
                }

                case NativeMethods.WM_ENDSESSION:
                {
                    uint flags = unchecked((uint)(long)m.LParam);
                    bool ending = m.WParam != 0;
                    _log.Info("WM_ENDSESSION received: " + (ending ? "ending" : "cancelled") + ", " + DescribeEndSessionFlags(flags) +
                        " (lParam 0x" + flags.ToString("X8", CultureInfo.InvariantCulture) + ").");
                    SessionEnding?.Invoke(this, new SessionEndingEventArgs(isQuery: false, ending, flags));
                    m.Result = 0;
                    return;
                }

                default:
                    break;
            }
        }

        base.WndProc(ref m);
    }
}
