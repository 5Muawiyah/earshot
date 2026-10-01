using Earshot.Interop;
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
// items use).
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
    Func<GaugePosition>? GaugePosition = null,   // where the gauge sits, so the card follows it; the right end when null
    Func<CancellationToken, Task<BatteryRefreshOutcome>>? RefreshBattery = null)   // reads the battery again; no refresh icon works when null
{
    public GaugePosition CurrentGaugePosition => GaugePosition?.Invoke() ?? Earshot.Widget.GaugePosition.RightEnd;
}

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
// It also opens the settings page (from the gear) and the update page (from the update line, or from the settings
// row that checks): the values come from the host each time the card is drawn, every change goes through the host,
// and the update page follows the update flow's own state. The update line's button is the one place a download
// is started from.
//
// Every battery part is handed to the card with whether it is fresh (BatteryFreshness): the card draws a part
// that is not fresh greyed, and says its age.
//
// UI thread only from the outside; every public method posts through uiPost so a caller on any thread is
// safe, matching CardPresenter's own contract.
internal sealed partial class WidgetCardPresenter : IDisposable
{
    // Short enough that a part greys within 5 s of crossing BatteryFreshness.FreshWindow.
    public static readonly TimeSpan ReadLineRefreshInterval = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan SpinnerInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<WidgetCard> _createCard;
    private readonly WidgetCardPresenterCallbacks _callbacks;

    // The scale a card opened from another display's gauge is drawn at, kept for as long as it is open; null for a card that follows
    // the host's.
    private int? _cardDpi;

    private int CardDpi => _cardDpi ?? _callbacks.Dpi();
    private readonly Action<Action> _uiPost;
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly IWidgetCardHost? _host;
    private readonly Func<bool>? _animationsEnabled;
    private readonly Func<Rectangle, Rectangle> _workAreaFor;

    private WidgetCard? _card;
    private ITimer? _refreshTimer;
    private ITimer? _spinnerTimer;
    private CardPlace _place = CardPlace.NearTray;
    private long? _closedByDeactivateAtTimestamp;
    private bool _disposed;

    private WidgetCardView _view = WidgetCardView.Main;
    private int _spinnerFrame;

    // Where Back on the update page goes: the settings page when it was opened from there, else the main view.
    private WidgetCardView _updateFrom = WidgetCardView.Main;

    // The reason a typed shortcut was refused, shown on its row until the next change or until the page closes.
    private (CardShortcut Shortcut, string Reason)? _shortcutNote;

    public WidgetCardPresenter(
        Func<WidgetCard> createCard, WidgetCardPresenterCallbacks callbacks, Action<Action> uiPost, TimeProvider time, ILog log,
        IWidgetCardHost? host = null, Func<bool>? animationsEnabled = null, Func<Rectangle, Rectangle>? workAreaFor = null)
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
        _host = host;
        _animationsEnabled = animationsEnabled;

        // The work area of the display an anchor is on. The tray reads the real displays; a test hands in a fixed area so a
        // page's placement does not depend on the screen the tests happen to run on.
        _workAreaFor = workAreaFor ?? SystemDisplaySource.WorkAreaFor;
        if (_host is not null)
        {
            _host.UpdateChanged += OnHostUpdateChanged;
        }
    }

    // True while the card is on screen. For tests; the UI thread only.
    internal bool IsShown => _card is { IsDisposed: false, Visible: true, IsExiting: false };

    // The card's own last-rendered model, for tests: proving a Refresh() actually reached the real card
    // Render drew, rather than only that Refresh() itself was called.
    internal WidgetCardModel? CurrentModelForTest => _card?.Model;

    // For tests: the reason WidgetCardPresenter believes the card last closed for, or null.
    internal bool HasPendingToggleCloseWindow => _closedByDeactivateAtTimestamp is not null;

    // For tests: whether the card's keyboard focus cue is on, and whether the card was given motion. Null until a card exists.
    internal bool? FocusCueVisibleForTest => _card?.FocusCueVisible;

    internal bool? HasMotionForTest => _card?.HasMotion;

    // For tests: which page the presenter has the card on, and whether the spinner timer is running.
    internal WidgetCardView ViewForTest => _view;

    // Opens the settings page on the card that is showing, as the gear does. UI thread.
    internal void OpenSettingsForTest() => OnSettingsRequested(this, EventArgs.Empty);

    internal bool SpinnerRunningForTest => _spinnerTimer is not null;

    // For tests: how wide the card is, which follows the scale it is drawn at. Zero until a card exists.
    internal int CardWidthForTest => _card?.ClientSize.Width ?? 0;

    // For tests: where the card sits, or is moving to. Null until a card exists.
    internal Rectangle? CardRestBoundsForTest => _card is { IsDisposed: false } card ? card.RestBounds : null;

    // Shows the card above gaugeBounds, or above a zero-size rectangle at fallbackPoint when the gauge is
    // hidden and the click came from the tray icon fallback instead: a simpler fallback than the gauge
    // case, since the tray icon has no free taskbar rectangle of its own to anchor above, unlike the full
    // click-anchor rule the tray's other cards use. A click within SystemInformation.DoubleClickTime of the
    // card's last close-through-deactivation is the second half of that gesture: it closes, it does not
    // reopen.
    //
    // openedByKeyboard is true when the open came from the keyboard, so the focus visual shows from the start;
    // a click opens the card with none.
    //
    // dpi is the scale of the display the gauge is on, for a gauge that is not the main display's. The card is drawn at it for as
    // long as it stays open, whatever the main gauge reads in the meantime. Null draws the card at the scale the host gives.
    public void RequestShow(Rectangle? gaugeBounds, Point fallbackPoint, bool openedByKeyboard = false, int? dpi = null) =>
        _uiPost(() => RequestShowOnUiThread(gaugeBounds, fallbackPoint, openedByKeyboard, dpi));

    // Opens the card at the update page (or switches an open card to it), placed the same way a gauge click places the
    // card. From the tray menu's "Check for updates" once a newer version is found, so the Update button is there.
    public void RequestUpdatePage(Rectangle? gaugeBounds, Point fallbackPoint) =>
        _uiPost(() => RequestUpdatePageOnUiThread(gaugeBounds, fallbackPoint));

    // Forces the card to hide, for example when a settings change turns the widget off.
    public void Hide() => _uiPost(HideOnUiThread);

    // The system's look changed (the theme, the accent, the text size, transparency or a high-contrast theme): the
    // card on screen takes the theme again, reads the look and is laid out and drawn again, keeping its bottom edge.
    // A card that is not on screen reads the look when it is next shown.
    public void ReapplyLook() => _uiPost(ReapplyLookOnUiThread);

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
        if (_host is not null)
        {
            _host.UpdateChanged -= OnHostUpdateChanged;
        }

        StopRefreshTimer();
        StopSpinnerTimer();
        EndBatteryRefresh();
        if (_card is not null)
        {
            Unsubscribe(_card);
            _card.Dispose();
            _card = null;
        }
    }

    private void RequestShowOnUiThread(Rectangle? gaugeBounds, Point fallbackPoint, bool openedByKeyboard, int? dpi)
    {
        if (_disposed)
        {
            return;
        }

        if (_card is { IsDisposed: false, Visible: true, IsExiting: false })
        {
            if (_view == WidgetCardView.Settings)
            {
                // The settings page closes on a second gauge click, as the main view does.
                HideOnUiThread();
                return;
            }

            if (_view != WidgetCardView.Main)
            {
                // The update page is open: a second gauge click does not close it, so a stray click does not lose
                // a download that is running.
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

        ShowAt(gaugeBounds, fallbackPoint, openedByKeyboard, dpi);
    }

    private void RequestUpdatePageOnUiThread(Rectangle? gaugeBounds, Point fallbackPoint)
    {
        if (_disposed || _host is null)
        {
            return;
        }

        _updateFrom = WidgetCardView.Main;
        _view = WidgetCardView.Update;
        _spinnerFrame = 0;
        if (_card is { IsDisposed: false, Visible: true })
        {
            RenderKeepingBottom();
        }
        else
        {
            ShowAt(gaugeBounds, fallbackPoint);
        }

        SyncSpinner();
    }

    private void ShowAt(Rectangle? gaugeBounds, Point fallbackPoint, bool openedByKeyboard = false, int? dpi = null)
    {
        _cardDpi = dpi;
        Rectangle anchor = gaugeBounds ?? new Rectangle(fallbackPoint, Size.Empty);
        _place = CardPlace.AtClick(gaugeBounds is { } gauge ? new Point(gauge.X + (gauge.Width / 2), gauge.Y) : fallbackPoint);

        WidgetCard card = EnsureCard();
        card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
        card.MaxHeight = WidgetCardPlacement.MaxHeight(anchor, _workAreaFor(anchor), CardDpi);
        card.Render(BuildModel(), CardDpi);
        Rectangle rest = PlaceAbove(card, anchor);
        card.ResetFocusCue(openedByKeyboard);
        card.PresentAnimated(rest, CardMotion.TravelFor(gaugeBounds ?? Rectangle.Empty, _workAreaFor(anchor), CardDpi));
        card.Activate();
        StartRefreshTimer();
        SyncRefreshSpinner();
    }

    private Rectangle PlaceAbove(WidgetCard card, Rectangle anchor)
    {
        Size cardSize = card.ClientSize;
        Rectangle workArea = _workAreaFor(anchor);
        return WidgetCardPlacement.Above(anchor, cardSize, workArea, CardDpi, _callbacks.CurrentGaugePosition);
    }

    private void HideOnUiThread()
    {
        _view = WidgetCardView.Main;
        _shortcutNote = null;
        StopSpinnerTimer();
        ForgetRefreshOutcome();
        if (_card is { IsDisposed: false, Visible: true } card)
        {
            card.HideAnimated();
        }

        StopRefreshTimer();
    }

    private void ReapplyLookOnUiThread()
    {
        if (_disposed || _card is not { IsDisposed: false, Visible: true, IsExiting: false } card)
        {
            return;
        }

        card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
        RenderKeepingBottom();
        _lookReapplies++;
    }

    private int _lookReapplies;

    // How many times a shown card was given the look again, for tests.
    internal int LookReappliesForTest => _lookReapplies;

    private void RefreshOnUiThread()
    {
        RenderKeepingBottom();
        SyncSpinner();
    }

    private WidgetCardModel BuildModel()
    {
        SetupViewModel? setup = _view == WidgetCardView.Update ? UpdatePage() : null;
        WidgetCardModel model = BuildModel(_callbacks, _time, _view, setup);
        return model with
        {
            UpdateVersion = _host?.AvailableUpdateVersion(),
            Settings = _view == WidgetCardView.Settings ? ReadSettings() : null,
            Refresh = RefreshViewForModel(),
        };
    }

    // The update page, from the update flow's own state: the same words the tray's messages use.
    private SetupViewModel UpdatePage() => _host!.UpdatePage(_spinnerFrame);

    private CardSettingsValues ReadSettings()
    {
        CardSettingsValues values = _host!.ReadSettings();
        if (_shortcutNote is { } note)
        {
            values = note.Shortcut == CardShortcut.Connect
                ? values with { ConnectFailure = note.Reason }
                : values with { DisconnectFailure = note.Reason };
        }

        return values;
    }

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
            View: view,
            Setup: setup)
        {
            Parts = BatteryFreshness.Shown(snapshot, time.GetUtcNow()),
        };
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
        if (_animationsEnabled is not null)
        {
            _card.AttachMotion(_time, _uiPost, _animationsEnabled);
        }

        _card.ToggleRequested += OnToggleRequested;
        _card.AutoPauseChanged += OnAutoPauseChanged;
        _card.SetupActionRequested += OnSetupAction;
        _card.SettingsRequested += OnSettingsRequested;
        _card.RefreshRequested += OnRefreshRequested;
        _card.UpdateRequested += OnUpdateRequested;
        _card.SettingChanged += OnSettingChanged;
        _card.CloseRequested += OnCardClosed;
        return _card;
    }

    private void Unsubscribe(WidgetCard card)
    {
        card.ToggleRequested -= OnToggleRequested;
        card.AutoPauseChanged -= OnAutoPauseChanged;
        card.SetupActionRequested -= OnSetupAction;
        card.SettingsRequested -= OnSettingsRequested;
        card.RefreshRequested -= OnRefreshRequested;
        card.UpdateRequested -= OnUpdateRequested;
        card.SettingChanged -= OnSettingChanged;
        card.CloseRequested -= OnCardClosed;
    }

    private void OnToggleRequested(object? sender, EventArgs e) => _callbacks.RequestToggle(_place);

    private void OnAutoPauseChanged(object? sender, bool on) => _callbacks.SetAutoPause(on, _place);

    // ---- The settings page and the update page

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        if (_host is null)
        {
            return;
        }

        _view = WidgetCardView.Settings;
        _shortcutNote = null;
        RenderKeepingBottom();
    }

    // The update line's Update button: the one path that starts a download. The page opens at once and follows
    // the flow, which begins with the download.
    private void OnUpdateRequested(object? sender, EventArgs e)
    {
        if (_host is null)
        {
            return;
        }

        _updateFrom = WidgetCardView.Main;
        _view = WidgetCardView.Update;
        _spinnerFrame = 0;
        RenderKeepingBottom();
        _host.StartUpdate();
        SyncSpinner();
    }

    private void OnHostUpdateChanged(object? sender, EventArgs e) => _uiPost(RefreshOnUiThread);

    private void OnSettingChanged(object? sender, SettingChange change)
    {
        if (_host is null)
        {
            return;
        }

        CardPlace place = _place;
        switch (change)
        {
            case PositionChange position:
                _host.SetGaugePosition(position.Value, place);
                break;
            case DisplayChange display:
                _host.SetGaugeDisplay(display.Id, place);
                break;
            case OrderChange order:
                _host.SetGaugeOrder(order.Value, place);
                break;
            case TextChange text:
                _host.SetOtherDeviceLabel(text.Value, place);
                break;
            case ThresholdChange threshold:
                _host.SetLowBatteryPercent(threshold.Percent, place);
                break;
            case ToggleChange toggle:
                ApplyToggle(toggle, place);
                break;
            case ShortcutChange shortcut:
                string? reason = _host.SetShortcut(shortcut.Shortcut, shortcut.Key, shortcut.Control, shortcut.Alt, shortcut.Shift, place);
                _shortcutNote = reason is null ? null : (shortcut.Shortcut, reason);
                break;
            case ShortcutClear clear:
                _host.ClearShortcut(clear.Shortcut, place);
                _shortcutNote = null;
                break;
            case OpenSoundSettingsRequest:
                _host.OpenSoundSettings(place);
                break;
            case RepairRequest:
                // One administrator prompt, asked for by the click. The result comes back on a card of its own.
                _host.RepairEarshot();
                break;
            case CheckRequest:
                // A check and nothing more: the result shows on the update page. Nothing downloads.
                _updateFrom = WidgetCardView.Settings;
                _view = WidgetCardView.Update;
                _spinnerFrame = 0;
                _host.CheckForUpdates();
                break;
        }

        if (_view is WidgetCardView.Settings or WidgetCardView.Update)
        {
            RenderKeepingBottom();
            SyncSpinner();
        }
    }

    private void ApplyToggle(ToggleChange toggle, CardPlace place)
    {
        switch (toggle.Row)
        {
            case SettingsRowId.PauseBud:
                _host!.SetPauseWhenBudComesOut(toggle.On, place);
                break;
            case SettingsRowId.PauseLeave:
                _host!.SetPauseWhenAirPodsLeave(toggle.On, place);
                break;
            case SettingsRowId.LeftClick:
                _host!.SetLeftClickConnects(toggle.On, place);
                break;
            case SettingsRowId.HandBack:
                _host!.SetHandBack(toggle.On, place);
                break;
            case SettingsRowId.CheckAutomatically:
                _host!.SetCheckAutomatically(toggle.On, place);
                break;
            case SettingsRowId.MicrophoneOff:
                _host!.SetHandsFreeMicrophoneOff(toggle.On, place);
                break;
        }
    }

    // Back on the settings page and on the update page, and the update page's own buttons.
    private void OnSubPageAction(SetupAction action)
    {
        if (_view == WidgetCardView.Settings)
        {
            if (action == SetupAction.Back)
            {
                _view = WidgetCardView.Main;
                _shortcutNote = null;
                RenderKeepingBottom();
            }

            return;
        }

        switch (action)
        {
            case SetupAction.Back:
                _view = _updateFrom == WidgetCardView.Settings ? WidgetCardView.Settings : WidgetCardView.Main;
                StopSpinnerTimer();
                RenderKeepingBottom();
                break;
            case SetupAction.Cancel:
                _host?.CancelUpdate();
                break;
            case SetupAction.TryAgain:
                _host?.TryUpdateAgain();
                break;
            case SetupAction.Update:
                _host?.StartUpdate();
                break;
            case SetupAction.Check:
                _host?.CheckForUpdates();
                break;
            case SetupAction.SetUp:
                _host?.SetUpEarshot();
                break;
            case SetupAction.Repair:
                _host?.RepairEarshot();
                break;
            case SetupAction.Switch:
                _host?.SwitchToInstalled();
                break;
        }

        SyncSpinner();
    }

    private void OnSetupAction(object? sender, SetupAction action)
    {
        if (_view is WidgetCardView.Settings or WidgetCardView.Update)
        {
            OnSubPageAction(action);
        }
    }

    private void OnCardClosed(object? sender, WidgetCardCloseReason reason)
    {
        if (reason == WidgetCardCloseReason.Deactivated)
        {
            _closedByDeactivateAtTimestamp = _time.GetTimestamp();
        }

        if (_view == WidgetCardView.Settings)
        {
            _view = WidgetCardView.Main;
            _shortcutNote = null;
        }

        StopRefreshTimer();
    }

    // Draws the card again from the current model. A page's height changes with what it holds (the step, a
    // caption, a row's note), so the card keeps its bottom edge where it was and grows upward from the gauge
    // instead of into the taskbar. It grows no higher than the top of the work area less the margin: a page taller than
    // that is capped there and scrolls.
    private void RenderKeepingBottom()
    {
        // A card that is fading out keeps the page it was closed on: closing switched the presenter's page, and drawing that
        // page now, or growing the card to fit it, would show the wrong page for the rest of the exit.
        if (_card is not { IsDisposed: false, Visible: true, IsExiting: false } card)
        {
            return;
        }

        Rectangle before = card.RestBounds;
        Rectangle workArea = _workAreaFor(before);
        card.MaxHeight = Math.Max(1, before.Bottom - (workArea.Top + CardPlacement.Scale(WidgetCardPlacement.GapAt96, CardDpi)));
        card.Render(BuildModel(), CardDpi);

        Size size = card.ClientSize;
        if (size != before.Size)
        {
            var resized = new Rectangle(before.X, before.Bottom - size.Height, size.Width, size.Height);
            card.PlaceAtRest(CardPlacement.Clamp(resized, workArea));
        }
    }

    private void AdvanceSpinner()
    {
        if (_view != WidgetCardView.Update)
        {
            return;
        }

        _spinnerFrame = (_spinnerFrame + 1) % SetupViewModel.SpinnerFrames;
        _card?.SetSpinnerFrame(_spinnerFrame);
    }

    // The update page turns its spinner only while the flow is checking; the timer stops the moment it is not.
    private void SyncSpinner()
    {
        if (_view != WidgetCardView.Update)
        {
            return;
        }

        bool spinning = _host is not null && _card is { IsDisposed: false, Visible: true } && UpdatePage().Icon == SetupIcon.Spinner;
        if (spinning && _spinnerTimer is null)
        {
            StartSpinnerTimer();
        }
        else if (!spinning)
        {
            StopSpinnerTimer();
        }
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
