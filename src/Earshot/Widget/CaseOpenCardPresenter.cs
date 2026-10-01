using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tray;

namespace Earshot.Widget;

// The gates a CaseOpened event must clear before the notice card shows, checked in this order: the setting,
// not closing, no card of ours already open, the notification state, then hand-back or a session end. Every
// Func is read fresh on each request: none of these are cached, since the very point is that a hand-back, a
// closing tray or a settings change can flip one between two CaseOpened events.
internal sealed record CaseOpenCardGate(
    Func<bool> Enabled,               // Settings.Widget.CaseOpenCard
    Func<bool> Closing,               // TrayContext._closing
    Func<bool> HandBackInProgress,    // BlockCoordinator.HandBackInProgress
    Func<bool> SessionEndInProgress,  // BlockCoordinator.SessionEndInProgress
    Func<bool> OwnCardOpen);          // WidgetCardPresenter.IsShown: the gauge-anchored card, not this one

// SystemParametersInfoW(SPI_GETMESSAGEDURATION) as it returned: the raw Win32 result, read fresh at each
// show so a machine's own accessibility setting is honoured every time, not just once at start-up.
internal readonly record struct DismissDurationReading(bool Ok, uint Seconds, int Win32Error);

// The case-open notice: a separate presenter from WidgetCardPresenter, owning its own
// WidgetCard(notice: true) instance, never the gauge-anchored card. Separate because the two can never
// share one live window (WS_EX_NOACTIVATE is set once in CreateParams; a style set at creation is not
// toggled at run time without recreating the handle) and because they are shown from different triggers -
// a gauge click, IWidgetStatus.CaseOpened - that must never contend for the same window. Model-building is
// shared through WidgetCardPresenter.BuildModel (same snapshot, same callbacks); the two cards render
// identically bar the Where line, which WidgetCard itself always overrides to "Case open" for a notice-mode
// instance, regardless of what model it was last given.
//
// It never connects on its own, and never shows a set-up page. The only path from a CaseOpened event to
// WidgetCardPresenterCallbacks.RequestToggle is a genuine left click on this card's Connect button while it
// is open (WidgetCard.ToggleRequested, wired exactly as WidgetCardPresenter wires it: a left down and a left
// up on the same one, not any button's up alone); there is no timer and no other code path here that calls it.
//
// UI thread only from the outside; every public method posts through uiPost, matching WidgetCardPresenter.
internal sealed class CaseOpenCardPresenter : IDisposable
{
    internal static readonly TimeSpan DefaultDismissDuration = TimeSpan.FromSeconds(5);

    private readonly Func<WidgetCard> _createCard;
    private readonly WidgetCardPresenterCallbacks _callbacks;
    private readonly CaseOpenCardGate _gate;
    private readonly ICardEnvironment _environment;
    private readonly Func<DismissDurationReading> _readDismissDuration;
    private readonly Func<bool>? _animationsEnabled;
    private readonly Action<Action> _uiPost;
    private readonly TimeProvider _time;
    private readonly ILog _log;

    private WidgetCard? _card;
    private ITimer? _dismissTimer;
    private string? _lastDurationProblem;
    private bool _disposed;

    public CaseOpenCardPresenter(
        Func<WidgetCard> createCard,
        WidgetCardPresenterCallbacks callbacks,
        CaseOpenCardGate gate,
        ICardEnvironment environment,
        Action<Action> uiPost,
        TimeProvider time,
        ILog log,
        Func<DismissDurationReading>? readDismissDuration = null,
        Func<bool>? animationsEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(createCard);
        ArgumentNullException.ThrowIfNull(callbacks);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(uiPost);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _createCard = createCard;
        _callbacks = callbacks;
        _gate = gate;
        _environment = environment;
        _uiPost = uiPost;
        _time = time;
        _log = log;
        _readDismissDuration = readDismissDuration ?? ReadRealDismissDuration;
        _animationsEnabled = animationsEnabled;
    }

    // True while the card is on screen. For tests; the UI thread only.
    internal bool IsShown => _card is { IsDisposed: false, Visible: true };

    // The open notice's own last-rendered model, for tests: null when no notice is open. Matches
    // WidgetCardPresenter.CurrentModelForTest exactly, so a test can prove Refresh() actually reached the
    // real card and re-rendered it, not only that Refresh() itself was called.
    internal WidgetCardModel? CurrentModelForTest => _card?.Model;

    // The gate's own two hand-back legs, read straight through rather than through RequestShow's whole
    // chain: the real notification-state check (SHQueryUserNotificationState) sits ahead of both of these
    // in RequestShowOnUiThread's own order, so a test proving which of the coordinator's own two flags each
    // one actually reads cannot get there by raising a real CaseOpened event on a desktop where that earlier
    // check already refuses (as this codebase's own private test desktops do). For tests only.
    internal bool HandBackInProgressForTest => _gate.HandBackInProgress();
    internal bool SessionEndInProgressForTest => _gate.SessionEndInProgress();

    // IWidgetStatus.CaseOpened, already posted to the UI thread by the data side, but this still posts
    // itself so a test or a future caller on another thread is safe too, matching WidgetCardPresenter.
    public void RequestShow(Rectangle? gaugeBounds) => _uiPost(() => RequestShowOnUiThread(gaugeBounds));

    // Forces the card to hide: a settings change turning the card off while one is on screen, or the tray
    // closing.
    public void Hide() => _uiPost(HideOnUiThread);

    // Re-renders the notice with the latest model, if it is on screen: matches WidgetCardPresenter.Refresh
    // exactly, since the notice's own Connect/Disconnect button reads the same callbacks.IsBusy() the
    // gauge-anchored card's button does. Before this existed, a notice already open when a connect or
    // disconnect started anywhere else (the tray icon, the menu, a hotkey) kept showing the button it last
    // rendered until the notice's own dismiss timer cleared it, the exact staleness
    // TrayContext.UpdatePresentation's own comment already describes fixing for the other card. A no-op
    // when no notice is open.
    public void Refresh() => _uiPost(RefreshOnUiThread);

    // The system's look changed: a notice on screen takes the theme and the look again, in place.
    public void ReapplyLook() => _uiPost(ReapplyLookOnUiThread);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopDismissTimer();
        if (_card is not null)
        {
            _card.CloseRequested -= OnCardClosed;
            _card.ToggleRequested -= OnToggleRequested;
            _card.AutoPauseChanged -= OnAutoPauseChanged;
            _card.Dispose();
            _card = null;
        }
    }

    // The gate, in this order: the setting, not closing, no card of ours already open, the
    // notification state, then hand-back/session-end. Each refusal beyond "the setting is off" is logged,
    // matching CardPresenter.NotificationsAccepted's own logging for the notification-state leg.
    private void RequestShowOnUiThread(Rectangle? gaugeBounds)
    {
        if (_disposed || !_gate.Enabled())
        {
            return;
        }

        if (_gate.Closing())
        {
            _log.Write(LogLevel.Debug, "Case-open card: not shown, Earshot is closing.");
            return;
        }

        if (_card is { IsDisposed: false, Visible: true })
        {
            // Idempotent: a second CaseOpened while one is already showing does not stack a second window.
            _log.Write(LogLevel.Debug, "Case-open card: already open, a second case-open notice is ignored.");
            return;
        }

        if (_gate.OwnCardOpen())
        {
            // The owner's own gauge-anchored card is a card of ours too: showing the notice over it would
            // stack one Earshot window on another, and the owner already has what he opened in front of him.
            _log.Write(LogLevel.Debug, "Case-open card: not shown, the owner's own card is already open.");
            return;
        }

        if (!NotificationsAccepted())
        {
            return;
        }

        if (_gate.HandBackInProgress() || _gate.SessionEndInProgress())
        {
            _log.Write(LogLevel.Debug, "Case-open card: not shown, " +
                (_gate.SessionEndInProgress() ? "the session is ending" : "a hand-back is running") + ".");
            return;
        }

        WidgetCard card = EnsureCard();
        WidgetCardModel model = WidgetCardPresenter.BuildModel(_callbacks, _time);
        card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
        card.Render(model, _callbacks.Dpi());

        Rectangle rest = PlaceCard(gaugeBounds, card.ClientSize);
        int travel = gaugeBounds is { } gauge
            ? CardMotion.TravelFor(gauge, SystemDisplaySource.WorkAreaFor(gauge), _callbacks.Dpi())
            : CardMotion.TravelFor(Rectangle.Empty, Rectangle.Empty, _callbacks.Dpi());
        card.PresentAnimated(rest, travel);
        StartDismissTimer();
    }

    private void ReapplyLookOnUiThread()
    {
        if (_disposed || _card is not { IsDisposed: false, Visible: true } card)
        {
            return;
        }

        card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
        card.Render(WidgetCardPresenter.BuildModel(_callbacks, _time), _callbacks.Dpi());
    }

    private void RefreshOnUiThread()
    {
        if (_card is not { IsDisposed: false, Visible: true } card)
        {
            return;
        }

        card.Render(WidgetCardPresenter.BuildModel(_callbacks, _time), _callbacks.Dpi());
    }

    private void HideOnUiThread()
    {
        if (_card is { IsDisposed: false, Visible: true } card)
        {
            card.HideAnimated();
        }

        StopDismissTimer();
    }

    // Above the gauge when gaugeBounds is given (the exact maths WidgetCardPresenter uses for its own
    // card), otherwise the NearTray corner CardPlacement already computes for every other card nobody
    // clicked for.
    private Rectangle PlaceCard(Rectangle? gaugeBounds, Size cardSize)
    {
        if (gaugeBounds is { } gauge)
        {
            Rectangle workArea = SystemDisplaySource.WorkAreaFor(gauge);
            return WidgetCardPlacement.Above(gauge, cardSize, workArea, _callbacks.Dpi(), _callbacks.CurrentGaugePosition);
        }

        PlacementScene scene = _environment.ReadScene();
        CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, scene);
        int dpi = _environment.DpiFor(target.Display);
        return CardPlacement.Place(CardAnchor.NearTray, scene, target, cardSize, dpi);
    }

    // The same rule and logging CardPresenter.NotificationsAccepted uses: a failed read refuses the card
    // (fail closed), and a state that does not accept an unrequested card is logged and refused too.
    private bool NotificationsAccepted()
    {
        NotificationStateReading reading = _environment.QueryNotificationState();
        if (reading.HResult < 0)
        {
            StepOutcome step = StepOutcomes.FromHResult("sh-query-user-notification-state:case-open-card", reading.HResult);
            _log.Warn("Case-open card not shown, the notification state could not be read. " + TrayReport.DescribeStep(step));
            return false;
        }

        if (CardPresenter.AcceptsUnrequestedCard(reading.State))
        {
            return true;
        }

        _log.Info("Case-open card not shown, Windows is not taking notifications now (" +
            CardPresenter.NotificationStateName(reading.State) + ").");
        return false;
    }

    private WidgetCard EnsureCard()
    {
        if (_card is { IsDisposed: false } card)
        {
            return card;
        }

        if (_card is not null)
        {
            _card.CloseRequested -= OnCardClosed;
            _card.ToggleRequested -= OnToggleRequested;
            _card.AutoPauseChanged -= OnAutoPauseChanged;
        }

        _card = _createCard();
        if (_animationsEnabled is not null)
        {
            _card.AttachMotion(_time, _uiPost, _animationsEnabled);
        }

        _card.CloseRequested += OnCardClosed;
        _card.ToggleRequested += OnToggleRequested;
        _card.AutoPauseChanged += OnAutoPauseChanged;
        return _card;
    }

    // The owner's own act: a click on this card's Connect button. Placed NearTray, since this card was
    // never anchored to a click of the owner's own.
    private void OnToggleRequested(object? sender, EventArgs e) => _callbacks.RequestToggle(CardPlace.NearTray);

    private void OnAutoPauseChanged(object? sender, bool on) => _callbacks.SetAutoPause(on, CardPlace.NearTray);

    private void OnCardClosed(object? sender, WidgetCardCloseReason reason) => StopDismissTimer();

    private void StartDismissTimer()
    {
        StopDismissTimer();
        TimeSpan duration = ReadDismissDuration();
        _dismissTimer = _time.CreateTimer(_ => _uiPost(HideOnUiThread), null, duration, Timeout.InfiniteTimeSpan);
    }

    private void StopDismissTimer()
    {
        _dismissTimer?.Dispose();
        _dismissTimer = null;
    }

    // SPI_GETMESSAGEDURATION, read at each show; failure or a nonsensical 0 falls back to 5 s, with the
    // Win32 code logged once per distinct problem rather than on every show.
    private TimeSpan ReadDismissDuration()
    {
        DismissDurationReading reading = _readDismissDuration();
        if (reading.Ok && reading.Seconds >= 1)
        {
            return TimeSpan.FromSeconds(reading.Seconds);
        }

        string problem = reading.Ok
            ? "SystemParametersInfoW(SPI_GETMESSAGEDURATION) returned 0 seconds"
            : "SystemParametersInfoW(SPI_GETMESSAGEDURATION) failed, Win32 error " +
                reading.Win32Error.ToString(CultureInfo.InvariantCulture);
        if (problem != _lastDurationProblem)
        {
            _lastDurationProblem = problem;
            _log.Warn("Case-open card: " + problem + ", so the default of 5 s is used.");
        }

        return DefaultDismissDuration;
    }

    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
    private static DismissDurationReading ReadRealDismissDuration()
    {
        bool ok = Shell.SystemParametersInfoForMessageDuration(Shell.SPI_GETMESSAGEDURATION, 0, out uint seconds, 0);
        return new DismissDurationReading(ok, seconds, ok ? 0 : Marshal.GetLastPInvokeError());
    }
}
