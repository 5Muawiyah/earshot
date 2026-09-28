using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Icons;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tray;
using Earshot.Widget;
using Earshot.Widget.Alert;
using Earshot.Widget.EarPause;

namespace Earshot.App;

// The widget's wiring into the tray: the data pipeline (CompositionRoot.BuildWidget), the taskbar gauge
// with its tray-icon fallback (GaugeController, TaskbarWatcher, UiaTaskbarReader, GaugeWindow), and click
// routing into the tray's own existing card and menu infrastructure. Nothing here adds a device path: a
// left click that asks to connect goes through StartToggle, the exact method the tray icon's own click
// already uses, into BlockCoordinator.
//
// Hand-back ordering (owner's instruction, carried from a security review of the data side): the widget's
// BLE watcher is suspended, and closed, only after the shut-down or sleep hand-back has finished. Never
// before it, never concurrently with it. SuspendWidget is called from TrayContext.OnSessionEnding and
// OnPowerChanged only after HoldReply has already returned (or there was nothing to hold for); CloseWidget
// runs from TrayContext.Close, which is the ordinary Exit and WM_CLOSE path, not the hand-back path.
// WidgetHandBackOrderTests pins the order for both triggers.
internal sealed partial class TrayContext
{
    private WidgetStatusService? _widgetStatus;
    private LowBatteryAlertService? _lowBatteryAlertService;
    private AutoPauseService? _autoPauseService;
    private GaugeController? _gaugeController;
    private TaskbarWatcher? _taskbarWatcher;
    private AppBarRegistration? _appBarRegistration;
    private ThemeReader? _widgetTheme;
    private GaugeWindow? _gaugeWindow;
    private WidgetCardPresenter? _widgetCardPresenter;
    private CaseOpenCardPresenter? _caseOpenCardPresenter;
    private WidgetCardPresenterCallbacks? _widgetCardCallbacks;
    private int _widgetLayoutDpi = CardPlacement96;
    private WidgetSnapshot _widgetSnapshotCache = WidgetSnapshot.Empty(WidgetWatcherState.NotStarted, claimExists: false);

    // TrayStartOptions.AdvertisementSourceFactory/TaskbarReaderFactory: the real ones by default, a fake in
    // tests (TrayHarness), so a widget-enabled test never starts a real Bluetooth watcher or polls the real
    // taskbar with UI Automation. See WidgetRealSurfaceGuardTests.
    private readonly Func<IAdvertisementSource>? _advertisementSourceFactory;
    private readonly Func<ITaskbarReader> _taskbarReaderFactory;
    private readonly int _taskbarWatcherPollIntervalMs;
    private readonly Func<ITrayIconVisibility>? _trayIconVisibilityFactory;

    // The gauge's own bounds and handle, for TaskbarWatcher's worker thread: written on the UI thread only
    // (RefreshShownGaugeForWorker, called after every OnTaskbarLayout, the only place the real window's
    // position or existence changes), read by the worker thread through ReadShownGauge. A plain field, not
    // the GaugeWindow itself: TaskbarWatcher must never touch Control.Bounds or Control.Handle off the UI
    // thread, and a stale read here costs one taskbar poll, nothing more. Boxed in a small immutable class
    // (ShownGauge is a struct, which cannot itself be volatile) so the worker thread's read is a single
    // reference load, never a torn read of the struct's own fields.
    private volatile ShownGaugeBox? _shownGaugeForWorker;

    private const int CardPlacement96 = 96;

    // Test seam: WidgetHandBackOrderTests substitutes these to prove the hand-back-first ordering without
    // constructing a real BLE watcher. Null (the default) means "call the real WidgetStatusService".
    private Action? _widgetSuspendForTest;
    private Action? _widgetResumeForTest;
    private Action? _widgetCloseForTest;

    internal void SetWidgetLifecycleForTest(Action suspend, Action resume, Action close)
    {
        _widgetSuspendForTest = suspend;
        _widgetResumeForTest = resume;
        _widgetCloseForTest = close;
    }

    // Called once from the constructor, after the menu, the tray icon and the hidden window all exist,
    // since the click routing below reaches into all three, and again from ApplyWidget whenever Enabled
    // flips from off to on after startup. Idempotent in two independent stages, each guarded on its own
    // field, so a call that finds one or both stages already wired does nothing to them:
    //   - the data pipeline (_widgetStatus), which stays built for the rest of the process once
    //     CompositionRoot.BuildWidget first succeeds: WidgetStatusService already starts and stops its own
    //     BLE source in response to the same Enabled setting changing later, so it is never torn down here.
    //     Enabled is the OR of ShowOnTaskbar and the other three consumers (WidgetSettings.WithWatcherRecomputed),
    //     so this stage runs whenever any of them wants the watcher, not only the gauge;
    //   - the card-side infrastructure that does not need the gauge to exist (_widgetTheme, the case-open
    //     card): built whenever this stage runs at all, since the case-open notice places itself near the
    //     tray with no gauge to anchor above;
    //   - the gauge's own UI pipeline (_gaugeController, _widgetCardPresenter, _taskbarWatcher), wired
    //     separately by WireGauge, called from here only when ShowOnTaskbar is on. ApplyWidget tears this one
    //     down (bar the controller and presenter, cheap to keep) when the owner turns the gauge off, so a
    //     hidden gauge leaves no UI Automation polling thread running, and WireGauge rebuilds it when turned
    //     back on - including from a start where the gauge began off and this stage was never wired.
    private void WireWidget()
    {
        if (_widgetStatus is null)
        {
            _widgetStatus = CompositionRoot.BuildWidget(_registry, () => _coordinator.BlockStatus, _time, _advertisementSourceFactory);
            if (_widgetStatus is null)
            {
                // No consumer wants it yet: nothing to wire. A later ApplyWidget call re-enters this method
                // once the owner turns one of the four consumer settings on.
                return;
            }

            _widgetStatus.Changed += OnWidgetStatusChanged;
            _widgetStatus.CaseOpened += OnCaseOpened;
            _widgetSnapshotCache = _widgetStatus.Current;
            _widgetStatus.Start();
            _lowBatteryAlertService = CompositionRoot.BuildLowBatteryAlertService(_registry, _widgetStatus);
            _autoPauseService = CompositionRoot.BuildAutoPauseService(_registry, _widgetStatus, () => _coordinator.BlockStatus, _time);
        }

        if (_widgetTheme is null)
        {
            _widgetTheme = new ThemeReader(_log);

            // Shared with the gauge-anchored card WireGauge builds below, whenever it is built: the same
            // snapshot, the same callbacks, only the Where line differs (WidgetCard itself overrides that
            // for a notice-mode instance regardless of what this carries).
            _widgetCardCallbacks = new WidgetCardPresenterCallbacks(
                CurrentSnapshot: () => _widgetSnapshotCache,
                AutoPauseOn: () => _registry.Settings.Current.Widget.AutoPause,
                CurrentIntent: () => TrayStatus.Intent(_snapshot, _registry.Settings.Current),
                IsBusy: () => IsBusy,
                Dpi: () => _widgetLayoutDpi,
                Ink: () => _widgetTheme?.Ink() ?? SystemColors.WindowText,
                HighContrast: () => SystemInformation.HighContrast,
                OtherDeviceLabel: () => _registry.Settings.Current.Widget.OtherDeviceLabel,
                RequestToggle: StartToggleFromWidget,
                SetAutoPause: (on, place) => TryUpdateSettingsFromWidget(
                    "pause when a bud comes out (widget)", s => s.Widget = (s.Widget with { AutoPause = on }).WithWatcherRecomputed(), place));

            var caseOpenGate = new CaseOpenCardGate(
                Enabled: () => _registry.Settings.Current.Widget.CaseOpenCard,
                Closing: () => _closing,
                HandBackInProgress: () => _coordinator.HandBackInProgress,
                SessionEndInProgress: () => _coordinator.SessionEndInProgress,
                OwnCardOpen: () => _widgetCardPresenter?.IsShown ?? false);
            _caseOpenCardPresenter = new CaseOpenCardPresenter(
                () => new WidgetCard(_log, notice: true), _widgetCardCallbacks, caseOpenGate, new SystemCardEnvironment(_log), _registry.UiPost, _time, _log);
        }

        if (_registry.Settings.Current.Widget.ShowOnTaskbar)
        {
            WireGauge();
        }
    }

    // The gauge's own UI pipeline: the taskbar overlay (or tray icon fallback), its card, and the
    // machinery that places and polls it. Called only while ShowOnTaskbar is on (from WireWidget, and from
    // ApplyWidget when the setting turns on after starting off); never runs before WireWidget's own
    // _widgetCardCallbacks exists, since ShowOnTaskbar being on already means Enabled is too
    // (WithWatcherRecomputed), so WireWidget's first two stages always ran first in the same call.
    private void WireGauge()
    {
        if (_gaugeController is null)
        {
            var controller = new GaugeController(
                CreateGaugeSurface,
                _trayIconVisibilityFactory?.Invoke() ?? new NotifyIconVisibility(_notifyIcon),
                ReadGaugeControllerSettings,
                _log,
                _time);
            controller.CardRequested += OnWidgetCardRequested;
            controller.ToggleRequested += (_, _) => StartToggle();
            controller.MenuRequested += (_, point) => _menu.Strip.Show(point);
            _gaugeController = controller;

            _widgetCardPresenter = new WidgetCardPresenter(() => new WidgetCard(_log), _widgetCardCallbacks!, _registry.UiPost, _time, _log);
        }

        if (_taskbarWatcher is null)
        {
            // Registered against the same hidden window that already carries every other shell broadcast,
            // for the lifetime of the gauge's UI pipeline exactly: ABM_NEW here, ABM_REMOVE wherever
            // _taskbarWatcher itself is torn down (CloseWidget, ApplyWidget's ShowOnTaskbar-off branch),
            // since ABM_NEW with no ABM_SETPOS reserves no taskbar space, so registering it while the gauge
            // is merely hidden by settings costs nothing worth guarding separately from the watcher's own
            // start/stop.
            _appBarRegistration = new AppBarRegistration(_window.Handle, _log);
            LogAppBarOutcome(_appBarRegistration.Register());

            _taskbarWatcher = new TaskbarWatcher(_taskbarReaderFactory(), ReadShownGauge, OnTaskbarLayout, _registry.UiPost, _log, _time, _taskbarWatcherPollIntervalMs);
            _taskbarWatcher.Start();
            _taskbarWatcher.Poke();
        }
    }

    // Explorer's internal appbar list is new after it restarts, so the old registration is gone with it;
    // called from TrayContext's TaskbarCreated listener, alongside (not instead of) the existing
    // ResetBackoff/Poke calls on the same event.
    private void OnTaskbarCreatedForAppBar()
    {
        if (_appBarRegistration is not { } registration)
        {
            return;
        }

        (StepOutcome removed, StepOutcome added) = registration.Reregister();
        LogAppBarOutcome(removed);
        LogAppBarOutcome(added);
    }

    // ABN_STATECHANGE and ABN_POSCHANGED ask only for an immediate re-measure. ABN_FULLSCREENAPP opening
    // hides the gauge at once through the controller (NotifyFullScreenApp), without waiting for a poll;
    // closing does not force a show, only a fresh read, since the taskbar's actual state still needs
    // re-reading. ABN_WINDOWARRANGE is not wired to a fast path in this build: TaskbarWatcher's own poll
    // still catches a rearranged taskbar, just not as immediately.
    private void OnAppBarNotification(object? sender, AppBarNotificationEventArgs e)
    {
        switch (e.Kind)
        {
            case Shell.ABN_STATECHANGE:
            case Shell.ABN_POSCHANGED:
                _taskbarWatcher?.Poke();
                break;

            case Shell.ABN_FULLSCREENAPP:
                bool opening = e.LParam != 0;
                _gaugeController?.NotifyFullScreenApp(opening);
                if (!opening)
                {
                    _taskbarWatcher?.Poke();
                }

                break;

            default:
                break;
        }
    }

    // Logs one StepOutcome from AppBarRegistration (Register, or one half of Reregister), the same way
    // LogVoiceOutcomes below logs a StepOutcome: Debug when it succeeded, Warn when it did not. A failure
    // here loses only the fast notification paths; TaskbarWatcher's own poll still runs.
    private void LogAppBarOutcome(StepOutcome outcome) =>
        _log.Write(outcome.Ok ? LogLevel.Debug : LogLevel.Warn, "AppBar: " + TrayReport.DescribeStep(outcome));

    // The widget gauge's current state, for tests: proving a fast-path notification (ABN_FULLSCREENAPP)
    // changed the controller's state synchronously, without reaching into TaskbarWatcher's own timing.
    internal GaugeState? WidgetGaugeStateForTest => _gaugeController?.State;

    // Whether the data pipeline and the case-open card are wired, for tests: proving they stay built while
    // any consumer wants them (WidgetSettings.WithWatcherRecomputed), independently of whether the gauge
    // itself is shown.
    internal bool WidgetDataPipelineWiredForTest => _widgetStatus is not null;
    internal bool WidgetCaseOpenCardWiredForTest => _caseOpenCardPresenter is not null;

    // Whether the gauge-anchored card is currently open, for tests: the same IsShown a real gauge click
    // (OnWidgetCardRequested) or close would change.
    internal bool WidgetCardIsShownForTest => _widgetCardPresenter?.IsShown ?? false;

    // The open card's own last-rendered ButtonEnabled, for tests: null when no card is open.
    internal bool? WidgetCardButtonEnabledForTest => _widgetCardPresenter?.CurrentModelForTest?.ButtonEnabled;

    // Drives the same path a real left click on the gauge does, LeftClickConnects off, without simulating
    // an actual click on the real GaugeWindow this pipeline builds.
    internal void RequestWidgetCardForTest() => OnWidgetCardRequested(this, EventArgs.Empty);

    // The Connect/Disconnect button on the widget card goes through the exact path the tray icon's own
    // left click and the menu's toggle item already use (Launch -> ToggleAsync -> BlockCoordinator), just
    // with the card placed above the gauge instead of at the cursor. Safe mode is unchanged: ToggleAsync's
    // own Safe decorators answer this call exactly as they do the tray icon's.
    internal void StartToggleFromWidget(CardPlace place)
    {
        long clickedAt = _tickCount();
        Launch("toggle", () => ToggleAsync(place, clickedAt, viaHotkey: false), place);
    }

    // The switch writes through TryUpdateSettings, the same settings-write path every menu item already
    // uses, so a save failure is reported on a card exactly as it is for them.
    internal bool TryUpdateSettingsFromWidget(string what, Action<EarshotSettings> mutate, CardPlace place) =>
        TryUpdateSettings(what, mutate, place);

    private GaugeControllerSettings ReadGaugeControllerSettings()
    {
        WidgetSettings widget = _registry.Settings.Current.Widget;
        return new GaugeControllerSettings(widget.ShowOnTaskbar, widget.LeftClickConnects);
    }

    // Called from TaskbarWatcher's own worker thread (the Func<ShownGauge?> its constructor takes). Reads
    // only the plain field RefreshShownGaugeForWorker maintains; never the GaugeWindow itself.
    private ShownGauge? ReadShownGauge() => _shownGaugeForWorker?.Value;

    // UI thread only. Recomputes the field the worker thread reads through ReadShownGauge, from the real
    // window's current Bounds and Handle: the only safe place to read either is here, since this runs on
    // the UI thread.
    private void RefreshShownGaugeForWorker()
    {
        _shownGaugeForWorker = _gaugeWindow is { IsDisposed: false } window && window.IsHandleCreated
            ? new ShownGaugeBox(new ShownGauge(window.Bounds, window.Handle))
            : null;
    }

    private GaugeWindow CreateGaugeSurface()
    {
        var window = new GaugeWindow(_log);
        _gaugeWindow = window;
        return window;
    }

    // Runs on the UI thread: TaskbarWatcher posts every result through registry.UiPost.
    private void OnTaskbarLayout(ITaskbarReader.Result result)
    {
        if (_gaugeController is not { } controller)
        {
            return;
        }

        if (result.Layout is { } layout)
        {
            _widgetLayoutDpi = layout.Dpi;
        }

        controller.OnLayout(result);
        RefreshShownGaugeForWorker();
        RenderGaugeIfShown();
    }

    // IWidgetStatus.Changed is documented as raised on the UI thread already (the data side posts it
    // through the same uiPost this class hands WidgetStatusService).
    private void OnWidgetStatusChanged(object? sender, EventArgs e)
    {
        if (_widgetStatus is null)
        {
            return;
        }

        _widgetSnapshotCache = _widgetStatus.Current;
        RenderGaugeIfShown();
        _widgetCardPresenter?.Refresh();
    }

    private void RenderGaugeIfShown()
    {
        if (_gaugeController?.State is GaugeState.Shown shown && _gaugeWindow is { IsDisposed: false } window && _widgetTheme is not null)
        {
            window.Render(_widgetSnapshotCache, _widgetLayoutDpi, shown.Bounds, _widgetTheme.Ink(), hover: false, MessageBoxFontFamily());
        }
    }

    private static string MessageBoxFontFamily()
    {
        using Font? font = SystemFonts.MessageBoxFont;
        return font?.Name ?? FontFamily.GenericSansSerif.Name;
    }

    // A left click on the gauge, LeftClickConnects off (the default): opens the dedicated three-column
    // card, above the gauge when it is shown, or near the cursor when the click came from the tray icon
    // fallback instead.
    private void OnWidgetCardRequested(object? sender, EventArgs e)
    {
        if (_widgetCardPresenter is not { } presenter)
        {
            return;
        }

        if (GaugeBoundsIfShown() is { } bounds)
        {
            presenter.RequestShow(bounds, bounds.Location);
        }
        else
        {
            presenter.RequestShow(gaugeBounds: null, _cursorPosition());
        }
    }

    // IWidgetStatus.CaseOpened, documented as already raised on the UI thread. The presenter itself runs
    // every gate (the setting, closing, the owner's own card already open, the notification state,
    // hand-back or a session end) before it shows anything; this only supplies where the gauge is, the same
    // rectangle OnWidgetCardRequested already uses for "above the gauge".
    private void OnCaseOpened(object? sender, CaseOpenedEventArgs e) => _caseOpenCardPresenter?.RequestShow(GaugeBoundsIfShown());

    // The gauge's own bounds when it is actually on screen with a handle, or null (the case-open card falls
    // back to NearTray; the widget card's own click path falls back to the cursor instead, since that path
    // only runs from a click that already has one). IsShown, not just IsHandleCreated: TransitionHidden
    // (GaugeController) hides the surface with SetWindowPos rather than disposing it, so a covered or
    // full-screen-hidden gauge still has a live handle and its last-shown Bounds, which is exactly the
    // stale rectangle a caller here must not anchor a new card on.
    private Rectangle? GaugeBoundsIfShown() =>
        _gaugeWindow is { IsDisposed: false, IsShown: true } window && window.IsHandleCreated ? window.Bounds : null;

    // For tests: the same answer OnWidgetCardRequested and OnCaseOpened get, without a card's own placement
    // math standing between the assertion and the fact being proved.
    internal Rectangle? GaugeBoundsIfShownForTest => GaugeBoundsIfShown();

    // Suspends the widget's BLE watcher. Called only once a shut-down or sleep hand-back has finished
    // (TrayContext.OnSessionEnding, OnPowerChanged): never before it, never concurrently with it.
    private void SuspendWidget()
    {
        if (_widgetSuspendForTest is { } hook)
        {
            hook();
            return;
        }

        if (_widgetStatus is null)
        {
            return;
        }

        try
        {
            _widgetStatus.Suspend();
        }
        catch (Exception ex)
        {
            _log.Error("Widget: Suspend threw.", ex);
        }
    }

    private void ResumeWidget()
    {
        if (_widgetResumeForTest is { } hook)
        {
            hook();
            return;
        }

        if (_widgetStatus is null)
        {
            return;
        }

        try
        {
            _widgetStatus.Resume();
        }
        catch (Exception ex)
        {
            _log.Error("Widget: Resume threw.", ex);
        }
    }

    // The ordinary Exit and WM_CLOSE path, not the hand-back path: stops the watcher thread, disposes the
    // gauge window and controller, and closes the widget's own BLE watcher. Called from
    // TrayContext.Close, on the UI thread.
    private void CloseWidget()
    {
        _autoPauseService?.Dispose();
        _autoPauseService = null;
        _lowBatteryAlertService?.Dispose();
        _lowBatteryAlertService = null;
        _widgetCardPresenter?.Dispose();
        _widgetCardPresenter = null;
        _caseOpenCardPresenter?.Dispose();
        _caseOpenCardPresenter = null;
        _taskbarWatcher?.Dispose();
        _taskbarWatcher = null;
        _appBarRegistration?.Dispose();
        _appBarRegistration = null;
        _gaugeController?.Dispose();
        _gaugeController = null;
        _gaugeWindow?.Dispose();
        _gaugeWindow = null;
        _shownGaugeForWorker = null;

        if (_widgetCloseForTest is { } hook)
        {
            hook();
            return;
        }

        if (_widgetStatus is { } status)
        {
            status.Changed -= OnWidgetStatusChanged;
            status.CaseOpened -= OnCaseOpened;
            try
            {
                status.Close();
            }
            catch (Exception ex)
            {
                _log.Error("Widget: Close threw.", ex);
            }
        }
    }

    // A settings change may have turned the watcher, the gauge, or both on or off, or changed
    // LeftClickConnects. Enabled (the OR of all four consumers, WidgetSettings.WithWatcherRecomputed) turning
    // on runs WireWidget, which is idempotent per field and rebuilds the data pipeline and the case-open card
    // whether or not the gauge itself is wanted; Enabled turning off leaves those objects in place exactly as
    // it always has (WidgetStatusService stops its own BLE source itself, reading the same setting).
    // ShowOnTaskbar turning on - including for the first time, from a start where the gauge began off -
    // builds the gauge's UI pipeline (WireGauge, also idempotent); turning it off stops the UI Automation
    // polling thread and the appbar registration, so a hidden gauge never leaves either running, and pokes
    // for an immediate re-check the rest of the time (a newly-off gauge, or a LeftClickConnects change,
    // disappears or takes effect within one poll rather than up to two seconds later).
    private void ApplyWidget()
    {
        WidgetSettings widget = _registry.Settings.Current.Widget;
        if (widget.Enabled)
        {
            WireWidget();
        }

        if (widget.ShowOnTaskbar)
        {
            _taskbarWatcher?.Poke();
        }
        else if (_taskbarWatcher is not null)
        {
            // The card this presenter owns is anchored above the gauge (OnWidgetCardRequested); once the
            // gauge itself is gone, an already-open card has nothing left to anchor to and must not linger.
            // The presenter and its card are not disposed here, only hidden: WireGauge only ever builds
            // them once (its own "if (_gaugeController is null)" guard), so the same instances are reused
            // if the gauge comes back.
            _widgetCardPresenter?.Hide();
            _gaugeController?.TurnOff();
            _taskbarWatcher.Dispose();
            _taskbarWatcher = null;
            _appBarRegistration?.Dispose();
            _appBarRegistration = null;
            _shownGaugeForWorker = null;
        }
    }
}

// An immutable holder for a ShownGauge, so TrayContext's _shownGaugeForWorker field can be volatile:
// ShownGauge is a readonly record struct, and a struct field cannot itself be declared volatile, but a
// reference to one small immutable object can be, and a reference assignment is what actually needs to be
// safe to publish from the UI thread to TaskbarWatcher's worker thread.
internal sealed class ShownGaugeBox(ShownGauge value)
{
    public ShownGauge Value { get; } = value;
}

// Adapts the tray's own NotifyIcon to ITrayIconVisibility, so GaugeController depends only on the
// interface it already declares.
internal sealed class NotifyIconVisibility(NotifyIcon icon) : ITrayIconVisibility
{
    // A test-only proof, the same pattern as GaugeWindow/UiaTaskbarReader/WinRtAdvertisementSource's own
    // ConstructionCount: how many times this adapter has made the real NotifyIcon visible. No execution is
    // ever allowed to do this for real (WidgetRealSurfaceGuardTests asserts it stays 0 for the whole run),
    // since a fake ITrayIconVisibility already proves everything GaugeController needs from this interface.
    internal static int RealVisibleTrueCount;

    public bool Visible
    {
        set
        {
            if (value)
            {
                Interlocked.Increment(ref RealVisibleTrueCount);
            }

            icon.Visible = value;
        }
    }
}
