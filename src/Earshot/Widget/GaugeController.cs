using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Widget;

// Why the gauge is not shown. An auto-hidden bar that has slid away is expressed by GaugePlacement
// returning null (NoFreeSpace) once the on-screen part of the bar is thinner than its own thickness: this
// build gives it no fast path of its own ahead of a read, it simply falls out of the next poll. A
// full-screen application opening does get a fast path, NotifyFullScreenApp, called from the appbar
// notification callback (ABN_FULLSCREENAPP) rather than waiting for the next poll to see it; the same
// notification's own polled equivalent (QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE) still exists
// for a state the appbar notification does not cover. Both still end up Hidden with the icon shown: every
// transition into Hidden calls IGaugeSurface.HideWindow on the existing surface, so the window itself
// disappears rather than merely leaving the tray icon to say so; a transition into Off goes further
// and disposes the surface outright.
internal enum HiddenReason { NoTaskbar, ReadFailed, NoFreeSpace, Covered, NotificationState, WindowFailed }

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

    event EventHandler? LeftClicked;

    event EventHandler<Point>? RightClicked;

    // SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE | SWP_SHOWWINDOW) then UpdateLayeredWindow.
    StepOutcome ShowAt(Rectangle bounds);

    // SetWindowPos(SWP_NOZORDER | SWP_NOACTIVATE).
    StepOutcome MoveTo(Rectangle bounds);

    // SetWindowPos(HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE): re-raised above whatever
    // opened after it.
    StepOutcome Raise();

    // SetWindowPos(SWP_HIDEWINDOW | SWP_NOACTIVATE | ...). The outcome is success when there was nothing to
    // hide.
    StepOutcome HideWindow();
}

// What the controller needs from the tray icon. TrayContext's own NotifyIcon satisfies this directly.
internal interface ITrayIconVisibility
{
    bool Visible { set; }
}

// The settings the controller reads each layout. A snapshot, not a live settings object, so a test can
// hand it a fixed value without building a whole ISettingsStore.
internal readonly record struct GaugeControllerSettings(bool Enabled, bool LeftClickConnects);

// The gauge's state machine: applies each TaskbarWatcher result, shows, moves or hides the window,
// toggles the tray icon, and routes clicks. UI thread only.
//
// Debounce: the icon hides 2 s after the gauge has been continuously shown, and reappears at once
// whenever the gauge leaves Shown for any reason. Checked on every OnLayout call rather than a separate
// timer, so tests drive it with ManualTime instead of a real clock.
internal sealed class GaugeController : IDisposable
{
    public static readonly TimeSpan IconHideDebounce = TimeSpan.FromSeconds(2);

    private readonly Func<IGaugeSurface> _createSurface;
    private readonly ITrayIconVisibility _trayIcon;
    private readonly Func<GaugeControllerSettings> _settings;
    private readonly ILog _log;
    private readonly TimeProvider _time;

    private IGaugeSurface? _surface;
    private GaugeState _state = new GaugeState.Off();
    private long _shownSinceTimestamp;
    private bool _iconHiddenForShown;
    private string? _lastHideReason;
    private int? _lastHideCode;
    private bool _disposed;

    public GaugeController(Func<IGaugeSurface> createSurface, ITrayIconVisibility trayIcon, Func<GaugeControllerSettings> settings, ILog log, TimeProvider time)
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
            TransitionOff(GaugeReasons.SettingOff);
            return;
        }

        if (result.Failure is { } failure)
        {
            bool noTaskbar = failure.Step == TaskbarReadFailureStep.NoTaskbar;
            TransitionHidden(
                noTaskbar ? HiddenReason.NoTaskbar : HiddenReason.ReadFailed,
                noTaskbar ? GaugeReasons.NoTaskbar : GaugeReasons.ReadFailed,
                new HideContext { Failure = failure.Outcome, Detail = "step=" + failure.Step });
            return;
        }

        TaskbarLayout layout = result.Layout!;
        if (layout.NotificationState is Shell.QUNS_BUSY or Shell.QUNS_RUNNING_D3D_FULL_SCREEN or Shell.QUNS_PRESENTATION_MODE)
        {
            TransitionHidden(HiddenReason.NotificationState, GaugeReasons.FullScreenState,
                new HideContext { Detail = "quns=" + layout.NotificationState, Foreground = layout.Foreground });
            return;
        }

        if (layout.Covered)
        {
            TransitionHidden(HiddenReason.Covered, GaugeReasons.Covered,
                new HideContext { Window = layout.CoveringWindow, Foreground = layout.Foreground });
            return;
        }

        Rectangle? placed = GaugePlacement.Place(layout, GaugeRenderer.WidthFor(layout.Dpi));
        if (placed is not { } bounds)
        {
            TransitionHidden(HiddenReason.NoFreeSpace, GaugeReasons.NoFreeSpace, new HideContext { Foreground = layout.Foreground });
            return;
        }

        ShowOrMove(bounds, layout);
        CheckIconDebounce();
    }

    // ABN_FULLSCREENAPP from the appbar notification callback, decoded by the caller into opening/closing.
    // Opening hides the gauge at once, without waiting for TaskbarWatcher's next scheduled read: reuses
    // HiddenReason.NotificationState, the same reason a polled QUNS_BUSY/QUNS_RUNNING_D3D_FULL_SCREEN/
    // QUNS_PRESENTATION_MODE result already produces, since both describe the same "something is claiming
    // the screen" condition. Closing does nothing here: per the design, the taskbar's actual state still
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

        TransitionHidden(HiddenReason.NotificationState, GaugeReasons.FullScreenAppNotified, new HideContext());
    }

    // The owner turned the setting off, or Enabled was already false: watcher stopped, icon shown,
    // window disposed. Idempotent.
    public void TurnOff() => TransitionOff(GaugeReasons.SettingOff);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_state is GaugeState.Shown shown)
        {
            LogHide(GaugeReasons.Disposed, new HideContext(), surfaceOutcome: null, _state, shown.Bounds, ShownFor());
        }

        _disposed = true;
        DisposeSurface();
    }

    private void ShowOrMove(Rectangle bounds, TaskbarLayout layout)
    {
        IGaugeSurface surface = EnsureSurface();
        bool wasShown = _state is GaugeState.Shown;
        Rectangle? previous = wasShown ? ((GaugeState.Shown)_state).Bounds : null;
        GaugeEvent? pending;
        GaugeEvent? cover = null;

        StepOutcome outcome;
        if (!wasShown)
        {
            outcome = surface.ShowAt(bounds);
            pending = NewEvent(GaugeEventKind.Show, GaugeReasons.Placed) with
            {
                Bounds = bounds,
                Foreground = layout.Foreground,
                Detail = "was=" + WasName(),
            };
        }
        else if (previous != bounds)
        {
            outcome = surface.MoveTo(bounds);
            pending = NewEvent(GaugeEventKind.Move, GaugeReasons.LayoutChanged) with
            {
                Bounds = bounds,
                From = previous,
                ShownFor = ShownFor(),
            };
        }
        else if (layout.GaugeCentreIsGauge == false)
        {
            // Nothing moved, but the read's own WindowFromPoint check at the gauge's own centre (the
            // reader's GaugeCentreIsGauge) found something else there instead of the gauge itself: the
            // shell (or some other topmost window) has been drawn over it since the read that last
            // confirmed it was on top - a full screen state ending, or a flyout closing, are the two
            // ordinary causes. A MoveTo to the same rectangle is a SetWindowPos no-op that would leave the
            // gauge invisible under whatever now sits there; Raise puts it back on top without moving or
            // resizing it. False, not just "not true": a genuinely unreadable point (no gauge was shown for
            // this read to check) is null, never treated as covered.
            cover = NewEvent(GaugeEventKind.Cover, GaugeReasons.WindowOverGauge) with
            {
                Bounds = bounds,
                ShownFor = ShownFor(),
                Window = layout.WindowAtGaugeCentre,
                Foreground = layout.Foreground,
            };
            outcome = surface.Raise();
            pending = NewEvent(GaugeEventKind.Raise, GaugeReasons.CoveredAtCentre) with
            {
                Bounds = bounds,
                ShownFor = ShownFor(),
                Window = layout.WindowAtGaugeCentre,
                Foreground = layout.Foreground,
            };
        }
        else
        {
            // Nothing changed, and the read confirms the gauge is still the topmost window at its own
            // centre (or there was no shown gauge yet for this read to check in the first place).
            return;
        }

        if (cover is not null)
        {
            GaugeEventLog.Write(_log, cover);
        }

        if (!outcome.Ok)
        {
            GaugeEventLog.Write(_log, pending with { Failure = outcome });
            DisposeSurface();
            TransitionHidden(HiddenReason.WindowFailed, GaugeReasons.WindowFailed, new HideContext { Failure = outcome, AlreadyLoggedFailure = true, SurfaceGone = true });
            return;
        }

        GaugeEventLog.Write(_log, pending);

        bool enteringShown = _state is not GaugeState.Shown;
        _state = new GaugeState.Shown(bounds);
        _lastHideReason = null;
        _lastHideCode = null;
        if (enteringShown)
        {
            _shownSinceTimestamp = _time.GetTimestamp();
            _iconHiddenForShown = false;
            _trayIcon.Visible = true;
        }
    }

    private void CheckIconDebounce()
    {
        if (_state is GaugeState.Shown && !_iconHiddenForShown &&
            _time.GetElapsedTime(_shownSinceTimestamp) >= IconHideDebounce)
        {
            _trayIcon.Visible = false;
            _iconHiddenForShown = true;
        }
    }

    // What a hide adds to its log line beyond the reason.
    private sealed class HideContext
    {
        public StepOutcome? Failure { get; init; }

        public WindowIdentity? Window { get; init; }

        public WindowIdentity? Foreground { get; init; }

        public string? Detail { get; init; }

        // The failing step was already written by the caller (a window call that failed), so the hide line
        // does not write it a second time.
        public bool AlreadyLoggedFailure { get; init; }

        // The surface was disposed by the caller, so there is nothing left to hide.
        public bool SurfaceGone { get; init; }
    }

    private void TransitionHidden(HiddenReason reason, string logReason, HideContext context)
    {
        GaugeState previous = _state;
        bool alreadyThisReason = previous is GaugeState.Hidden hidden && hidden.Reason == reason &&
            string.Equals(_lastHideReason, logReason, StringComparison.Ordinal) && _lastHideCode == context.Failure?.Code;
        Rectangle? shownBounds = previous is GaugeState.Shown shown ? shown.Bounds : null;
        TimeSpan? shownFor = previous is GaugeState.Shown ? ShownFor() : null;

        _state = new GaugeState.Hidden(reason);
        _iconHiddenForShown = false;
        _trayIcon.Visible = true;

        StepOutcome? surfaceOutcome = context.SurfaceGone ? null : _surface?.HideWindow();

        // One line per change: leaving Shown, or a different reason from the one already recorded. A poll
        // that finds the same hidden state again writes nothing.
        if (!alreadyThisReason)
        {
            LogHide(logReason, context, surfaceOutcome, previous, shownBounds, shownFor);
            _lastHideReason = logReason;
            _lastHideCode = context.Failure?.Code;
        }
    }

    private void TransitionOff(string logReason)
    {
        // Always sets the icon visible and disposes any surface, even when already Off: the constructor
        // starts in Off before any layout is ever applied, so an "already there" guard here would leave
        // the icon in whatever state the caller set it to before the first call.
        GaugeState previous = _state;
        if (previous is not GaugeState.Off)
        {
            Rectangle? shownBounds = previous is GaugeState.Shown shown ? shown.Bounds : null;
            TimeSpan? shownFor = previous is GaugeState.Shown ? ShownFor() : null;
            StepOutcome? surfaceOutcome = _surface?.HideWindow();
            LogHide(logReason, new HideContext(), surfaceOutcome, previous, shownBounds, shownFor);
        }

        _state = new GaugeState.Off();
        _lastHideReason = null;
        _lastHideCode = null;
        _trayIcon.Visible = true;
        DisposeSurface();
    }

    private void LogHide(string logReason, HideContext context, StepOutcome? surfaceOutcome, GaugeState? previous, Rectangle? shownBounds, TimeSpan? shownFor)
    {
        StepOutcome? failure = context.AlreadyLoggedFailure ? null : context.Failure;
        if (failure is null && surfaceOutcome is { Ok: false })
        {
            failure = surfaceOutcome;
        }

        string? detail = context.Detail;
        if (previous is GaugeState.Hidden was)
        {
            detail = (detail is null ? "" : detail + " ") + "was=hidden:" + was.Reason;
        }

        if (string.IsNullOrEmpty(detail))
        {
            detail = failure?.Detail;
        }

        GaugeEventLog.Write(_log, NewEvent(GaugeEventKind.Hide, logReason) with
        {
            ShownFor = shownFor,
            Bounds = shownBounds,
            Window = context.Window,
            Foreground = context.Foreground,
            Failure = failure,
            Detail = detail,
        });
    }

    private GaugeEvent NewEvent(GaugeEventKind kind, string reason) => new(kind, reason, _time.GetUtcNow());

    private TimeSpan ShownFor() => _time.GetElapsedTime(_shownSinceTimestamp);

    private string WasName() => _state switch
    {
        GaugeState.Hidden hidden => "hidden:" + hidden.Reason,
        GaugeState.Off => "off",
        _ => "shown",
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
