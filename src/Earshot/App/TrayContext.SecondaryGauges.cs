using Earshot.Interop;
using Earshot.Popup;
using Earshot.Widget;

namespace Earshot.App;

// The gauges on the other displays' taskbars, for Gauge display set to All displays. The main display's gauge is the ordinary one
// (TrayContext.Widget.cs); each other display with a taskbar of its own gets a SecondaryGauge, added and removed by
// ReconcileSecondaryGauges as displays and taskbars come and go. Nothing here touches a device.
internal sealed partial class TrayContext
{
    private SecondaryGaugeSet? _secondaryGauges;
    private TrayIconVotes? _iconVotes;
    private readonly Func<SecondaryTaskbarReading> _secondaryTaskbarSource;

    // The main display's own scale, from its last read. The card is drawn at the scale of the display of the gauge it was opened
    // from, which for a card opened from another display's gauge is not this one.
    private int _mainLayoutDpi = CardPlacement96;
    private bool _cardOpenedFromSecondaryGauge;

    // Where the last card request was anchored, for tests: the gauge the click was on, or null for a request with no gauge.
    internal Rectangle? LastCardAnchorForTest { get; private set; }

    // How many gauges are on other displays' taskbars now, and their states, for tests.
    internal int SecondaryGaugeCountForTest => _secondaryGauges?.Count ?? 0;

    internal IReadOnlyCollection<SecondaryGauge> SecondaryGaugesForTest => _secondaryGauges?.Gauges ?? [];

    // Makes the gauges on the other displays match the setting and what is connected. UI thread.
    private void ReconcileSecondaryGauges()
    {
        WidgetSettings widget = _registry.Settings.Current.Widget;
        bool wanted = !_closing && widget.ShowOnTaskbar && GaugeDisplayChoice.IsAll(widget.GaugeDisplay) &&
            _gaugeController is not null && _iconVotes is not null;
        if (_secondaryGauges is null)
        {
            if (!wanted)
            {
                return;
            }

            _secondaryGauges = BuildSecondaryGauges(_iconVotes!);
        }

        _secondaryGauges.Reconcile(wanted);
    }

    private SecondaryGaugeSet BuildSecondaryGauges(TrayIconVotes votes)
    {
        var parts = new SecondaryGaugeParts(
            _taskbarReaderFactory,
            CreateSecondaryGaugeSurface,
            ReadGaugeControllerSettings,
            _log,
            _time,
            () => _gaugeCoverProbeFactory?.Invoke() ?? new WindowCoverProbe(),
            _registry.UiPost,
            () => _foregroundWindowProbe(_displaySource.Read().Displays),
            _taskbarWatcherPollIntervalMs,
            votes);
        var set = new SecondaryGaugeSet(parts, _displaySource, _secondaryTaskbarSource);
        set.CardRequested += (_, gauge) => OpenWidgetCardFor(gauge);
        set.ToggleRequested += (_, _) => StartToggle();
        set.MenuRequested += (_, e) => _menu.Strip.Show(e.Point);
        set.LaidOut += (_, gauge) => RenderSecondaryGauge(gauge);
        return set;
    }

    // Each gauge has its own window. The window the tray keeps as the main gauge's is never one of these.
    private IGaugeSurface CreateSecondaryGaugeSurface() =>
        _gaugeSurfaceFactory is { } factory
            ? factory()
            : new GaugeWindow(_log, order: () => _registry.Settings.Current.Widget.GaugeOrder);

    // A left click on another display's gauge: the card opens above that gauge, on its display, at that display's scale.
    private void OpenWidgetCardFor(SecondaryGauge gauge)
    {
        if (_widgetCardPresenter is not { } presenter || gauge.ShownBounds is not { } bounds)
        {
            return;
        }

        _caseOpenCardPresenter?.Hide();
        _widgetLayoutDpi = gauge.Dpi;
        _cardOpenedFromSecondaryGauge = true;
        LastCardAnchorForTest = bounds;
        presenter.RequestShow(bounds, bounds.Location, openedByKeyboard: false);
    }

    // The scale the card is drawn at when it is opened from the main gauge or the tray icon.
    private void UseMainDisplayScaleForCard()
    {
        _cardOpenedFromSecondaryGauge = false;
        _widgetLayoutDpi = _mainLayoutDpi;
    }

    // The main display's read gives its scale; a card that is open on another display's gauge keeps the scale it was opened at.
    private void NoteMainLayoutDpi(int dpi)
    {
        _mainLayoutDpi = dpi;
        if (!_cardOpenedFromSecondaryGauge || !(_widgetCardPresenter?.IsShown ?? false))
        {
            _widgetLayoutDpi = dpi;
        }
    }

    private void RenderSecondaryGauge(SecondaryGauge gauge)
    {
        if (_widgetTheme is not { } theme)
        {
            return;
        }

        WidgetSettings widget = _registry.Settings.Current.Widget;
        gauge.Render(
            _widgetSnapshotCache, _time.GetUtcNow(), new GaugeDisplaySettings(widget.LowBatteryThresholdPercent, widget.OtherDeviceLabel),
            theme.Ink(), TypeRamp.FamilyFor(TypeRole.Gauge));
    }

    private void RenderSecondaryGauges()
    {
        if (_secondaryGauges is not { } set || _widgetTheme is not { } theme)
        {
            return;
        }

        WidgetSettings widget = _registry.Settings.Current.Widget;
        set.Render(
            _widgetSnapshotCache, _time.GetUtcNow(), new GaugeDisplaySettings(widget.LowBatteryThresholdPercent, widget.OtherDeviceLabel),
            theme.Ink(), TypeRamp.FamilyFor(TypeRole.Gauge));
    }

    private void PokeSecondaryGauges(bool resetBackoff) => _secondaryGauges?.Poke(resetBackoff);
}
