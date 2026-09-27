using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Icons;
using Earshot.Popup;
using Earshot.Tray;
using Earshot.Widget;

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
    private GaugeController? _gaugeController;
    private TaskbarWatcher? _taskbarWatcher;
    private ThemeReader? _widgetTheme;
    private GaugeWindow? _gaugeWindow;
    private WidgetCardPresenter? _widgetCardPresenter;
    private CaseOpenCardPresenter? _caseOpenCardPresenter;
    private int _widgetLayoutDpi = CardPlacement96;
    private WidgetSnapshot _widgetSnapshotCache = WidgetSnapshot.Empty(WidgetWatcherState.NotStarted, claimExists: false);

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
    //     BLE source in response to the same Enabled setting changing later, so it is never torn down here;
    //   - the UI/gauge pipeline (_gaugeController, _widgetCardPresenter, _taskbarWatcher), which ApplyWidget
    //     tears down (bar the controller and presenter, cheap to keep) when the owner turns the widget off,
    //     so a disabled widget leaves no UI Automation polling thread running, and rebuilds when turned back
    //     on - including from a start where the widget began disabled and neither stage was ever wired.
    private void WireWidget()
    {
        if (_widgetStatus is null)
        {
            _widgetStatus = CompositionRoot.BuildWidget(_registry, () => _coordinator.BlockStatus, _time);
            if (_widgetStatus is null)
            {
                // Still disabled: nothing to wire yet. A later ApplyWidget call re-enters this method once
                // the owner turns the setting on.
                return;
            }

            _widgetStatus.Changed += OnWidgetStatusChanged;
            _widgetStatus.CaseOpened += OnCaseOpened;
            _widgetSnapshotCache = _widgetStatus.Current;
            _widgetStatus.Start();
        }

        if (_gaugeController is null)
        {
            _widgetTheme = new ThemeReader(_log);
            var controller = new GaugeController(
                CreateGaugeSurface,
                new NotifyIconVisibility(_notifyIcon),
                ReadGaugeControllerSettings,
                _log,
                _time);
            controller.CardRequested += OnWidgetCardRequested;
            controller.ToggleRequested += (_, _) => StartToggle();
            controller.MenuRequested += (_, point) => _menu.Strip.Show(point);
            _gaugeController = controller;

            var callbacks = new WidgetCardPresenterCallbacks(
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
                    "pause when a bud comes out (widget)", s => s.Widget = s.Widget with { AutoPause = on }, place));
            _widgetCardPresenter = new WidgetCardPresenter(() => new WidgetCard(_log), callbacks, _registry.UiPost, _time, _log);

            var caseOpenGate = new CaseOpenCardGate(
                Enabled: () => _registry.Settings.Current.Widget.CaseOpenCard,
                Closing: () => _closing,
                HandBackInProgress: () => _coordinator.HandBackInProgress,
                SessionEndInProgress: () => _coordinator.SessionEndInProgress);
            _caseOpenCardPresenter = new CaseOpenCardPresenter(
                () => new WidgetCard(_log, notice: true), callbacks, caseOpenGate, new SystemCardEnvironment(_log), _registry.UiPost, _time, _log);
        }

        if (_taskbarWatcher is null)
        {
            _taskbarWatcher = new TaskbarWatcher(new UiaTaskbarReader(), ReadShownGauge, OnTaskbarLayout, _registry.UiPost, _log, _time);
            _taskbarWatcher.Start();
            _taskbarWatcher.Poke();
        }
    }

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
        return new GaugeControllerSettings(widget.Enabled, widget.LeftClickConnects);
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
    // every gate in spec 7.6 (the setting, closing, already open, the notification state, hand-back or a
    // session end) before it shows anything; this only supplies where the gauge is, the same rectangle
    // OnWidgetCardRequested already uses for "above the gauge".
    private void OnCaseOpened(object? sender, CaseOpenedEventArgs e) => _caseOpenCardPresenter?.RequestShow(GaugeBoundsIfShown());

    // The gauge's own bounds when it is actually on screen with a handle, or null (the case-open card falls
    // back to NearTray; the widget card's own click path falls back to the cursor instead, since that path
    // only runs from a click that already has one).
    private Rectangle? GaugeBoundsIfShown() =>
        _gaugeWindow is { IsDisposed: false } window && window.IsHandleCreated ? window.Bounds : null;

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
        _widgetCardPresenter?.Dispose();
        _widgetCardPresenter = null;
        _caseOpenCardPresenter?.Dispose();
        _caseOpenCardPresenter = null;
        _taskbarWatcher?.Dispose();
        _taskbarWatcher = null;
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

    // A settings change may have turned the widget on or off, or changed LeftClickConnects. LeftClickConnects
    // and an already-running widget being turned off need only the controller's own Settings.Current re-check
    // (an immediate poke, so a newly-off gauge disappears within one poll rather than up to two seconds
    // later); Enabled turning off must also stop the UI Automation polling thread itself (WireWidget's
    // construction logic is otherwise never re-run, so a widget that started disabled would have no watcher
    // or controller for a later "turn it on" to act on); Enabled turning on - including for the first time,
    // from a start where the widget began disabled - re-runs WireWidget, which is idempotent per field.
    private void ApplyWidget()
    {
        if (_registry.Settings.Current.Widget.Enabled)
        {
            WireWidget();
            _taskbarWatcher?.Poke();
        }
        else if (_taskbarWatcher is not null)
        {
            _gaugeController?.TurnOff();
            _taskbarWatcher.Dispose();
            _taskbarWatcher = null;
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
    public bool Visible
    {
        set => icon.Visible = value;
    }
}
