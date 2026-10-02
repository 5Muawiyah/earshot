using System.Globalization;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Popup;
using Earshot.Tray;

namespace Earshot.Widget;

// What the case-open card is set to, read fresh at each open: when it closes by itself, which displays show it, and the
// display the gauge is set to, for "where the gauge is". The gauge's position on the taskbar, for a card placed above it,
// comes from the callbacks, as for the gauge's own card.
internal sealed record CaseOpenCardOptions(int CloseSeconds, IReadOnlyList<string> Displays, string GaugeDisplay)
{
    public static CaseOpenCardOptions Default { get; } = new(CaseOpenCardClose.UntilCaseCloses, [], GaugeDisplayChoice.MainDisplay);

    public static CaseOpenCardOptions From(WidgetSettings widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        return new CaseOpenCardOptions(widget.CaseOpenCardCloseSeconds, widget.CaseOpenCardDisplays, widget.GaugeDisplay);
    }
}

// The gates a CaseOpened event must clear before the notice card shows, checked in this order: the setting,
// not closing, no card of ours already open, the notification state, then hand-back or a session end. Every
// Func is read fresh on each request: none of these are cached, since the very point is that a hand-back, a
// closing tray or a settings change can flip one between two CaseOpened events.
internal sealed record CaseOpenCardGate(
    Func<bool> Enabled,               // Settings.Widget.CaseOpenCardOn
    Func<bool> Closing,               // TrayContext._closing
    Func<bool> HandBackInProgress,    // BlockCoordinator.HandBackInProgress
    Func<bool> SessionEndInProgress,  // BlockCoordinator.SessionEndInProgress
    Func<bool> OwnCardOpen,           // WidgetCardPresenter.IsShown: the gauge-anchored card, not this one
    Func<CaseOpenCardOptions>? Options = null); // the close and display choices; the defaults when null

// The desktop as the case-open card needs it: the displays, the gauges shown on them and the foreground window. The real one
// reads Windows; a test hands in fake displays, gauges and a full-screen window.
internal interface ICaseOpenCardScene
{
    // The connected displays; empty only when Windows could not be asked.
    IReadOnlyList<DisplayInfo> Displays();

    // The bounds of every gauge shown now (the main one and those on other displays under All displays).
    IReadOnlyList<Rectangle> Gauges();

    // The foreground window as the full-screen rule reads it, or null.
    ForegroundWindowReading? Foreground(IReadOnlyList<DisplayInfo> displays);
}

// The real scene: the display source the tray works from, the gauges the tray says are shown, and the foreground window.
internal sealed class SystemCaseOpenCardScene(IDisplaySource displays, Func<IReadOnlyList<Rectangle>> gauges) : ICaseOpenCardScene
{
    public IReadOnlyList<DisplayInfo> Displays() => displays.Read().Displays;

    public IReadOnlyList<Rectangle> Gauges() => gauges();

    public ForegroundWindowReading? Foreground(IReadOnlyList<DisplayInfo> list) => SystemDisplaySource.ForegroundWindow(list);
}

// The case-open card: a separate presenter from WidgetCardPresenter, owning its own WidgetCard(notice: true) instances, one
// for each display it shows on, never the gauge-anchored card. Separate because the two can never share one live window
// (WS_EX_NOACTIVATE is set once in CreateParams; a style set at creation is not toggled at run time without recreating the
// handle) and because they are shown from different triggers - a gauge click, IWidgetStatus.CaseOpened - that must never
// contend for the same window. Model-building is shared through WidgetCardPresenter.BuildModel (same snapshot, same
// callbacks); the cards render the main card's live L, R and Case and its Connect or Disconnect button, with the Where line
// reading "Case open" and a close button where the gear is.
//
// It shows on the displays the setting chooses (CaseOpenCardDisplayChoice.Targets: by default where the gauge is), never on
// a display a full-screen application is on (CaseOpenCardFullScreen, the gauge's own rule), and still on the other chosen
// ones. It closes when the case closes (IWidgetStatus.CaseClosed), after its own close time when one is set, on its close
// button, on a click on it that misses its buttons, and after a press of Connect or Disconnect: one close closes every
// display's card. A screen reader is told of each open once, from one card.
//
// It never connects on its own, and never shows a set-up page. The only path from a CaseOpened event to
// WidgetCardPresenterCallbacks.RequestToggle is a genuine left click on a card's Connect button while it is open
// (WidgetCard.ToggleRequested, wired exactly as WidgetCardPresenter wires it: a left down and a left up on the same one,
// not any button's up alone); there is no timer and no other code path here that calls it.
//
// UI thread only from the outside; every public method posts through uiPost, matching WidgetCardPresenter.
internal sealed class CaseOpenCardPresenter : IDisposable
{
    private readonly Func<WidgetCard> _createCard;
    private readonly WidgetCardPresenterCallbacks _callbacks;
    private readonly CaseOpenCardGate _gate;
    private readonly ICardEnvironment _environment;
    private readonly ICaseOpenCardScene _scene;
    private readonly Func<bool>? _animationsEnabled;
    private readonly Func<WidgetCard, IFrameClock> _frameClockFor;
    private readonly Action<Action> _uiPost;
    private readonly TimeProvider _time;
    private readonly ILog _log;

    // One card per display it is on, by the display's Id ("" for the one card placed without a display list).
    private readonly Dictionary<string, (WidgetCard Card, DisplayInfo? Display)> _cards = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _closeTimer;
    private readonly OpenGeneration _generation = new();

    // The scale of the one card placed without a display list: the host's scale, which every taskbar poll rewrites, so it is fixed
    // when the card is shown and read again only on a display change. A card on a display is drawn at that display's own scale.
    private readonly CardDpiLatch _fallbackDpi = new();
    private int _announcements;
    private bool _disposed;

    public CaseOpenCardPresenter(
        Func<WidgetCard> createCard,
        WidgetCardPresenterCallbacks callbacks,
        CaseOpenCardGate gate,
        ICardEnvironment environment,
        Action<Action> uiPost,
        TimeProvider time,
        ILog log,
        ICaseOpenCardScene? scene = null,
        Func<bool>? animationsEnabled = null,
        Func<WidgetCard, IFrameClock>? frameClockFor = null)
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
        _scene = scene ?? new SystemCaseOpenCardScene(new SystemDisplaySource(), static () => []);
        _animationsEnabled = animationsEnabled;

        // Each card's frames come from the display it is on (VBlankFrameClock); a test hands in a fake clock.
        _frameClockFor = frameClockFor ?? (card => new VBlankFrameClock(() => card.IsHandleCreated ? card.Handle : 0, uiPost, log, new NoVBlankOutputs()));
    }

    // True while a card is on screen on any display. For tests and the gauge card's own gate; the UI thread only.
    internal bool IsShown => _cards.Values.Any(c => c.Card is { IsDisposed: false, Visible: true });

    // The displays a card is on screen on now, by Id, for tests.
    internal IReadOnlyList<string> ShownDisplaysForTest =>
        _cards.Where(c => c.Value.Card is { IsDisposed: false, Visible: true, IsExiting: false }).Select(c => c.Key).Order(StringComparer.Ordinal).ToList();

    // The card for one display, for tests.
    internal WidgetCard? CardForTest(string displayId) => _cards.TryGetValue(displayId, out var entry) ? entry.Card : null;

    // How many opens were announced, for tests.
    internal int AnnouncementsForTest => _announcements;

    // Whether the card was given motion, for tests. Null until a card exists.
    internal bool? HasMotionForTest => _cards.Count == 0 ? null : _cards.Values.First().Card.HasMotion;

    // The first open card's own last-rendered model, for tests: null when none is open. Matches
    // WidgetCardPresenter.CurrentModelForTest, so a test can prove Refresh() actually reached the real card.
    internal WidgetCardModel? CurrentModelForTest => OpenCards().Select(c => c.Card.Model).FirstOrDefault();

    // The gate's own two hand-back legs, read straight through rather than through RequestShow's whole chain, for tests.
    internal bool HandBackInProgressForTest => _gate.HandBackInProgress();
    internal bool SessionEndInProgressForTest => _gate.SessionEndInProgress();

    // IWidgetStatus.CaseOpened: the linked pair's case opened. gaugeBounds is the main gauge's when it is shown; the scene
    // gives the others. A card already open takes the new open in place: drawn again, placed again, its close time started
    // over, and announced again, since it is a new open.
    // Each request takes a generation here, on the calling thread, so a close or a close timer that was queued for an earlier
    // open can tell it is stale (a CaseClosed posted before a reopen must not close the card the reopen shows).
    public void RequestShow(Rectangle? gaugeBounds)
    {
        int generation = _generation.Next();
        _uiPost(() => ShowOnUiThread(gaugeBounds, generation));
    }

    // IWidgetStatus.CaseClosed: the linked pair's case closed, so every card closes, whatever the close setting says. Ignored
    // when a newer open was asked for after this was called: that open is a case that is open now.
    public void CaseClosed()
    {
        int generation = _generation.Current;
        _uiPost(() =>
        {
            if (_generation.IsCurrent(generation))
            {
                HideOnUiThread("the case closed");
            }
        });
    }

    // Forces the cards to hide: the gauge's own card opening, a settings change turning the card off, or the tray closing.
    public void Hide() => _uiPost(() => HideOnUiThread(null));

    // Re-renders the open cards with the latest model: the snapshot moved, or a connect or disconnect started elsewhere, so
    // the Connect or Disconnect button and the three figures stay current. A no-op when none is open.
    public void Refresh() => _uiPost(RefreshOnUiThread);

    // The system's look changed: an open card takes the theme and the look again, in place.
    public void ReapplyLook() => _uiPost(ReapplyLookOnUiThread);

    // The displays changed: an open card reads its scale again (the fallback card from the host, the others from their display)
    // and keeps its bottom edge as it grows or shrinks. The only time the fallback card's scale is read after the show.
    public void DisplayChanged() => _uiPost(DisplayChangedOnUiThread);

    // A full-screen application may have opened or the foreground moved: a card on a display a full-screen application is
    // now on closes, and the others stay.
    public void RecheckFullScreen() => _uiPost(RecheckFullScreenOnUiThread);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopCloseTimer();
        foreach ((WidgetCard card, _) in _cards.Values)
        {
            Unhook(card);
            card.Dispose();
        }

        _cards.Clear();
    }

    // The gate, in this order: the setting, not closing, no card of ours already open, the notification state, then
    // hand-back or session end. Each refusal beyond "the setting is off" is logged.
    private void ShowOnUiThread(Rectangle? gaugeBounds, int generation)
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

        if (_gate.OwnCardOpen())
        {
            // The owner's own gauge-anchored card is a card of ours too: showing the notice over it would
            // stack one Earshot window on another, and the owner already has what he opened in front of him.
            _log.Write(LogLevel.Debug, "Case-open card: not shown, the owner's own card is already open.");
            return;
        }

        if (ReadNotificationState() is not int state)
        {
            return;
        }

        if (_gate.HandBackInProgress() || _gate.SessionEndInProgress())
        {
            _log.Write(LogLevel.Debug, "Case-open card: not shown, " +
                (_gate.SessionEndInProgress() ? "the session is ending" : "a hand-back is running") + ".");
            return;
        }

        CaseOpenCardOptions options = _gate.Options?.Invoke() ?? CaseOpenCardOptions.Default;
        IReadOnlyList<DisplayInfo> displays = _scene.Displays();
        var gauges = new List<Rectangle>(_scene.Gauges());
        if (gaugeBounds is { } main && !gauges.Contains(main))
        {
            gauges.Insert(0, main);
        }

        WidgetCardModel model = WidgetCardPresenter.BuildModel(_callbacks, _time);
        var shown = new List<WidgetCard>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (displays.Count == 0)
        {
            // Windows could not list the displays: one card, above the main gauge or near the tray, as before there was a
            // choice of displays, so an open is still shown.
            if (!CaseOpenCardRules.FallbackMayShow(state))
            {
                // No display list means no display to leave out, so a full-screen application refuses the one card.
                _log.Write(LogLevel.Debug, "Case-open card: not shown, a full-screen application is on and the displays could not be listed.");
                return;
            }

            keep.Add("");
            shown.Add(ShowFallback(model, gaugeBounds, _callbacks.CurrentGaugePosition));
        }
        else
        {
            ForegroundWindowReading? foreground = displays.Count > 1 && state is Shell.QUNS_BUSY or Shell.QUNS_RUNNING_D3D_FULL_SCREEN
                ? _scene.Foreground(displays)
                : null;
            foreach (DisplayInfo display in CaseOpenCardDisplayChoice.Targets(options.Displays, options.GaugeDisplay, displays))
            {
                if (CaseOpenCardFullScreen.Covers(state, displays.Count, foreground, display))
                {
                    _log.Write(LogLevel.Debug, "Case-open card: not shown on " + DisplayNames.Short(display, displays) + ", a full-screen application is on it.");
                    continue;
                }

                keep.Add(display.Id);
                shown.Add(ShowOn(display, model, gauges, _callbacks.CurrentGaugePosition));
            }
        }

        // A display no longer chosen, or one a full-screen application is now on, has its card closed.
        foreach ((string id, (WidgetCard card, _)) in _cards)
        {
            if (!keep.Contains(id) && card is { IsDisposed: false, Visible: true })
            {
                card.HideAnimated();
            }
        }

        if (shown.Count == 0)
        {
            StopCloseTimer();
            return;
        }

        Announce(shown[0], model);
        StartCloseTimer(options.CloseSeconds, generation);
    }

    private WidgetCard ShowOn(DisplayInfo display, WidgetCardModel model, IReadOnlyList<Rectangle> gauges, GaugePosition position)
    {
        WidgetCard card = EnsureCard(display.Id, display);
        bool open = card is { Visible: true, IsExiting: false };
        int dpi = display.Dpi > 0 ? display.Dpi : CardPlacement.BaseDpi;
        card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
        card.Render(model, dpi);

        Rectangle? gauge = gauges.Where(g => display.Bounds.IntersectsWith(g)).Select(g => (Rectangle?)g).FirstOrDefault();
        Rectangle rest = CaseOpenCardPlacement.On(display, card.ClientSize, gauge, position);
        if (open)
        {
            card.PlaceAtRest(rest);
        }
        else
        {
            card.PresentAnimated(rest, CardMotion.TravelFor(gauge ?? Rectangle.Empty, display.WorkArea, dpi));
        }

        return card;
    }

    // Above the gauge when one is given (the exact maths WidgetCardPresenter uses for its own card), otherwise the NearTray
    // corner CardPlacement computes for every other card nobody clicked for.
    private WidgetCard ShowFallback(WidgetCardModel model, Rectangle? gaugeBounds, GaugePosition position)
    {
        WidgetCard card = EnsureCard("", null);
        bool open = card is { Visible: true, IsExiting: false };
        int dpi = _fallbackDpi.FixAtShow(null, _callbacks.Dpi);
        card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
        card.Render(model, dpi);

        Rectangle rest;
        if (gaugeBounds is { } gauge)
        {
            rest = WidgetCardPlacement.Above(gauge, card.ClientSize, SystemDisplaySource.WorkAreaFor(gauge), dpi, position);
        }
        else
        {
            PlacementScene scene = _environment.ReadScene();
            CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, scene);
            rest = CardPlacement.Place(CardAnchor.NearTray, scene, target, card.ClientSize, _environment.DpiFor(target.Display));
        }

        if (open)
        {
            card.PlaceAtRest(rest);
        }
        else
        {
            card.PresentAnimated(rest, CardMotion.TravelFor(gaugeBounds ?? Rectangle.Empty, Rectangle.Empty, dpi));
        }

        return card;
    }

    // Once per open, from one card: what a screen reader says, built from the same parts the card shows.
    private void Announce(WidgetCard card, WidgetCardModel model)
    {
        _announcements++;
        string text = WidgetCopy.CaseOpenAnnouncement(model.ShownParts, model.Now);
        if (!card.Announce(text))
        {
            _log.Write(LogLevel.Debug, "Case-open card: the screen reader notification was not raised.");
        }
    }

    // The same rule and logging CardPresenter.NotificationsAccepted uses: a failed read refuses the card (fail closed). A state
    // that lets no display take the card is logged and refused; a full-screen application refuses only its own display, below.
    private int? ReadNotificationState()
    {
        NotificationStateReading reading = _environment.QueryNotificationState();
        if (reading.HResult < 0)
        {
            StepOutcome step = StepOutcomes.FromHResult("sh-query-user-notification-state:case-open-card", reading.HResult);
            _log.Warn("Case-open card not shown, the notification state could not be read. " + TrayReport.DescribeStep(step));
            return null;
        }

        if (CaseOpenCardFullScreen.AllowsAnyDisplay(reading.State))
        {
            return reading.State;
        }

        _log.Info("Case-open card not shown, Windows is not taking notifications now (" +
            CardPresenter.NotificationStateName(reading.State) + ").");
        return null;
    }

    private void RecheckFullScreenOnUiThread()
    {
        List<(string Id, WidgetCard Card, DisplayInfo? Display)> open = OpenCards().ToList();
        if (_disposed || open.Count == 0)
        {
            return;
        }

        NotificationStateReading reading = _environment.QueryNotificationState();
        if (reading.HResult < 0)
        {
            // Not knowing closes the card, as the show refuses on a failed read: fail closed.
            _log.Warn("Case-open card closed, the notification state could not be read. " +
                TrayReport.DescribeStep(StepOutcomes.FromHResult("sh-query-user-notification-state:case-open-card-recheck", reading.HResult)));
        }

        IReadOnlyList<DisplayInfo> displays = _scene.Displays();
        ForegroundWindowReading? foreground = reading.HResult >= 0 && displays.Count > 1 && reading.State is Shell.QUNS_BUSY or Shell.QUNS_RUNNING_D3D_FULL_SCREEN
            ? _scene.Foreground(displays)
            : null;
        foreach ((_, WidgetCard card, DisplayInfo? display) in open)
        {
            if (CaseOpenCardRules.ClosesOnRecheck(reading.HResult, reading.State, displays.Count, foreground, display))
            {
                if (reading.HResult >= 0)
                {
                    _log.Write(LogLevel.Debug, "Case-open card: closed" + (display is null ? "" : " on " +
                        DisplayNames.Short(display, displays.Count > 0 ? displays : new[] { display })) + ", a full-screen application is on it now.");
                }

                card.HideAnimated();
            }
        }

        if (!IsShown)
        {
            StopCloseTimer();
        }
    }

    private IEnumerable<(string Id, WidgetCard Card, DisplayInfo? Display)> OpenCards() =>
        _cards.Where(c => c.Value.Card is { IsDisposed: false, Visible: true, IsExiting: false })
            .Select(c => (c.Key, c.Value.Card, c.Value.Display));

    private int _lookReapplies;

    // How many times a shown card was given the look again, for tests.
    internal int LookReappliesForTest => _lookReapplies;

    private void ReapplyLookOnUiThread()
    {
        if (_disposed)
        {
            return;
        }

        foreach ((_, WidgetCard card, DisplayInfo? display) in OpenCards().ToList())
        {
            card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
            RenderKeepingBottom(card, display);
            _lookReapplies++;
        }
    }

    private void DisplayChangedOnUiThread()
    {
        if (_disposed)
        {
            return;
        }

        _fallbackDpi.RereadOnDisplayChange(null, _callbacks.Dpi);
        RefreshOnUiThread();
    }

    private void RefreshOnUiThread()
    {
        foreach ((_, WidgetCard card, DisplayInfo? display) in OpenCards().ToList())
        {
            RenderKeepingBottom(card, display);
        }
    }

    // Draws the card again at its display's scale (read again, since it may have changed), or the gauge's when it has no
    // display. A new text size or scale changes its size, so it keeps its bottom edge where it was and grows upward, clamped
    // to the work area, as the gauge's own card does; a card that is leaving is left as it is.
    private void RenderKeepingBottom(WidgetCard card, DisplayInfo? display)
    {
        Rectangle before = card.RestBounds;
        DisplayInfo? now = display is null ? null : _scene.Displays().FirstOrDefault(d => string.Equals(d.Id, display.Id, StringComparison.OrdinalIgnoreCase)) ?? display;
        int dpi = now is { Dpi: > 0 } d ? d.Dpi : _fallbackDpi.Current(_callbacks.Dpi);
        card.Render(WidgetCardPresenter.BuildModel(_callbacks, _time), dpi);

        Size size = card.LaidOutSize;
        if (size != before.Size)
        {
            Rectangle workArea = now?.WorkArea ?? SystemDisplaySource.WorkAreaFor(before);
            var resized = new Rectangle(before.X, before.Bottom - size.Height, size.Width, size.Height);
            card.PlaceAtRest(CardPlacement.Clamp(resized, workArea));
        }
    }

    // Every card closes: the case closed, the close time passed, a close button, Connect, or the presenter was told to hide.
    private void HideOnUiThread(string? why)
    {
        StopCloseTimer();
        bool any = false;
        foreach ((WidgetCard card, _) in _cards.Values)
        {
            if (card is { IsDisposed: false, Visible: true })
            {
                any = true;
                card.HideAnimated();
            }
        }

        if (any && why is not null)
        {
            _log.Write(LogLevel.Debug, "Case-open card: closed, " + why + ".");
        }
    }

    private WidgetCard EnsureCard(string id, DisplayInfo? display)
    {
        if (_cards.TryGetValue(id, out var entry) && !entry.Card.IsDisposed)
        {
            _cards[id] = (entry.Card, display);
            return entry.Card;
        }

        if (entry.Card is not null)
        {
            Unhook(entry.Card);
        }

        WidgetCard card = _createCard();
        if (_animationsEnabled is not null)
        {
            card.AttachMotion(_frameClockFor(card), _animationsEnabled);
        }

        card.CloseRequested += OnCardClosed;
        card.ToggleRequested += OnToggleRequested;
        card.AutoPauseChanged += OnAutoPauseChanged;
        _cards[id] = (card, display);
        return card;
    }

    private void Unhook(WidgetCard card)
    {
        card.CloseRequested -= OnCardClosed;
        card.ToggleRequested -= OnToggleRequested;
        card.AutoPauseChanged -= OnAutoPauseChanged;
    }

    // The owner's own act: a click on a card's Connect button. Placed NearTray, since this card was never anchored to a click
    // of the owner's own.
    private void OnToggleRequested(object? sender, EventArgs e) => _callbacks.RequestToggle(CardPlace.NearTray);

    private void OnAutoPauseChanged(object? sender, bool on) => _callbacks.SetAutoPause(on, CardPlace.NearTray);

    // One card closed itself (its close button, a click past its buttons, or Connect): the open is over, so every display's
    // card closes with it.
    private void OnCardClosed(object? sender, WidgetCardCloseReason reason) =>
        HideOnUiThread(reason switch
        {
            WidgetCardCloseReason.CloseButton => "its close button was pressed",
            WidgetCardCloseReason.Action => "its button was pressed",
            _ => "it was clicked",
        });

    // The timer's callback may already be queued when the card is shown again, so it carries the generation of the open it was
    // started for and does nothing once a newer open was asked for.
    private void StartCloseTimer(int closeSeconds, int generation)
    {
        StopCloseTimer();
        if (CaseOpenCardClose.After(closeSeconds) is not TimeSpan after)
        {
            return; // until the case closes: IWidgetStatus.CaseClosed closes it
        }

        _closeTimer = _time.CreateTimer(
            _ => _uiPost(() =>
            {
                if (_generation.IsCurrent(generation))
                {
                    HideOnUiThread("its close time of " + closeSeconds.ToString(CultureInfo.InvariantCulture) + " s passed");
                }
            }),
            null, after, Timeout.InfiniteTimeSpan);
    }

    private void StopCloseTimer()
    {
        _closeTimer?.Dispose();
        _closeTimer = null;
    }
}
