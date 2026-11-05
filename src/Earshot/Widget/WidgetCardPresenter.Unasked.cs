using System.Globalization;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tray;

namespace Earshot.Widget;

// What the presenter needs to open a card nobody asked for: how to make one (an unasked WidgetCard), the gates, the desktop the
// displays are read from and the notification state source. A presenter given none of it never opens an unasked card.
internal sealed record UnaskedParts(
    Func<WidgetCard> CreateCard,
    UnaskedGate Gate,
    ICardEnvironment Environment,
    IUnaskedScene? Scene = null);

// Unasked openings: a card that the owner did not ask for, such as the case opening. They are separate WidgetCard instances
// created without activation, one for each display the card is shown on, and never the asked card. The two are never on screen
// together: an unasked opening is refused while the asked card is open, and an asked open closes the unasked cards first.
//
// An unasked card shows on the displays the setting chooses (CaseOpenCardDisplayChoice.Targets: by default where the gauge is),
// never on a display a full-screen application is on (CaseOpenCardFullScreen, the gauge's own rule), and still on the other
// chosen ones. It closes when the case closes (CaseClosed), after its own close time when one is set, on its close button, and
// on a click on it that misses its buttons: one close closes every display's card. A screen reader is told of each opening
// once, from one card. It never takes focus and the keyboard does nothing on it.
//
// It never connects on its own, and never shows a set-up page. The only path from an opening to
// WidgetCardPresenterCallbacks.RequestToggle is a genuine left click on a card's Connect button while it is open (WidgetCard.
// ToggleRequested, wired as the asked card is: a left down and a left up on the same one); there is no timer and no other code
// path here that calls it.
internal sealed partial class WidgetCardPresenter
{
    // One card per display it is on, by the display's Id ("" for the one card placed without a display list).
    private readonly Dictionary<string, (WidgetCard Card, DisplayInfo? Display)> _unaskedCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly OpenGeneration _generation = new();
    private readonly UnaskedParts? _unaskedParts;
    private ITimer? _unaskedCloseTimer;
    private long _unaskedShownAt;
    private UnaskedKind _unaskedKind = UnaskedKind.CaseOpened;

    // The scale of the one card placed without a display list: the host's scale, which every taskbar poll rewrites, so it is fixed
    // when the card is shown and read again only on a display change. A card on a display is drawn at that display's own scale.
    private readonly CardDpiLatch _fallbackDpi = new();
    private int _announcements;
    private int _unaskedLookReapplies;

    // True while an unasked card is on screen on any display. For tests and for the asked card's own gate; the UI thread only.
    // Whether a full card is on screen, the asked one or an unasked one that is not the compact row. A full card tells a toggle itself;
    // a compact row has no place for it, so the gauge keeps the story under a row. Read from the cards themselves, so it is true at
    // the moment a card is shown or changes form.
    internal bool IsFullCardShown => IsShown || UnaskedOpenCards().Any(c => !c.Card.IsCompactShown);

    internal bool IsUnaskedShown => _unaskedCards.Values.Any(c => c.Card is { IsDisposed: false, Visible: true, IsExiting: false });

    // The displays an unasked card is on screen on now, by Id, for tests.
    internal IReadOnlyList<string> UnaskedShownDisplaysForTest =>
        _unaskedCards.Where(c => c.Value.Card is { IsDisposed: false, Visible: true, IsExiting: false }).Select(c => c.Key).Order(StringComparer.Ordinal).ToList();

    // The unasked card for one display, for tests.
    internal WidgetCard? UnaskedCardForTest(string displayId) => _unaskedCards.TryGetValue(displayId, out var entry) ? entry.Card : null;

    // How many openings were announced, for tests.
    internal int AnnouncementsForTest => _announcements;

    // Whether an unasked card was given motion, for tests. Null until one exists.
    internal bool? UnaskedHasMotionForTest => _unaskedCards.Count == 0 ? null : _unaskedCards.Values.First().Card.HasMotion;

    // The first open unasked card's own last-rendered model, for tests: null when none is open.
    internal WidgetCardModel? UnaskedModelForTest => UnaskedOpenCards().Select(c => c.Card.Model).FirstOrDefault();

    // The gate's own two hand-back legs, read straight through rather than through the whole chain, for tests.
    internal bool HandBackInProgressForTest => _unaskedParts?.Gate.HandBackInProgress() ?? false;
    internal bool SessionEndInProgressForTest => _unaskedParts?.Gate.SessionEndInProgress() ?? false;

    // How many times a shown unasked card was given the look again, for tests.
    internal int UnaskedLookReappliesForTest => _unaskedLookReapplies;

    // IWidgetStatus.CaseOpened: the linked pair's case opened. gaugeBounds is the main gauge's when it is shown; the scene gives
    // the others. A card already open takes the new opening in place: drawn again, placed again, its close time started over, and
    // announced again, since it is a new opening.
    // Each request takes a generation here, on the calling thread, so a close or a close timer that was queued for an earlier
    // opening can tell it is stale (a CaseClosed posted before a reopen must not close the card the reopen shows).
    public void CaseOpened(Rectangle? gaugeBounds)
    {
        int generation = _generation.Next();
        _uiPost(() => _ = OpenUnaskedOnUiThread(UnaskedKind.CaseOpened, gaugeBounds, generation));
    }

    // IWidgetStatus.CaseClosed: the linked pair's case closed, so the card the case opened closes, whatever the close setting says.
    // Any other unasked card (a notice that stays up, the card a failure opened, Moved to) is not the case's and stays until it is
    // closed. Ignored when a newer opening was asked for after this was called: that opening is a case that is open now.
    public void CaseClosed()
    {
        int generation = _generation.Current;
        _uiPost(() =>
        {
            if (_generation.IsCurrent(generation) && _unaskedKind == UnaskedKind.CaseOpened)
            {
                CloseUnasked("the case closed", promoteWaiting: true);
            }
        });
    }

    // Forces the unasked cards to hide: a settings change turning the card off, the display choice moving, or the tray closing.
    public void HideUnasked() => _uiPost(() => CloseUnasked("it was hidden"));

    // A full-screen application may have opened or the foreground moved: a card on a display a full-screen application is now on
    // closes, and the others stay.
    public void RecheckFullScreen() => _uiPost(RecheckFullScreenOnUiThread);

    private void DisposeUnasked()
    {
        StopUnaskedCloseTimer();
        ResetTakeBack();
        foreach ((WidgetCard card, _) in _unaskedCards.Values)
        {
            UnhookUnasked(card);
            card.Dispose();
        }

        _unaskedCards.Clear();
        _waiting.Clear();
    }

    private static string KindName(UnaskedKind kind) => kind switch
    {
        UnaskedKind.CaseOpened => "case",
        UnaskedKind.ConnectFailed => "connect failed",
        UnaskedKind.MovedTo => "moved to",
        UnaskedKind.LowFallback => "low battery",
        UnaskedKind.ChargedFallback => "fully charged",
        _ => "notice",
    };

    // The gate, in this order: the setting (the case opening has one), not closing, no asked card open, the notification state (not for
    // a card that follows his own click), then hand-back or session end (not for a plain notice, which is often about exactly that),
    // and last each display. Each refusal is logged. request is the compact notice to show, or null for the case opening.
    private bool OpenUnaskedOnUiThread(UnaskedKind kind, Rectangle? gaugeBounds, int generation, CompactRequest? request = null)
    {
        if (_disposed || _unaskedParts is not { } parts)
        {
            return false;
        }

        string name = KindName(kind);
        UnaskedGate gate = parts.Gate;
        if (kind == UnaskedKind.CaseOpened && !gate.Enabled())
        {
            _log.Write(LogLevel.Debug, "Card: not shown (" + name + "), its setting is off.");
            return false;
        }

        if (gate.Closing())
        {
            _log.Write(LogLevel.Debug, "Card: not shown (" + name + "), Earshot is closing.");
            return false;
        }

        if (IsShown)
        {
            // A card the owner asked for is in front of him: an unasked one over it would stack one Earshot window on another.
            _log.Write(LogLevel.Debug, "Card: not shown (" + name + "), an asked card is open.");
            return false;
        }

        // A card after his own click is the result of what he did, so it is never held back by the notification state or by a
        // full-screen application; every other unasked card is.
        bool afterClick = request is { Anchor: CardAnchor.NearCursor };
        int state = Shell.QUNS_ACCEPTS_NOTIFICATIONS;
        if (!afterClick)
        {
            if (ReadNotificationState(parts, name) is not int read)
            {
                return false;
            }

            state = read;
        }

        if (kind != UnaskedKind.Notice && (gate.HandBackInProgress() || gate.SessionEndInProgress()))
        {
            _log.Write(LogLevel.Debug, "Card: not shown (" + name + "), " +
                (gate.SessionEndInProgress() ? "the session is ending" : "a hand-back is running") + ".");
            return false;
        }

        IUnaskedScene scene = parts.Scene ?? new SystemUnaskedScene(new SystemDisplaySource(), static () => []);
        CaseOpenCardOptions options = gate.Options?.Invoke() ?? CaseOpenCardOptions.Default;
        IReadOnlyList<DisplayInfo> displays = scene.Displays();
        var gauges = new List<Rectangle>(scene.Gauges());
        if (gaugeBounds is { } main && !gauges.Contains(main))
        {
            gauges.Insert(0, main);
        }

        WidgetCardModel model = UnaskedModel(request?.Content);
        var shown = new List<WidgetCard>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (afterClick)
        {
            keep.Add("");
            shown.Add(ShowUnaskedAtClick(parts, model, request!));
        }
        else if (displays.Count == 0)
        {
            // Windows could not list the displays: one card, above the main gauge or near the tray, as before there was a
            // choice of displays, so an opening is still shown.
            if (!UnaskedGate.FallbackMayShow(state))
            {
                // No display list means no display to leave out, so a full-screen application refuses the one card.
                _log.Write(LogLevel.Debug, "Card: not shown (" + name + "), a full-screen application is on and the displays could not be listed.");
                return false;
            }

            keep.Add("");
            shown.Add(ShowUnaskedFallback(parts, model, gaugeBounds));
        }
        else
        {
            ForegroundWindowReading? foreground = displays.Count > 1 && state is Shell.QUNS_BUSY or Shell.QUNS_RUNNING_D3D_FULL_SCREEN
                ? scene.Foreground(displays)
                : null;
            foreach (DisplayInfo display in CaseOpenCardDisplayChoice.Targets(options.Displays, options.GaugeDisplay, displays))
            {
                if (CaseOpenCardFullScreen.Covers(state, displays.Count, foreground, display))
                {
                    _log.Write(LogLevel.Debug, "Card: not shown (" + name + "), a full-screen application is on " + DisplayWords(display, displays) + ".");
                    continue;
                }

                keep.Add(display.Id);
                shown.Add(ShowUnaskedOn(parts, display, model, gauges, scene));
            }
        }

        // A display no longer chosen, or one a full-screen application is now on, has its card closed.
        foreach ((string id, (WidgetCard card, _)) in _unaskedCards)
        {
            if (!keep.Contains(id) && card is { IsDisposed: false, Visible: true })
            {
                card.HideAnimated();
            }
        }

        if (shown.Count == 0)
        {
            if (kind == UnaskedKind.CaseOpened)
            {
                StopUnaskedCloseTimer();
            }

            return false;
        }

        _unaskedKind = kind;
        _unaskedCompact = request?.Content;
        _unaskedRequest = request;
        _unaskedShownAt = _time.GetTimestamp();
        AnnounceUnasked(shown[0], model);
        string closes;
        if (request is null && kind == UnaskedKind.ConnectFailed)
        {
            // The card the failure opened holds by itself (WidgetCard), whatever the case card's close time is.
            StopUnaskedCloseTimer();
            closes = "holds " + ((long)OneCardArbiter.ActionHold.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms.";
        }
        else if (request is null)
        {
            StartUnaskedCloseTimer(options.CloseSeconds, generation);
            closes = CaseOpenCardClose.After(options.CloseSeconds) is null
                ? "closes when the case closes."
                : "closes after " + options.CloseSeconds.ToString(CultureInfo.InvariantCulture) + " s.";
        }
        else
        {
            // A compact row holds and folds by itself (WidgetCard), so no close time of the case card applies to it.
            StopUnaskedCloseTimer();
            closes = request.Content.Hold is { } hold
                ? "holds " + ((long)hold.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms."
                : "stays until it is closed.";
        }

        _log.Write(LogLevel.Debug, "Card: shown unasked (" + name + "), on " + shown.Count.ToString(CultureInfo.InvariantCulture) +
            (shown.Count == 1 ? " display" : " displays") + ", state " + UnaskedGate.NotificationStateName(state) + ", " + closes);
        return true;
    }

    private static string DisplayWords(DisplayInfo display, IReadOnlyList<DisplayInfo> all) =>
        "display " + DisplayNames.Number(display, all).ToString(CultureInfo.InvariantCulture);

    private WidgetCard ShowUnaskedOn(UnaskedParts parts, DisplayInfo display, WidgetCardModel model, IReadOnlyList<Rectangle> gauges, IUnaskedScene scene)
    {
        _ = scene;
        WidgetCard card = EnsureUnaskedCard(parts, display.Id, display);
        bool open = card is { Visible: true, IsExiting: false };
        int dpi = display.Dpi > 0 ? display.Dpi : CardPlacement.BaseDpi;
        card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
        card.Render(model, dpi);

        Rectangle? gauge = gauges.Where(g => display.Bounds.IntersectsWith(g)).Select(g => (Rectangle?)g).FirstOrDefault();
        Rectangle rest = CaseOpenCardPlacement.On(display, card.ClientSize, gauge, _callbacks.CurrentGaugePosition);
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

    // Above the gauge when one is given (the exact maths the asked card uses), otherwise the NearTray corner CardPlacement
    // computes for every other card nobody clicked for.
    private WidgetCard ShowUnaskedFallback(UnaskedParts parts, WidgetCardModel model, Rectangle? gaugeBounds)
    {
        WidgetCard card = EnsureUnaskedCard(parts, "", null);
        bool open = card is { Visible: true, IsExiting: false };
        int dpi = _fallbackDpi.FixAtShow(null, _callbacks.Dpi);
        card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
        card.Render(model, dpi);

        Rectangle rest;
        if (gaugeBounds is { } gauge)
        {
            rest = WidgetCardPlacement.Above(gauge, card.ClientSize, SystemDisplaySource.WorkAreaFor(gauge), dpi, _callbacks.CurrentGaugePosition);
        }
        else
        {
            PlacementScene scene = parts.Environment.ReadScene();
            CardTarget target = CardPlacement.TargetFor(CardAnchor.NearTray, scene);
            rest = CardPlacement.Place(CardAnchor.NearTray, scene, target, card.ClientSize, parts.Environment.DpiFor(target.Display));
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

    // Once per opening, from one card: what a screen reader says, built from the same parts the card shows.
    private void AnnounceUnasked(WidgetCard card, WidgetCardModel model)
    {
        _announcements++;
        string text = model.Form == CardForm.Compact && model.Compact is { } row
            ? row.Text
            : model.Failure is { } failure ? failure.Text : WidgetCopy.CaseOpenAnnouncement(model.ShownParts, model.Now);
        if (!card.Announce(text))
        {
            _log.Write(LogLevel.Debug, "Card: the screen reader notification was not raised.");
        }
    }

    // A failed read refuses the card (fail closed). A state that lets no display take the card is logged and refused; a
    // full-screen application refuses only its own display, below.
    private int? ReadNotificationState(UnaskedParts parts, string name)
    {
        NotificationStateReading reading = parts.Environment.QueryNotificationState();
        if (reading.HResult < 0)
        {
            StepOutcome step = StepOutcomes.FromHResult("sh-query-user-notification-state:unasked-card", reading.HResult);
            _log.Warn("Card: not shown (" + name + "), the notification state could not be read. " + TrayReport.DescribeStep(step));
            return null;
        }

        if (CaseOpenCardFullScreen.AllowsAnyDisplay(reading.State))
        {
            return reading.State;
        }

        _log.Info("Card: not shown (" + name + "), Windows is not taking notifications now (" + UnaskedGate.NotificationStateName(reading.State) + ").");
        return null;
    }

    private void RecheckFullScreenOnUiThread()
    {
        List<(string Id, WidgetCard Card, DisplayInfo? Display)> open = UnaskedOpenCards().ToList();
        if (_disposed || _unaskedParts is not { } parts || open.Count == 0)
        {
            return;
        }

        NotificationStateReading reading = parts.Environment.QueryNotificationState();
        if (reading.HResult < 0)
        {
            // Not knowing closes the card, as the show refuses on a failed read: fail closed.
            _log.Warn("Card: closed (" + KindName(_unaskedKind) + "), the notification state could not be read. " +
                TrayReport.DescribeStep(StepOutcomes.FromHResult("sh-query-user-notification-state:unasked-card-recheck", reading.HResult)));
        }

        IUnaskedScene scene = parts.Scene ?? new SystemUnaskedScene(new SystemDisplaySource(), static () => []);
        IReadOnlyList<DisplayInfo> displays = scene.Displays();
        ForegroundWindowReading? foreground = reading.HResult >= 0 && displays.Count > 1 && reading.State is Shell.QUNS_BUSY or Shell.QUNS_RUNNING_D3D_FULL_SCREEN
            ? scene.Foreground(displays)
            : null;
        foreach ((_, WidgetCard card, DisplayInfo? display) in open)
        {
            if (UnaskedGate.ClosesOnRecheck(
                reading.HResult, reading.State, displays.Count, foreground, display, afterClick: _unaskedRequest is { Anchor: CardAnchor.NearCursor }))
            {
                if (reading.HResult >= 0)
                {
                    _log.Write(LogLevel.Debug, "Card: closed (" + KindName(_unaskedKind) + "), a full-screen application is on " +
                        (display is null ? "this display" : DisplayWords(display, displays.Count > 0 ? displays : new[] { display })) + " now.");
                }

                card.HideAnimated();
            }
        }

        if (!IsUnaskedShown)
        {
            StopUnaskedCloseTimer();
        }

        // The recheck hid the last card by itself, so a failure it said is over with it, as when any other way closes the card.
        if (!UnaskedOpenCards().Any())
        {
            EndShownFailure();
        }
    }

    private IEnumerable<(string Id, WidgetCard Card, DisplayInfo? Display)> UnaskedOpenCards() =>
        _unaskedCards.Where(c => c.Value.Card is { IsDisposed: false, Visible: true, IsExiting: false })
            .Select(c => (c.Key, c.Value.Card, c.Value.Display));

    private void ReapplyUnaskedLook()
    {
        foreach ((_, WidgetCard card, DisplayInfo? display) in UnaskedOpenCards().ToList())
        {
            card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
            RenderUnaskedKeepingBottom(card, display);
            _unaskedLookReapplies++;
        }
    }

    private void RefreshUnasked()
    {
        // The Moved to row says something that has stopped being true: it folds now, under the pointer too. While Take back runs the
        // AirPods coming back is its own success, and its ring and tick stay.
        if (_unaskedCompact is { Kind: UnaskedKind.MovedTo } && _takeBack == TakeBackState.None && _callbacks.CurrentSnapshot().Where != AirPodsWhere.Elsewhere)
        {
            CloseUnasked("the AirPods are no longer on another device");
            return;
        }

        foreach ((_, WidgetCard card, DisplayInfo? display) in UnaskedOpenCards().ToList())
        {
            RenderUnaskedKeepingBottom(card, display);
        }
    }

    private void DisplayChangedUnasked()
    {
        _fallbackDpi.RereadOnDisplayChange(null, _callbacks.Dpi);
        RefreshUnasked();
    }

    // Draws the card again at its display's scale (read again, since it may have changed), or the gauge's when it has no
    // display. A new text size or scale changes its size, so it keeps its bottom edge where it was and grows upward, clamped
    // to the work area, as the asked card does; a card that is leaving is left as it is.
    private void RenderUnaskedKeepingBottom(WidgetCard card, DisplayInfo? display)
    {
        Rectangle before = card.RestBounds;
        IUnaskedScene scene = _unaskedParts?.Scene ?? new SystemUnaskedScene(new SystemDisplaySource(), static () => []);
        DisplayInfo? now = display is null ? null : scene.Displays().FirstOrDefault(d => string.Equals(d.Id, display.Id, StringComparison.OrdinalIgnoreCase)) ?? display;
        int dpi = now is { Dpi: > 0 } d ? d.Dpi : _fallbackDpi.Current(_callbacks.Dpi);
        card.Render(UnaskedModel(_unaskedCompact), dpi);

        Size size = card.LaidOutSize;
        if (size != before.Size)
        {
            Rectangle workArea = now?.WorkArea ?? SystemDisplaySource.WorkAreaFor(before);
            var resized = new Rectangle(before.X, before.Bottom - size.Height, size.Width, size.Height);
            card.PlaceAtRest(CardPlacement.Clamp(resized, workArea));
        }
    }

    // Every unasked card closes: the case closed, the close time passed, a close button, or the presenter was told to hide.
    private void CloseUnasked(string why, bool promoteWaiting = false)
    {
        StopUnaskedCloseTimer();

        // A card that folded by itself (its hold ended) has already hidden, so nothing of Take back outlives the close.
        ResetTakeBack();
        if (why == AskedOpenedWhy)
        {
            // The card he asked for is in front of him: a notice that waited for the unasked card is not shown behind it.
            _waiting.Clear();
        }

        bool any = false;
        foreach ((WidgetCard card, _) in _unaskedCards.Values)
        {
            if (card is { IsDisposed: false, Visible: true })
            {
                any = true;
                card.HideAnimated();
            }
        }

        if (any)
        {
            EndShownFailure();
            long ms = (long)_time.GetElapsedTime(_unaskedShownAt).TotalMilliseconds;
            _log.Write(LogLevel.Debug, "Card: closed (" + KindName(_unaskedKind) + "), " + why + ", after " + ms.ToString(CultureInfo.InvariantCulture) + " ms.");
            _unaskedCompact = null;
            _unaskedRequest = null;
        }

        if (promoteWaiting)
        {
            ShowWaiting();
        }
    }

    private WidgetCard EnsureUnaskedCard(UnaskedParts parts, string id, DisplayInfo? display)
    {
        if (_unaskedCards.TryGetValue(id, out var entry) && !entry.Card.IsDisposed)
        {
            _unaskedCards[id] = (entry.Card, display);
            return entry.Card;
        }

        if (entry.Card is not null)
        {
            UnhookUnasked(entry.Card);
        }

        WidgetCard card = parts.CreateCard();
        if (_animationsEnabled is not null)
        {
            card.AttachMotion(_frameClockFor(card), _animationsEnabled);
        }

        card.CloseRequested += OnUnaskedCardClosed;
        card.CompactActionRequested += OnCompactActionRequested;
        card.CompactBodyClicked += OnCompactBodyClicked;
        card.NoticeActionRequested += OnUnaskedNoticeActionRequested;
        card.ToggleRequested += OnUnaskedToggleRequested;
        card.AutoPauseChanged += OnUnaskedAutoPauseChanged;
        _unaskedCards[id] = (card, display);
        return card;
    }

    private void UnhookUnasked(WidgetCard card)
    {
        card.CloseRequested -= OnUnaskedCardClosed;
        card.CompactActionRequested -= OnCompactActionRequested;
        card.CompactBodyClicked -= OnCompactBodyClicked;
        card.NoticeActionRequested -= OnUnaskedNoticeActionRequested;
        card.ToggleRequested -= OnUnaskedToggleRequested;
        card.AutoPauseChanged -= OnUnaskedAutoPauseChanged;
    }

    // The owner's own act: a click on a card's Connect button. Placed NearTray, since this card was never anchored to a click.
    private void OnUnaskedToggleRequested(object? sender, EventArgs e) => _callbacks.RequestToggle(CardPlace.NearTray);

    private void OnUnaskedAutoPauseChanged(object? sender, bool on) => _callbacks.SetAutoPause(on, CardPlace.NearTray);

    // One card closed itself (its close button or a click past its buttons): the opening is over, so every display's card
    // closes with it.
    private void OnUnaskedCardClosed(object? sender, WidgetCardCloseReason reason)
    {
        // The card hid itself before telling the presenter, so what it said is over whether or not it still shows as visible.
        EndShownFailure();
        CloseUnasked(
            reason switch
            {
                WidgetCardCloseReason.CloseButton => "its close button was pressed",
                WidgetCardCloseReason.Action => "its button was pressed",
                WidgetCardCloseReason.HoldEnded => "its hold ended",
                _ => "it was clicked",
            },
            promoteWaiting: true);
    }

    // The timer's callback may already be queued when the card is shown again, so it carries the generation of the opening it was
    // started for and does nothing once a newer one was asked for.
    private void StartUnaskedCloseTimer(int closeSeconds, int generation)
    {
        StopUnaskedCloseTimer();
        if (CaseOpenCardClose.After(closeSeconds) is not TimeSpan after)
        {
            return; // until the case closes: CaseClosed closes it
        }

        _unaskedCloseTimer = _time.CreateTimer(
            _ => _uiPost(() =>
            {
                if (_generation.IsCurrent(generation))
                {
                    CloseUnasked("its close time of " + closeSeconds.ToString(CultureInfo.InvariantCulture) + " s passed", promoteWaiting: true);
                }
            }),
            null, after, Timeout.InfiniteTimeSpan);
    }

    private void StopUnaskedCloseTimer()
    {
        _unaskedCloseTimer?.Dispose();
        _unaskedCloseTimer = null;
    }
}
