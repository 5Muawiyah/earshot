using Earshot.App;
using Earshot.Contracts;
using Earshot.Tray;

namespace Earshot.Widget;

// What WidgetCardPresenter needs from TrayContext, injected so the presenter has no direct dependency on
// it (and so tests hand it fakes instead of building a real TrayContext). Every write goes through the
// exact path the tray icon's own click and the menu already use: RequestToggle is
// TrayContext.StartToggleFromWidget (Launch -> ToggleAsync -> BlockCoordinator, the same as the tray
// icon's left click), and SetAutoPause is TryUpdateSettingsFromWidget (the same TryUpdateSettings the menu
// items use). Nothing here is a new device path or a new settings-write path.
internal sealed record WidgetCardPresenterCallbacks(
    Func<WidgetSnapshot> CurrentSnapshot,
    Func<bool> AutoPauseOn,
    Func<ToggleIntent?> CurrentIntent,
    Func<bool> IsBusy,
    Func<int> Dpi,
    Func<Color> Ink,
    Func<bool> HighContrast,
    Func<string> OtherDeviceLabel,
    Action<CardPlace> RequestToggle,
    Action<bool, CardPlace> SetAutoPause);

// Owns the WidgetCard instance's lifecycle: creates it lazily, places it above the gauge (or a fallback
// point when the gauge is hidden), shows it activated, re-renders on IWidgetStatus.Changed, refreshes the
// read line every 30 s while open, and implements two related but distinct toggle-close rules. A gauge
// click while the card is already shown closes it directly (RequestShowOnUiThread's own IsShown check):
// the gauge answers WM_MOUSEACTIVATE with MA_NOACTIVATE, so it never deactivates the card, and this is the
// only way a second gauge click can close it at all. Separately, a left click on the gauge within
// SystemInformation.DoubleClickTime of the card closing through a genuine deactivation (losing focus to some
// other real window) does not reopen it, so dismissing the card by clicking elsewhere and then happening to
// also click the gauge does not immediately flicker it back open.
//
// UI thread only from the outside; every public method posts through uiPost so a caller on any thread is
// safe, matching CardPresenter's own contract.
internal sealed class WidgetCardPresenter : IDisposable
{
    public static readonly TimeSpan ReadLineRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly Func<WidgetCard> _createCard;
    private readonly WidgetCardPresenterCallbacks _callbacks;
    private readonly Action<Action> _uiPost;
    private readonly TimeProvider _time;
    private readonly ILog _log;

    private WidgetCard? _card;
    private ITimer? _refreshTimer;
    private CardPlace _place = CardPlace.NearTray;
    private long? _closedByDeactivateAtTimestamp;
    private bool _disposed;

    public WidgetCardPresenter(Func<WidgetCard> createCard, WidgetCardPresenterCallbacks callbacks, Action<Action> uiPost, TimeProvider time, ILog log)
    {
        ArgumentNullException.ThrowIfNull(createCard);
        ArgumentNullException.ThrowIfNull(callbacks);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _createCard = createCard;
        _callbacks = callbacks;
        _uiPost = uiPost;
        _time = time;
        _log = log;
    }

    // True while the card is on screen. For tests; the UI thread only.
    internal bool IsShown => _card is { IsDisposed: false, Visible: true };

    // For tests: the reason WidgetCardPresenter believes the card last closed for, or null.
    internal bool HasPendingToggleCloseWindow => _closedByDeactivateAtTimestamp is not null;

    // Shows the card above gaugeBounds, or above a zero-size rectangle at fallbackPoint when the gauge is
    // hidden and the click came from the tray icon fallback instead (see the report for why this is a
    // simpler fallback than the gauge case, not the full click-anchor rule the tray's other cards use). A
    // click within SystemInformation.DoubleClickTime of the card's last close-through-deactivation is the
    // second half of that gesture: it closes, it does not reopen.
    public void RequestShow(Rectangle? gaugeBounds, Point fallbackPoint) =>
        _uiPost(() => RequestShowOnUiThread(gaugeBounds, fallbackPoint));

    // Forces the card to hide, for example when a settings change turns the widget off.
    public void Hide() => _uiPost(HideOnUiThread);

    // Re-renders the card with the latest snapshot, if it is on screen. IWidgetStatus.Changed is documented
    // as already raised on the UI thread, but this still posts, so a test or a future caller on another
    // thread is safe too.
    public void Refresh() => _uiPost(RefreshOnUiThread);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopRefreshTimer();
        if (_card is not null)
        {
            _card.ToggleRequested -= OnToggleRequested;
            _card.AutoPauseChanged -= OnAutoPauseChanged;
            _card.CloseRequested -= OnCardClosed;
            _card.Dispose();
            _card = null;
        }
    }

    private void RequestShowOnUiThread(Rectangle? gaugeBounds, Point fallbackPoint)
    {
        if (_disposed)
        {
            return;
        }

        if (_card is { IsDisposed: false, Visible: true })
        {
            // The gauge answers WM_MOUSEACTIVATE with MA_NOACTIVATE (GaugeWindow), so a click on it never
            // deactivates this card - WidgetCard.OnDeactivate, the only other way it closes itself, never
            // fires from a gauge click. A second gauge click while the card is already shown is the owner
            // asking to close it, the same gesture as a second click on the volume flyout's own icon: closed
            // directly, not through the deactivate-close double-click window below, which is for a different
            // trigger entirely (losing focus to some other real window, then clicking the gauge again).
            HideOnUiThread();
            return;
        }

        TimeSpan doubleClick = TimeSpan.FromMilliseconds(Math.Max(0, SystemInformation.DoubleClickTime));
        if (_closedByDeactivateAtTimestamp is { } closedAt && _time.GetElapsedTime(closedAt) <= doubleClick)
        {
            // The second half of the volume-icon gesture: this click is the one that closed it. Consume
            // the window so a third click behaves like a first.
            _closedByDeactivateAtTimestamp = null;
            _log.Write(LogLevel.Debug, "Widget card: gauge click within the double-click window of its own deactivate-close; not reopened.");
            return;
        }

        Rectangle anchor = gaugeBounds ?? new Rectangle(fallbackPoint, Size.Empty);
        _place = CardPlace.AtClick(gaugeBounds is { } gauge ? new Point(gauge.X + (gauge.Width / 2), gauge.Y) : fallbackPoint);

        WidgetCard card = EnsureCard();
        WidgetCardModel model = BuildModel();
        card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
        card.Render(model, _callbacks.Dpi());

        Size cardSize = card.ClientSize;
        Rectangle workArea = Screen.FromPoint(anchor.Location).WorkingArea;
        Rectangle bounds = WidgetCardPlacement.Above(anchor, cardSize, workArea, _callbacks.Dpi());

        card.Bounds = bounds;
        card.Show();
        card.Activate();
        StartRefreshTimer();
    }

    private void HideOnUiThread()
    {
        if (_card is { IsDisposed: false, Visible: true } card)
        {
            card.Hide();
        }

        StopRefreshTimer();
    }

    private void RefreshOnUiThread()
    {
        if (_card is not { IsDisposed: false, Visible: true } card)
        {
            return;
        }

        card.Render(BuildModel(), _callbacks.Dpi());
    }

    private WidgetCardModel BuildModel() => BuildModel(_callbacks, _time);

    // Shared with CaseOpenCardPresenter, which renders the exact same three columns from the exact same
    // callbacks for its own WidgetCard(notice: true) instance; only the Where line differs, and WidgetCard
    // itself overrides that for any notice-mode instance regardless of what this model carries.
    internal static WidgetCardModel BuildModel(WidgetCardPresenterCallbacks callbacks, TimeProvider time)
    {
        WidgetSnapshot snapshot = callbacks.CurrentSnapshot();
        ToggleIntent? intent = callbacks.CurrentIntent();
        bool busy = callbacks.IsBusy();
        return new WidgetCardModel(
            snapshot,
            AutoPauseOn: callbacks.AutoPauseOn(),
            ShowSwitch: snapshot.AutoPauseAvailable,
            ConnectIntent: intent?.Connect ?? true,
            ButtonEnabled: !busy && intent is not null,
            OtherDeviceLabel: callbacks.OtherDeviceLabel(),
            Now: time.GetUtcNow());
    }

    private WidgetCard EnsureCard()
    {
        if (_card is { IsDisposed: false } card)
        {
            return card;
        }

        if (_card is not null)
        {
            _card.ToggleRequested -= OnToggleRequested;
            _card.AutoPauseChanged -= OnAutoPauseChanged;
            _card.CloseRequested -= OnCardClosed;
        }

        _card = _createCard();
        _card.ToggleRequested += OnToggleRequested;
        _card.AutoPauseChanged += OnAutoPauseChanged;
        _card.CloseRequested += OnCardClosed;
        return _card;
    }

    private void OnToggleRequested(object? sender, EventArgs e) => _callbacks.RequestToggle(_place);

    private void OnAutoPauseChanged(object? sender, bool on) => _callbacks.SetAutoPause(on, _place);

    private void OnCardClosed(object? sender, WidgetCardCloseReason reason)
    {
        if (reason == WidgetCardCloseReason.Deactivated)
        {
            _closedByDeactivateAtTimestamp = _time.GetTimestamp();
        }

        StopRefreshTimer();
    }

    private void StartRefreshTimer()
    {
        StopRefreshTimer();
        _refreshTimer = _time.CreateTimer(_ => _uiPost(RefreshOnUiThread), null, ReadLineRefreshInterval, ReadLineRefreshInterval);
    }

    private void StopRefreshTimer()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }
}
