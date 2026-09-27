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

    void HideWindow();
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
    private HiddenReason? _lastLoggedReason;
    private int? _lastLoggedCode;
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
            TransitionOff();
            return;
        }

        if (result.Failure is { } failure)
        {
            TransitionHidden(failure.Step == TaskbarReadFailureStep.NoTaskbar ? HiddenReason.NoTaskbar : HiddenReason.ReadFailed, failure.Outcome);
            return;
        }

        TaskbarLayout layout = result.Layout!;
        if (layout.NotificationState is Shell.QUNS_BUSY or Shell.QUNS_RUNNING_D3D_FULL_SCREEN or Shell.QUNS_PRESENTATION_MODE)
        {
            TransitionHidden(HiddenReason.NotificationState, null);
            return;
        }

        if (layout.Covered)
        {
            TransitionHidden(HiddenReason.Covered, null);
            return;
        }

        Rectangle? placed = GaugePlacement.Place(layout, GaugeRenderer.WidthFor(layout.Dpi));
        if (placed is not { } bounds)
        {
            TransitionHidden(HiddenReason.NoFreeSpace, null);
            return;
        }

        ShowOrMove(bounds);
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

        TransitionHiddenNoLog(HiddenReason.NotificationState);
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
        DisposeSurface();
    }

    private void ShowOrMove(Rectangle bounds)
    {
        IGaugeSurface surface = EnsureSurface();
        bool wasShown = _state is GaugeState.Shown;
        Rectangle? previous = wasShown ? ((GaugeState.Shown)_state).Bounds : null;

        StepOutcome outcome;
        if (!wasShown)
        {
            outcome = surface.ShowAt(bounds);
        }
        else if (previous != bounds)
        {
            outcome = surface.MoveTo(bounds);
        }
        else
        {
            // Nothing changed. A future refinement re-raises the window here once, for the case where the
            // shell has raised the taskbar above the topmost band since the gauge was last shown (a full
            // screen state ending, or a flyout closing), which would otherwise leave the gauge invisible
            // under the bar until the next move. Not implemented in this build (see the report).
            return;
        }

        if (!outcome.Ok)
        {
            LogFailureOnce(HiddenReason.WindowFailed, outcome);
            DisposeSurface();
            TransitionHiddenNoLog(HiddenReason.WindowFailed);
            return;
        }

        bool enteringShown = _state is not GaugeState.Shown;
        _state = new GaugeState.Shown(bounds);
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

    private void TransitionHidden(HiddenReason reason, StepOutcome? outcome)
    {
        if (outcome is { } o)
        {
            LogFailureOnce(reason, o);
        }

        TransitionHiddenNoLog(reason);
    }

    private void TransitionHiddenNoLog(HiddenReason reason)
    {
        bool leavingShown = _state is GaugeState.Shown;
        _state = new GaugeState.Hidden(reason);
        _iconHiddenForShown = false;
        if (leavingShown || _trayIcon is not null)
        {
            _trayIcon!.Visible = true;
        }

        _surface?.HideWindow();
    }

    private void TransitionOff()
    {
        // Always sets the icon visible and disposes any surface, even when already Off: the constructor
        // starts in Off before any layout is ever applied, so an "already there" guard here would leave
        // the icon in whatever state the caller set it to before the first call.
        _state = new GaugeState.Off();
        _trayIcon.Visible = true;
        DisposeSurface();
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
