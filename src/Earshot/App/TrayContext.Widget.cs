using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Icons;
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
    private int _widgetLayoutDpi = CardPlacement96;
    private WidgetSnapshot _widgetSnapshotCache = WidgetSnapshot.Empty(WidgetWatcherState.NotStarted, claimExists: false);

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
    // since the click routing below reaches into all three.
    private void WireWidget(TrayStartOptions options)
    {
        _widgetStatus = CompositionRoot.BuildWidget(_registry, () => _coordinator.BlockStatus, options.Time);
        if (_widgetStatus is null)
        {
            return;
        }

        _widgetStatus.Changed += OnWidgetStatusChanged;
        _widgetSnapshotCache = _widgetStatus.Current;
        _widgetStatus.Start();

        _widgetTheme = new ThemeReader(_log);
        var controller = new GaugeController(
            CreateGaugeSurface,
            new NotifyIconVisibility(_notifyIcon),
            ReadGaugeControllerSettings,
            _log,
            options.Time);
        controller.CardRequested += OnWidgetCardRequested;
        controller.ToggleRequested += (_, _) => StartToggle();
        controller.MenuRequested += (_, point) => _menu.Strip.Show(point);
        _gaugeController = controller;

        _taskbarWatcher = new TaskbarWatcher(new UiaTaskbarReader(), ReadShownGauge, OnTaskbarLayout, _registry.UiPost, _log, options.Time);
        _taskbarWatcher.Start();
        _taskbarWatcher.Poke();

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
        _widgetCardPresenter = new WidgetCardPresenter(() => new WidgetCard(_log), callbacks, _registry.UiPost, options.Time, _log);
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

    private ShownGauge? ReadShownGauge() =>
        _gaugeWindow is { IsDisposed: false } window && window.IsHandleCreated
            ? new ShownGauge(window.Bounds, window.Handle)
            : null;

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

        if (_gaugeWindow is { IsDisposed: false } window && window.IsHandleCreated)
        {
            presenter.RequestShow(window.Bounds, window.Bounds.Location);
        }
        else
        {
            presenter.RequestShow(gaugeBounds: null, _cursorPosition());
        }
    }

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
        _taskbarWatcher?.Dispose();
        _taskbarWatcher = null;
        _gaugeController?.Dispose();
        _gaugeController = null;
        _gaugeWindow?.Dispose();
        _gaugeWindow = null;

        if (_widgetCloseForTest is { } hook)
        {
            hook();
            return;
        }

        if (_widgetStatus is { } status)
        {
            status.Changed -= OnWidgetStatusChanged;
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

    // A settings change may have turned the gauge on or off, or changed LeftClickConnects: the controller
    // reads Settings.Current itself on every OnLayout, so the one thing needed here is an immediate
    // re-check, so a newly-off gauge disappears within one poll rather than up to two seconds later.
    private void ApplyWidget() => _taskbarWatcher?.Poke();
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
