using Earshot.App;
using Earshot.Contracts;
using Earshot.Popup;
using Earshot.Tray;

namespace Earshot.Widget;

// What WidgetCardPresenter needs from TrayContext, injected so the presenter has no direct dependency on
// it (and so tests hand it fakes instead of building a real TrayContext). Every write goes through the
// exact path the tray icon's own click and the menu already use: RequestToggle is
// TrayContext.StartToggleFromWidget (Launch -> ToggleAsync -> BlockCoordinator, the same as the tray
// icon's left click), SetAutoPause is TryUpdateSettingsFromWidget (the same TryUpdateSettings the menu
// items use). The battery set-up is not a device action: ListenForSetup and CompleteSetup are the widget
// status service's own, and only listen to advertisements and write the owner's own records.
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
    Action<bool, CardPlace> SetAutoPause,
    Func<CancellationToken, Task<BatterySetupListen>> ListenForSetup,
    Func<BatterySetupListen, BatterySetupPicks, BatterySetupResult> CompleteSetup);

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
// It also drives the battery set-up on the card: listening (the service's listen, with a spinner that turns
// on a 100 ms timer only while listening), the pickers, the result. A set-up page stays open when the card
// loses focus and a second gauge click does not close it; Escape, Cancel, Back and Done do, and each cancels a
// listen that is still running.
//
// UI thread only from the outside; every public method posts through uiPost so a caller on any thread is
// safe, matching CardPresenter's own contract.
internal sealed class WidgetCardPresenter : IDisposable
{
    public static readonly TimeSpan ReadLineRefreshInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan SpinnerInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<WidgetCard> _createCard;
    private readonly WidgetCardPresenterCallbacks _callbacks;
    private readonly Action<Action> _uiPost;
    private readonly TimeProvider _time;
    private readonly ILog _log;

    private WidgetCard? _card;
    private ITimer? _refreshTimer;
    private ITimer? _spinnerTimer;
    private CardPlace _place = CardPlace.NearTray;
    private long? _closedByDeactivateAtTimestamp;
    private bool _disposed;

    // The set-up in progress. _view is Main whenever there is none.
    private WidgetCardView _view = WidgetCardView.Main;
    private SetupViewModel? _setup;
    private CancellationTokenSource? _listenCts;
    private BatterySetupListen? _listen;
    private BatterySetupPicks _picks = BatterySetupPicks.Default;
    private int _spinnerFrame;

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

    // The card's own last-rendered model, for tests: proving a Refresh() actually reached the real card
    // Render drew, rather than only that Refresh() itself was called.
    internal WidgetCardModel? CurrentModelForTest => _card?.Model;

    // For tests: the reason WidgetCardPresenter believes the card last closed for, or null.
    internal bool HasPendingToggleCloseWindow => _closedByDeactivateAtTimestamp is not null;

    // For tests: which page the presenter has the card on, and whether the spinner timer is running.
    internal WidgetCardView ViewForTest => _view;

    internal bool SpinnerRunningForTest => _spinnerTimer is not null;

    internal bool ListenTokenCancelledForTest => _listenCts?.IsCancellationRequested ?? false;

    // Shows the card above gaugeBounds, or above a zero-size rectangle at fallbackPoint when the gauge is
    // hidden and the click came from the tray icon fallback instead: a simpler fallback than the gauge
    // case, since the tray icon has no free taskbar rectangle of its own to anchor above, unlike the full
    // click-anchor rule the tray's other cards use. A click within SystemInformation.DoubleClickTime of the
    // card's last close-through-deactivation is the second half of that gesture: it closes, it does not
    // reopen.
    public void RequestShow(Rectangle? gaugeBounds, Point fallbackPoint) =>
        _uiPost(() => RequestShowOnUiThread(gaugeBounds, fallbackPoint));

    // Opens the card at the first set-up page (or switches an open card to it) and starts listening. From the
    // tray menu's "Set up battery", placed the same way a gauge click places the card.
    public void RequestSetup(Rectangle? gaugeBounds, Point fallbackPoint) =>
        _uiPost(() => RequestSetupOnUiThread(gaugeBounds, fallbackPoint));

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
        CancelSetup();
        if (_card is not null)
        {
            Unsubscribe(_card);
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
            if (_view != WidgetCardView.Main)
            {
                // A set-up page is open: a second gauge click does not close it, so a stray click on the way
                // to the owner's case does not lose the step.
                return;
            }

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

        ShowAt(gaugeBounds, fallbackPoint);
    }

    private void RequestSetupOnUiThread(Rectangle? gaugeBounds, Point fallbackPoint)
    {
        if (_disposed)
        {
            return;
        }

        if (_card is { IsDisposed: false, Visible: true })
        {
            StartListening();
            return;
        }

        _view = WidgetCardView.SetupListening;
        _setup = SetupViewModel.Listening();
        ShowAt(gaugeBounds, fallbackPoint);
        StartListening();
    }

    private void ShowAt(Rectangle? gaugeBounds, Point fallbackPoint)
    {
        Rectangle anchor = gaugeBounds ?? new Rectangle(fallbackPoint, Size.Empty);
        _place = CardPlace.AtClick(gaugeBounds is { } gauge ? new Point(gauge.X + (gauge.Width / 2), gauge.Y) : fallbackPoint);

        WidgetCard card = EnsureCard();
        card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
        card.Render(BuildModel(), _callbacks.Dpi());
        card.Bounds = PlaceAbove(card, anchor);
        card.Show();
        card.Activate();
        StartRefreshTimer();
    }

    private Rectangle PlaceAbove(WidgetCard card, Rectangle anchor)
    {
        Size cardSize = card.ClientSize;
        Rectangle workArea = Screen.FromPoint(anchor.Location).WorkingArea;
        return WidgetCardPlacement.Above(anchor, cardSize, workArea, _callbacks.Dpi());
    }

    private void HideOnUiThread()
    {
        CancelSetup();
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

    private WidgetCardModel BuildModel() => BuildModel(_callbacks, _time, _view, _setup);

    // Shared with CaseOpenCardPresenter, which renders the exact same three columns from the exact same
    // callbacks for its own WidgetCard(notice: true) instance; only the Where line differs, and WidgetCard
    // itself overrides that for any notice-mode instance regardless of what this model carries. A notice is
    // always the main view.
    internal static WidgetCardModel BuildModel(WidgetCardPresenterCallbacks callbacks, TimeProvider time) =>
        BuildModel(callbacks, time, WidgetCardView.Main, null);

    private static WidgetCardModel BuildModel(WidgetCardPresenterCallbacks callbacks, TimeProvider time, WidgetCardView view, SetupViewModel? setup)
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
            Now: time.GetUtcNow(),
            ShowSetupButton: snapshot.Left.Percent is null && snapshot.Right.Percent is null && snapshot.Case.Percent is null,
            View: view,
            Setup: setup);
    }

    private WidgetCard EnsureCard()
    {
        if (_card is { IsDisposed: false } card)
        {
            return card;
        }

        if (_card is not null)
        {
            Unsubscribe(_card);
        }

        _card = _createCard();
        _card.ToggleRequested += OnToggleRequested;
        _card.AutoPauseChanged += OnAutoPauseChanged;
        _card.SetupRequested += OnSetupRequested;
        _card.SetupActionRequested += OnSetupAction;
        _card.SetupPicksChanged += OnSetupPicksChanged;
        _card.CloseRequested += OnCardClosed;
        return _card;
    }

    private void Unsubscribe(WidgetCard card)
    {
        card.ToggleRequested -= OnToggleRequested;
        card.AutoPauseChanged -= OnAutoPauseChanged;
        card.SetupRequested -= OnSetupRequested;
        card.SetupActionRequested -= OnSetupAction;
        card.SetupPicksChanged -= OnSetupPicksChanged;
        card.CloseRequested -= OnCardClosed;
    }

    private void OnToggleRequested(object? sender, EventArgs e) => _callbacks.RequestToggle(_place);

    private void OnAutoPauseChanged(object? sender, bool on) => _callbacks.SetAutoPause(on, _place);

    // The main view's "Set up battery" button: the same card turns into the first set-up page.
    private void OnSetupRequested(object? sender, EventArgs e) => StartListening();

    private void OnSetupPicksChanged(object? sender, BatterySetupPicks picks) => _picks = picks;

    private void OnSetupAction(object? sender, SetupAction action)
    {
        switch (action)
        {
            case SetupAction.Cancel:
            case SetupAction.Back:
            case SetupAction.Done:
                CloseSetup();
                break;
            case SetupAction.TryAgain:
                StartListening();
                break;
            case SetupAction.Save:
                SaveSetup();
                break;
        }
    }

    private void OnCardClosed(object? sender, WidgetCardCloseReason reason)
    {
        if (reason == WidgetCardCloseReason.Deactivated)
        {
            _closedByDeactivateAtTimestamp = _time.GetTimestamp();
        }

        StopRefreshTimer();
    }

    // ---- The set-up

    private void StartListening()
    {
        CancelListenOnly();
        _view = WidgetCardView.SetupListening;
        _spinnerFrame = 0;
        _setup = SetupViewModel.Listening();
        _listen = null;
        var cts = new CancellationTokenSource();
        _listenCts = cts;
        StartSpinnerTimer();
        RenderSetup();

        // The listen runs off the UI thread; its result comes back through uiPost. A result for a listen that
        // has since been cancelled or replaced is dropped.
        _ = ListenAsync(cts);
    }

    private async Task ListenAsync(CancellationTokenSource cts)
    {
        BatterySetupListen result;
        try
        {
            result = await _callbacks.ListenForSetup(cts.Token);
        }
        catch (Exception ex)
        {
            // Raised by nothing the flow does on purpose; kept visible rather than lost on an unobserved task.
            _log.Error("Widget card: the battery set-up listen failed unexpectedly.", ex);
            DateTimeOffset now = _time.GetUtcNow();
            result = new BatterySetupListen(
                BatterySetupListenStatus.WatcherNotStarted, WidgetCopy.SetupBluetoothOff, Candidate: null, [], now, now, 0, 0);
        }

        _uiPost(() => OnListenCompleted(cts, result));
    }

    private void OnListenCompleted(CancellationTokenSource cts, BatterySetupListen result)
    {
        if (_disposed || !ReferenceEquals(cts, _listenCts) || cts.IsCancellationRequested || _view != WidgetCardView.SetupListening)
        {
            return;
        }

        StopSpinnerTimer();
        switch (result.Status)
        {
            case BatterySetupListenStatus.Found:
            case BatterySetupListenStatus.ShortFormOnly:
                _listen = result;
                _picks = BatterySetupPicks.Default;
                _view = WidgetCardView.SetupPick;
                _setup = SetupViewModel.Pick(_picks);
                RenderSetup();
                break;
            case BatterySetupListenStatus.Cancelled:
                CloseSetup();
                break;
            default:
                _view = WidgetCardView.SetupFailed;
                _setup = SetupViewModel.Failed(result.Status);
                RenderSetup();
                break;
        }
    }

    private void SaveSetup()
    {
        if (_listen is not { } listen || _view != WidgetCardView.SetupPick)
        {
            return;
        }

        BatterySetupResult result = _callbacks.CompleteSetup(listen, _picks);
        _view = WidgetCardView.SetupDone;
        _setup = SetupViewModel.Done(result.Status);
        RenderSetup();
    }

    // Back, Cancel, Done and Escape: the set-up is over. A listen still running is cancelled and nothing is
    // written; the card closes, and the main view shows whatever the snapshot holds when it is next opened.
    private void CloseSetup()
    {
        CancelSetup();
        if (_card is { IsDisposed: false, Visible: true } card)
        {
            card.Hide();
        }

        StopRefreshTimer();
    }

    private void CancelSetup()
    {
        CancelListenOnly();
        _view = WidgetCardView.Main;
        _setup = null;
        _listen = null;
    }

    private void CancelListenOnly()
    {
        StopSpinnerTimer();
        if (_listenCts is { } cts)
        {
            _listenCts = null;
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void RenderSetup()
    {
        if (_card is not { IsDisposed: false, Visible: true } card)
        {
            return;
        }

        Rectangle before = card.Bounds;
        card.Render(BuildModel(), _callbacks.Dpi());

        // The page's height changes with the step; the card keeps its bottom edge where it was, so it grows
        // upward from the gauge instead of into the taskbar.
        Size size = card.ClientSize;
        if (size != before.Size)
        {
            Rectangle workArea = Screen.FromPoint(before.Location).WorkingArea;
            var resized = new Rectangle(before.X, before.Bottom - size.Height, size.Width, size.Height);
            card.Bounds = CardPlacement.Clamp(resized, workArea);
        }
    }

    private void AdvanceSpinner()
    {
        if (_view != WidgetCardView.SetupListening || _setup is null)
        {
            return;
        }

        _spinnerFrame = (_spinnerFrame + 1) % SetupViewModel.SpinnerFrames;
        _setup = _setup with { SpinnerFrame = _spinnerFrame };
        _card?.SetSpinnerFrame(_spinnerFrame);
    }

    private void StartSpinnerTimer()
    {
        StopSpinnerTimer();
        _spinnerTimer = _time.CreateTimer(_ => _uiPost(AdvanceSpinner), null, SpinnerInterval, SpinnerInterval);
    }

    private void StopSpinnerTimer()
    {
        _spinnerTimer?.Dispose();
        _spinnerTimer = null;
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
