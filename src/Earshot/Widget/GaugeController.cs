using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Widget;

// Why the gauge is not shown. An auto-hidden bar that has slid away is expressed by GaugePlacement
// returning no rectangle (NoFreeSpace) once the on-screen part of the bar is thinner than its own
// thickness: this build gives it no fast path of its own ahead of a read, it simply falls out of the next
// poll. A full-screen application opening does get a fast path, NotifyFullScreenApp, called from the appbar
// notification callback (ABN_FULLSCREENAPP) rather than waiting for the next poll to see it; it has its own
// reason (FullScreenNotified) so the log tells it from the polled equivalent (QUNS_RUNNING_D3D_FULL_SCREEN,
// QUNS_PRESENTATION_MODE), which still exists for a state the appbar notification does not cover. Both end
// up Hidden with the icon shown: every transition into Hidden calls IGaugeSurface.HideWindow on the
// existing surface, so the window itself disappears rather than merely leaving the tray icon to say so; a
// transition into Off goes further and disposes the surface outright.
internal enum HiddenReason { NoTaskbar, ReadFailed, NoFreeSpace, Covered, NotificationState, FullScreenNotified, WindowFailed }

// The controller's state.
internal abstract record GaugeState
{
    private GaugeState()
    {
    }

    public sealed record Off : GaugeState;

    public sealed record Hidden(HiddenReason Reason) : GaugeState;

    public sealed record Shown(Rectangle Bounds) : GaugeState;
}

// What the controller needs from the gauge window. GaugeWindow is the real implementation; tests supply
// a fake (FakeGaugeSurface).
internal interface IGaugeSurface : IDisposable
{
    bool IsDisposed { get; }

    // The window handle, or 0 when there is none yet.
    nint WindowHandle { get; }

    event EventHandler? LeftClicked;

    event EventHandler<Point>? RightClicked;

    // SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE | SWP_SHOWWINDOW) then UpdateLayeredWindow.
    StepOutcome ShowAt(Rectangle bounds);

    // SetWindowPos(SWP_NOZORDER | SWP_NOACTIVATE).
    StepOutcome MoveTo(Rectangle bounds);

    // SetWindowPos(HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE): re-raised above whatever
    // opened after it.
    StepOutcome Raise();

    void HideWindow();
}

// What the controller needs from the tray icon. TrayContext's own NotifyIcon satisfies this directly.
internal interface ITrayIconVisibility
{
    bool Visible { set; }
}

// The settings the controller reads each layout. A snapshot, not a live settings object, so a test can
// hand it a fixed value without building a whole ISettingsStore.
internal readonly record struct GaugeControllerSettings(bool Enabled, bool LeftClickConnects, GaugePosition Position = GaugePosition.RightEnd);

// The gauge's state machine: applies each TaskbarWatcher result, shows, moves or hides the window,
// toggles the tray icon, and routes clicks. UI thread only.
//
// Debounce: the icon hides 2 s after the gauge has been continuously shown, and reappears at once
// whenever the gauge leaves Shown for any reason. Checked on every OnLayout call rather than a separate
// timer, so tests drive it with ManualTime instead of a real clock.
//
// A failed read keeps the gauge where it was. Only "no Shell_TrayWnd" is a definite sign that there is no
// bar to sit on; any other failure (a UI Automation read that timed out while the shell was busy, a thrown
// read) counts, and the gauge hides after ReadFailureTolerance failures in a row.
//
// Being put back on top: the shell raises the taskbar above the topmost band when Start, a flyout or a
// full-screen application closes, which leaves the gauge under it. Every one of those changes the
// foreground window, so a foreground change (OnForegroundChanged) looks at what is over the gauge's centre
// now and once more 250 ms later, and raises the gauge only when that window is the taskbar. The poll's own
// check stays as the safety net.
internal sealed class GaugeController : IDisposable
{
    public static readonly TimeSpan IconHideDebounce = TimeSpan.FromSeconds(2);

    // Three is one poll interval more than the longest read seen while a full-screen state changed (606 ms
    // in the field log), and few enough that a real loss is noticed in about three seconds.
    public const int ReadFailureTolerance = 3;

    // The gap between the two looks at what is over the gauge after a foreground change, and the shortest
    // time between two raises.
    public static readonly TimeSpan SecondLookDelay = TimeSpan.FromMilliseconds(250);

    // Raises after which no more are made until a poll confirms the gauge is on top.
    public const int RaiseCap = 4;

    private const string ShellTrayWndClass = "Shell_TrayWnd";

    private readonly Func<IGaugeSurface> _createSurface;
    private readonly ITrayIconVisibility _trayIcon;
    private readonly Func<GaugeControllerSettings> _settings;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly IGaugeCoverProbe? _coverProbe;
    private readonly Action<Action> _uiPost;

    private IGaugeSurface? _surface;
    private GaugeState _state = new GaugeState.Off();
    private long _shownSinceTimestamp;
    private bool _iconHiddenForShown;
    private HiddenReason? _lastLoggedReason;
    private int? _lastLoggedCode;
    private string? _lastHideKey;
    private int _consecutiveReadFailures;
    private long _lastRaiseTimestamp;
    private bool _raisedBefore;
    private int _raisesSinceConfirmed;
    private bool _capWarned;
    private ITimer? _secondLook;
    private string _secondLookForegroundClass = "";
    private bool _disposed;

    // coverProbe: what is at the gauge's centre; null means the foreground-change path has nothing to look
    // with and does nothing (the poll still works). uiPost: how a timer callback, which runs on a pool
    // thread, gets back to the UI thread; null runs it where it is called from (a test's manual clock).
    public GaugeController(
        Func<IGaugeSurface> createSurface, ITrayIconVisibility trayIcon, Func<GaugeControllerSettings> settings, ILog log, TimeProvider time,
        IGaugeCoverProbe? coverProbe = null, Action<Action>? uiPost = null)
    {
        ArgumentNullException.ThrowIfNull(createSurface);
        ArgumentNullException.ThrowIfNull(trayIcon);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        _createSurface = createSurface;
        _trayIcon = trayIcon;
        _settings = settings;
        _log = log;
        _time = time;
        _coverProbe = coverProbe;
        _uiPost = uiPost ?? (static action => action());
    }

    public GaugeState State => _state;

    // Raised on a left click when LeftClickConnects is off (the default): the owner asked to see the card.
    public event EventHandler? CardRequested;

    // Raised on a left click when LeftClickConnects is on: the owner asked to connect or disconnect
    // straight away, the v1 behaviour.
    public event EventHandler? ToggleRequested;

    // Raised on a right click, at the screen point clicked: the same context menu the tray icon shows.
    public event EventHandler<Point>? MenuRequested;

    // Applies one TaskbarWatcher result. Called on the UI thread, once per read.
    public void OnLayout(ITaskbarReader.Result result)
    {
        if (_disposed)
        {
            return;
        }

        GaugeControllerSettings settings = _settings();
        if (!settings.Enabled)
        {
            TransitionOff();
            return;
        }

        if (result.Failure is { } failure)
        {
            OnFailedRead(failure);
            return;
        }

        _consecutiveReadFailures = 0;
        TaskbarLayout layout = result.Layout!;
        if (layout.NotificationState is Shell.QUNS_BUSY or Shell.QUNS_RUNNING_D3D_FULL_SCREEN or Shell.QUNS_PRESENTATION_MODE)
        {
            TransitionHidden(HiddenReason.NotificationState, "NotificationState " + QunsName(layout.NotificationState),
                GaugeEventLog.HiddenNotificationState(QunsName(layout.NotificationState)), outcome: null);
            return;
        }

        if (layout.Covered)
        {
            TransitionHidden(HiddenReason.Covered, "Covered " + GaugeEventLog.Describe(layout.CoveringWindow),
                GaugeEventLog.HiddenCovered(layout.CoveringWindow), outcome: null);
            return;
        }

        GaugeLayout gauge = GaugeLayout.For(layout.Dpi);
        PlacementResult placed = GaugePlacement.Place(layout, gauge, settings.Position);
        if (placed.Bounds is not { } bounds)
        {
            if (!(_state is GaugeState.Hidden { Reason: HiddenReason.NoFreeSpace } && _lastHideKey == "NoFreeSpace " + placed.Failure))
            {
                _log.Write(LogLevel.Debug, GaugeEventLog.PlacementFailed(placed.Failure));
            }

            TransitionHidden(HiddenReason.NoFreeSpace, "NoFreeSpace " + placed.Failure, GaugeEventLog.HiddenNoFreeSpace(), outcome: null);
            return;
        }

        ShowOrMove(bounds, layout);
        CheckIconDebounce();
    }

    // A read failed. No Shell_TrayWnd is definite: there is no bar to sit on. Anything else is counted, and
    // while the gauge is on screen it stays where it is until ReadFailureTolerance failures in a row.
    private void OnFailedRead(TaskbarReadFailure failure)
    {
        if (failure.Step == TaskbarReadFailureStep.NoTaskbar)
        {
            TransitionHidden(HiddenReason.NoTaskbar, "NoTaskbar", GaugeEventLog.HiddenNoTaskbar(), failure.Outcome);
            return;
        }

        _consecutiveReadFailures++;
        if (_state is GaugeState.Shown shown && _consecutiveReadFailures < ReadFailureTolerance)
        {
            _log.Write(LogLevel.Debug, GaugeEventLog.KeptAfterFailedRead(shown.Bounds, failure.Step.ToString(), failure.Outcome, _consecutiveReadFailures, ReadFailureTolerance));
            return;
        }

        TransitionHidden(
            HiddenReason.ReadFailed, "ReadFailed " + failure.Step + " " + failure.Outcome.Code,
            GaugeEventLog.HiddenReadFailed(failure.Step.ToString(), failure.Outcome, _consecutiveReadFailures), failure.Outcome);
    }

    // ABN_FULLSCREENAPP from the appbar notification callback, decoded by the caller into opening/closing.
    // Opening hides the gauge at once, without waiting for TaskbarWatcher's next scheduled read, with its own
    // reason: FullScreenNotified, not the NotificationState a polled QUNS_BUSY/QUNS_RUNNING_D3D_FULL_SCREEN/
    // QUNS_PRESENTATION_MODE result gives. Closing does nothing here: the taskbar's actual state still
    // needs re-reading before anything is shown again, so the caller pokes TaskbarWatcher for a fresh read
    // instead of this method forcing a show. Does nothing while the widget is off (Off is not a state this
    // notification should move out of): the caller poking a disposed or never-built watcher is already
    // harmless on its own.
    public void NotifyFullScreenApp(bool opening)
    {
        if (_disposed || !opening || _state is GaugeState.Off)
        {
            return;
        }

        TransitionHidden(HiddenReason.FullScreenNotified, "FullScreenNotified", GaugeEventLog.HiddenFullScreenNotified(), outcome: null);
    }

    // The foreground window changed to a window of the given class (a class only, never a title). Looks at
    // what is over the gauge now and again after SecondLookDelay, since the shell can raise the taskbar a
    // moment after the change. Does nothing unless the gauge is on screen. UI thread.
    public void OnForegroundChanged(string foregroundClass)
    {
        if (_disposed || _state is not GaugeState.Shown)
        {
            return;
        }

        CheckCover(foregroundClass);
        if (_secondLook is not null)
        {
            // One second look is already pending: this change is covered by it.
            _secondLookForegroundClass = foregroundClass;
            return;
        }

        _secondLookForegroundClass = foregroundClass;
        _secondLook = _time.CreateTimer(_ => _uiPost(OnSecondLook), null, SecondLookDelay, Timeout.InfiniteTimeSpan);
    }

    // The owner turned the setting off, or Enabled was already false: watcher stopped, icon shown,
    // window disposed. Idempotent.
    public void TurnOff() => TransitionOff();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _secondLook?.Dispose();
        _secondLook = null;
        DisposeSurface();
    }

    private void OnSecondLook()
    {
        _secondLook?.Dispose();
        _secondLook = null;
        if (_disposed || _state is not GaugeState.Shown)
        {
            return;
        }

        CheckCover(_secondLookForegroundClass);
    }

    // Asks what is at the gauge's centre. The gauge itself: nothing to do. The taskbar: put the gauge back
    // on top, within the limits. Anything else (a flyout, a full-screen window, another topmost window)
    // belongs over the gauge for now and is left alone.
    private void CheckCover(string foregroundClass)
    {
        if (_coverProbe is null || _surface is not { IsDisposed: false } surface || _state is not GaugeState.Shown shown)
        {
            return;
        }

        GaugeCover cover = _coverProbe.Probe(new ShownGauge(shown.Bounds, surface.WindowHandle));
        if (cover.IsGauge)
        {
            return;
        }

        if (!string.Equals(cover.RootClassName, ShellTrayWndClass, StringComparison.Ordinal))
        {
            _log.Write(LogLevel.Debug, GaugeEventLog.LeftUnder(cover.RootClassName));
            return;
        }

        if (_raisedBefore && _time.GetElapsedTime(_lastRaiseTimestamp) < SecondLookDelay)
        {
            _log.Write(LogLevel.Debug, GaugeEventLog.RaiseSkippedRateLimit());
            return;
        }

        if (_raisesSinceConfirmed >= RaiseCap)
        {
            if (!_capWarned)
            {
                _capWarned = true;
                _log.Write(LogLevel.Warn, GaugeEventLog.RaiseCapReached());
            }

            return;
        }

        var over = new WindowIdentity(cover.RootClassName, cover.BelongsToExplorer);
        RaiseSurface(surface, GaugeEventLog.RaisedAfterForegroundChange(over, foregroundClass));
    }

    // Puts the gauge back on top and writes the line saying why. A failing raise is a window failure, the
    // same as a failing show or move.
    private void RaiseSurface(IGaugeSurface surface, string line)
    {
        StepOutcome outcome = surface.Raise();
        _lastRaiseTimestamp = _time.GetTimestamp();
        _raisedBefore = true;
        _raisesSinceConfirmed++;
        if (!outcome.Ok)
        {
            LogFailureOnce(HiddenReason.WindowFailed, outcome);
            DisposeSurface();
            TransitionHidden(HiddenReason.WindowFailed, "WindowFailed " + outcome.Code, GaugeEventLog.HiddenWindowFailed(outcome), outcome: null);
            return;
        }

        _log.Info(line);
    }

    private void ShowOrMove(Rectangle bounds, TaskbarLayout layout)
    {
        IGaugeSurface surface = EnsureSurface();
        bool wasShown = _state is GaugeState.Shown;
        Rectangle? previous = wasShown ? ((GaugeState.Shown)_state).Bounds : null;

        if (layout.GaugeCentreIsGauge == true)
        {
            _raisesSinceConfirmed = 0;
            _capWarned = false;
        }

        if (!wasShown)
        {
            string reason = ShownReason();
            StepOutcome shownOutcome = surface.ShowAt(bounds);
            if (!shownOutcome.Ok)
            {
                FailWindow(shownOutcome);
                return;
            }

            _log.Info(GaugeEventLog.Shown(bounds, reason));
        }
        else if (previous != bounds)
        {
            StepOutcome movedOutcome = surface.MoveTo(bounds);
            if (!movedOutcome.Ok)
            {
                FailWindow(movedOutcome);
                return;
            }

            _log.Info(GaugeEventLog.Moved(bounds));
        }
        else if (layout.GaugeCentreIsGauge == false)
        {
            // Nothing moved, but the read's own WindowFromPoint check at the gauge's own centre (the
            // reader's GaugeCentreIsGauge) found something else there instead of the gauge itself: the
            // shell (or some other topmost window) has been drawn over it since the read that last
            // confirmed it was on top. A MoveTo to the same rectangle is a SetWindowPos no-op that would
            // leave the gauge invisible under whatever now sits there; Raise puts it back on top without
            // moving or resizing it. False, not just "not true": a genuinely unreadable point (no gauge
            // was shown for this read to check) is null, never treated as covered.
            RaiseSurface(surface, GaugeEventLog.RaisedByPoll(layout.WindowAtGaugeCentre));
            if (_state is not GaugeState.Shown)
            {
                return;
            }
        }
        else
        {
            // Nothing changed, and the read confirms the gauge is still the topmost window at its own
            // centre (or there was no shown gauge yet for this read to check in the first place).
            return;
        }

        bool enteringShown = _state is not GaugeState.Shown;
        _state = new GaugeState.Shown(bounds);
        _lastHideKey = null;
        if (enteringShown)
        {
            _shownSinceTimestamp = _time.GetTimestamp();
            _iconHiddenForShown = false;
            _trayIcon.Visible = true;
        }
    }

    // A window call failed: written once as an Error with its raw code, the surface is thrown away (a lost
    // handle is a fresh window next time) and the gauge hides.
    private void FailWindow(StepOutcome outcome)
    {
        LogFailureOnce(HiddenReason.WindowFailed, outcome);
        DisposeSurface();
        TransitionHidden(HiddenReason.WindowFailed, "WindowFailed " + outcome.Code, GaugeEventLog.HiddenWindowFailed(outcome), outcome: null);
    }

    // What the gauge was doing just before it is shown again, as the reason the log gives.
    private string ShownReason() => _state switch
    {
        GaugeState.Hidden { Reason: HiddenReason.NoTaskbar } => GaugeEventLog.ShownTaskbarBack,
        GaugeState.Hidden { Reason: HiddenReason.ReadFailed } => GaugeEventLog.ShownReadRecovered,
        GaugeState.Hidden { Reason: HiddenReason.NotificationState or HiddenReason.FullScreenNotified } => GaugeEventLog.ShownFullScreenClosed,
        GaugeState.Hidden { Reason: HiddenReason.NoFreeSpace } => GaugeEventLog.ShownFreeSpaceBack,
        GaugeState.Hidden { Reason: HiddenReason.Covered } => GaugeEventLog.ShownUncovered,
        GaugeState.Hidden { Reason: HiddenReason.WindowFailed } => GaugeEventLog.ShownWindowRecreated,
        _ => GaugeEventLog.ShownFirstLayout,
    };

    private void CheckIconDebounce()
    {
        if (_state is GaugeState.Shown && !_iconHiddenForShown &&
            _time.GetElapsedTime(_shownSinceTimestamp) >= IconHideDebounce)
        {
            _trayIcon.Visible = false;
            _iconHiddenForShown = true;
        }
    }

    // key tells one cause from another of the same HiddenReason (a different failing step, a different
    // covering window) so a change of cause is a line and a poll that finds the same thing again is not.
    private void TransitionHidden(HiddenReason reason, string key, string line, StepOutcome? outcome)
    {
        if (outcome is { } o)
        {
            LogFailureOnce(reason, o);
        }

        bool changed = _state is not GaugeState.Hidden hidden || hidden.Reason != reason || !string.Equals(_lastHideKey, key, StringComparison.Ordinal);
        _state = new GaugeState.Hidden(reason);
        _lastHideKey = key;
        _iconHiddenForShown = false;
        _trayIcon.Visible = true;
        _surface?.HideWindow();
        if (changed)
        {
            _log.Info(line);
        }
    }

    private void TransitionOff()
    {
        // Always sets the icon visible and disposes any surface, even when already Off: the constructor
        // starts in Off before any layout is ever applied, so an "already there" guard here would leave
        // the icon in whatever state the caller set it to before the first call.
        bool wasOff = _state is GaugeState.Off;
        _state = new GaugeState.Off();
        _lastHideKey = null;
        _consecutiveReadFailures = 0;
        _trayIcon.Visible = true;
        DisposeSurface();
        if (!wasOff)
        {
            _log.Info(GaugeEventLog.HiddenSettingOff());
        }
    }

    private void LogFailureOnce(HiddenReason reason, StepOutcome outcome)
    {
        if (_lastLoggedReason == reason && _lastLoggedCode == outcome.Code)
        {
            _log.Write(LogLevel.Debug, "Gauge " + reason + ": " + outcome.CodeName + " (repeat).");
            return;
        }

        _lastLoggedReason = reason;
        _lastLoggedCode = outcome.Code;
        _log.Error("Gauge " + reason + ": " + outcome.CodeName + " (" + outcome.Code + ") " + outcome.Detail);
    }

    private static string QunsName(int quns) => quns switch
    {
        Shell.QUNS_BUSY => "QUNS_BUSY",
        Shell.QUNS_RUNNING_D3D_FULL_SCREEN => "QUNS_RUNNING_D3D_FULL_SCREEN",
        Shell.QUNS_PRESENTATION_MODE => "QUNS_PRESENTATION_MODE",
        _ => "QUNS_" + quns,
    };

    private IGaugeSurface EnsureSurface()
    {
        if (_surface is { IsDisposed: false } existing)
        {
            return existing;
        }

        if (_surface is not null)
        {
            _surface.LeftClicked -= OnLeftClicked;
            _surface.RightClicked -= OnRightClicked;
        }

        IGaugeSurface created = _createSurface();
        created.LeftClicked += OnLeftClicked;
        created.RightClicked += OnRightClicked;
        _surface = created;
        return created;
    }

    private void DisposeSurface()
    {
        if (_surface is null)
        {
            return;
        }

        _surface.LeftClicked -= OnLeftClicked;
        _surface.RightClicked -= OnRightClicked;
        _surface.Dispose();
        _surface = null;
    }

    private void OnLeftClicked(object? sender, EventArgs e)
    {
        if (_settings().LeftClickConnects)
        {
            ToggleRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            CardRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnRightClicked(object? sender, Point point) => MenuRequested?.Invoke(this, point);
}
