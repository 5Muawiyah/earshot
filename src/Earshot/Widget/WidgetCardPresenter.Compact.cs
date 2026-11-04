using System.Globalization;
using Earshot.Contracts;

namespace Earshot.Widget;

// Compact notices: the one-row form of the card. A notice arrives as a CompactRequest, from CompactNoticePresenter (every line the
// tray used to show on its popup) or from the low battery and fully charged fallbacks. OneCardArbiter says what it does with it:
// opens in an unasked card, replaces the row on show in place, waits in the one slot of its kind, or is carried by the asked card.
//
// A notice follows the rules the old popup had. One that follows his own click (NearCursor) is the result of what he did, so it
// shows whatever the notification state and a full-screen application say, at the click, or where the last one after a click was
// for a minute, or at the cursor on the taskbar, or near the notification area. Any other notice is a card nobody asked for: it
// keeps the notification state and the full-screen rule, and shows on the displays the case-open setting chooses.
// https://learn.microsoft.com/en-us/windows/win32/shell/notification-area
// https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate
internal sealed partial class WidgetCardPresenter
{
    // The reason a close is logged with when the card he asked for opened.
    private const string AskedOpenedWhy = "an asked card was opened";

    // How long after a card was shown at a click point that point still anchors the next card after a click. A UI timing choice:
    // long enough to outlast a connect that allows the device first, short enough that an older click is not reused.
    internal static readonly TimeSpan ClickAnchorLifetime = TimeSpan.FromSeconds(60);

    private CompactContent? _unaskedCompact;
    private CompactRequest? _unaskedRequest;
    private (CompactRequest Request, int Token)? _askedNotice;
    private int _askedNoticeToken;
    private ITimer? _askedNoticeTimer;
    private Point? _clickAnchor;
    private long _clickAnchorAt;

    // A notice that came while a card that outranks it was up: one slot for each kind, shown after that card only if still true.
    private readonly Dictionary<UnaskedKind, (CompactRequest Request, long At)> _waiting = new();

    // The model of an unasked card: the same columns, and the compact row when a notice is on. The form is Full for the case card.
    private WidgetCardModel UnaskedModel(CompactContent? compact)
    {
        WidgetCardModel model = BuildModel(_callbacks, _time);
        return compact is null ? model : model with { Form = CardForm.Compact, Compact = compact, CompactState = CompactActionState.Idle };
    }

    // The notice the asked card carries now, for tests; null when none.
    internal CompactContent? AskedNoticeForTest => _askedNotice?.Request.Content;

    // The card he asked for, for tests; null until it was opened.
    internal WidgetCard? AskedCardForTest => _card;

    // The words of the compact row on show, for tests; null when none is.
    internal CompactContent? UnaskedCompactForTest => _unaskedCompact;

    // How many notices wait, for tests.
    internal int WaitingForTest => _waiting.Count;

    public Task<bool> ShowCompact(CompactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiPost(() => shown.TrySetResult(ShowCompactOnUiThread(request)));
        return shown.Task;
    }

    // Closes a compact notice and forgets one that waits. A card for the case opening stays: a notice is not what hid it.
    public void HideCompact() => _uiPost(() =>
    {
        _waiting.Remove(UnaskedKind.Notice);
        if (_askedNotice is not null)
        {
            ForgetAskedNotice();
            RenderKeepingBottom();
        }

        if (_unaskedKind == UnaskedKind.Notice && _unaskedCompact is not null)
        {
            CloseUnasked("it was hidden");
        }
    });

    private CardState CurrentCardState()
    {
        if (IsShown)
        {
            return new CardState(_view == WidgetCardView.Main ? CardShowing.AskedMain : CardShowing.AskedSubPage);
        }

        WidgetCard? open = UnaskedOpenCards().Select(c => c.Card).FirstOrDefault();
        return open is null
            ? new CardState(CardShowing.Nothing)
            : new CardState(open.IsCompactShown ? CardShowing.CompactHolding : CardShowing.UnaskedFull, _unaskedKind);
    }

    private bool ShowCompactOnUiThread(CompactRequest request)
    {
        if (_disposed || _unaskedParts is null)
        {
            _log.Write(LogLevel.Debug, "Card: not shown (" + KindName(request.Content.Kind) + "), there is no window for it.");
            return false;
        }

        CompactContent content = request.Content;
        CardDecision decision = OneCardArbiter.Decide(
            CurrentCardState(), CardEvent.Opens(content.Kind, hasAction: content.Action is not null, staysUp: content.Hold is null));
        switch (decision.Does)
        {
            case CardDoes.Open:
            case CardDoes.ReplaceInPlace:
                bool shown = OpenUnaskedOnUiThread(content.Kind, gaugeBounds: null, _generation.Current, request);
                if (shown && decision.Does == CardDoes.ReplaceInPlace)
                {
                    // The hold starts again whether or not the words changed.
                    foreach ((_, WidgetCard card, _) in UnaskedOpenCards())
                    {
                        card.RestartHold();
                    }
                }

                return shown;

            case CardDoes.WaitInSlot:
                _waiting[content.Kind] = (request, _time.GetTimestamp());
                _log.Write(LogLevel.Debug, "Card: waiting (" + KindName(content.Kind) + "), a card that outranks it is on screen.");
                return false;

            case CardDoes.StatusRowCarries:
            case CardDoes.FooterStrip:
                return CarryInAskedCard(request, decision);

            default:
                _log.Write(LogLevel.Debug, "Card: not shown (" + KindName(content.Kind) + "), an asked card is open.");
                return false;
        }
    }

    // A notice that came while the card he asked for is open: its status row says it (or a strip at the foot of a sub-page does),
    // with the notice's button, for the hold. A newer notice takes its place. It leaves with the card, or when Hide asks.
    private bool CarryInAskedCard(CompactRequest request, CardDecision decision)
    {
        if (_card is not { IsDisposed: false, Visible: true, IsExiting: false } card)
        {
            return false;
        }

        StopAskedNoticeTimer();
        _askedNotice = (request, ++_askedNoticeToken);
        if (decision.Hold is { } hold)
        {
            int token = _askedNoticeToken;
            _askedNoticeTimer = _time.CreateTimer(_ => _uiPost(() => ClearAskedNotice(token)), null, hold, Timeout.InfiniteTimeSpan);
        }

        RenderKeepingBottom();
        if (!card.Announce(request.Content.Text))
        {
            _log.Write(LogLevel.Debug, "Card: the screen reader notification was not raised.");
        }

        _log.Write(LogLevel.Debug, "Card: carried by the asked card (" + KindName(request.Content.Kind) + "), " +
            (decision.Does == CardDoes.FooterStrip ? "in the strip at the foot of the page." : "in the status row."));
        return true;
    }

    private void ClearAskedNotice(int token)
    {
        if (_askedNotice is not { } carried || carried.Token != token)
        {
            return;
        }

        ForgetAskedNotice();
        RenderKeepingBottom();
    }

    // The notice leaves the asked card, without drawing it again: the card is closing, or the caller draws it next.
    private void ForgetAskedNotice()
    {
        StopAskedNoticeTimer();
        _askedNotice = null;
    }

    private void StopAskedNoticeTimer()
    {
        _askedNoticeTimer?.Dispose();
        _askedNoticeTimer = null;
    }

    // The carried notice's button: the notice goes and then what the button does runs; a failure of it is logged.
    private void OnNoticeActionRequested(object? sender, EventArgs e)
    {
        CompactRequest? request = _askedNotice?.Request;
        string label = request?.Content.Action?.Label ?? string.Empty;
        _log.Write(LogLevel.Debug, "Card: button pressed" + (request?.OnAction is null ? ", but it has no action." : " (" + label + ")."));
        ForgetAskedNotice();
        RenderKeepingBottom();
        RunNoticeAction(request?.OnAction, label);
    }

    // After the card that held the slot closed: the waiting notice that still is true, the most important kind first.
    private void ShowWaiting()
    {
        if (_disposed || _waiting.Count == 0)
        {
            return;
        }

        foreach (UnaskedKind kind in _waiting.Keys.Order().ToList())
        {
            (CompactRequest request, long at) = _waiting[kind];
            _waiting.Remove(kind);
            bool stillTrue = request.Content.Hold is not { } hold || _time.GetElapsedTime(at) < hold;
            if (!stillTrue)
            {
                _log.Write(LogLevel.Debug, "Card: dropped (" + KindName(kind) + "), it was no longer true when its turn came.");
                continue;
            }

            if (OpenUnaskedOnUiThread(kind, gaugeBounds: null, _generation.Current, request))
            {
                return;
            }
        }
    }

    // One card at the point his click gave, or where the card for the same click was, or at the cursor on the taskbar, or near the
    // notification area. Keyed "" in the cards by display, like the card placed without a display list.
    private WidgetCard ShowUnaskedAtClick(UnaskedParts parts, WidgetCardModel model, CompactRequest request)
    {
        WidgetCard card = EnsureUnaskedCard(parts, "", null);
        bool open = card is { Visible: true, IsExiting: false };
        PlacementScene scene = parts.Environment.ReadScene();
        (Point? anchoredAt, string where) = ClickAnchorFor(scene, request.ClickPoint);
        CardAnchor placement = CardAnchor.NearTray;
        if (anchoredAt is { } point)
        {
            placement = CardAnchor.NearCursor;
            scene = scene with { Cursor = point };
        }

        CardTarget target = CardPlacement.TargetFor(placement, scene);
        int dpi = parts.Environment.DpiFor(target.Display);
        card.SetTheme(_callbacks.Ink(), _callbacks.HighContrast());
        card.Render(model, dpi);
        Rectangle rest = CardPlacement.Place(placement, scene, target, card.ClientSize, dpi);
        if (open)
        {
            card.PlaceAtRest(rest);
        }
        else
        {
            card.PresentAnimated(rest, CardMotion.TravelFor(Rectangle.Empty, Rectangle.Empty, dpi));
        }

        if (anchoredAt is { } used)
        {
            _clickAnchor = used;
            _clickAnchorAt = _time.GetTimestamp();
        }

        _log.Write(LogLevel.Debug, "Card: placed " + where + ".");
        return card;
    }

    private (Point? Point, string Where) ClickAnchorFor(PlacementScene scene, Point? clickPoint)
    {
        if (clickPoint is { } click && CardPlacement.IsOnADisplay(click, scene.Displays))
        {
            return (click, Describe("anchored at the click", click));
        }

        if (_clickAnchor is { } last &&
            _time.GetElapsedTime(_clickAnchorAt) <= ClickAnchorLifetime &&
            CardPlacement.IsOnADisplay(last, scene.Displays))
        {
            return (last, Describe("anchored where the last card after a click was", last));
        }

        if (CardPlacement.IsOnTaskbar(scene, scene.Cursor))
        {
            return (scene.Cursor, Describe("anchored at the cursor on the taskbar", scene.Cursor));
        }

        return (null, Describe("near the notification area, because the cursor is away from the taskbar", scene.Cursor));
    }

    private static string Describe(string where, Point point) =>
        string.Create(CultureInfo.InvariantCulture, $"{where} ({point.X},{point.Y})");

    // The compact row's button: the card goes away and then what the button does runs. A failure of it is logged, not thrown into
    // the message loop.
    private void OnCompactActionRequested(object? sender, EventArgs e)
    {
        CompactRequest? request = _unaskedRequest;
        string label = request?.Content.Action?.Label ?? string.Empty;
        _log.Write(LogLevel.Debug, "Card: button pressed" + (request?.OnAction is null ? ", but it has no action." : " (" + label + ")."));
        CloseUnasked("its button was pressed");
        RunNoticeAction(request?.OnAction, label);
    }

    private void RunNoticeAction(Action? action, string label)
    {
        if (action is null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            _log.Error("Card: the button \"" + label + "\" failed (" + ex.GetType().Name + ").", ex);
        }
    }

    // A click on the row past its button grows it to the full card, which has no hold and closes by its close button.
    private void OnCompactBodyClicked(object? sender, EventArgs e)
    {
        if (_unaskedCompact is null)
        {
            return;
        }

        _log.Write(LogLevel.Debug, "Card: grown to the full card (" + KindName(_unaskedKind) + ").");
        _unaskedCompact = null;
        _unaskedRequest = null;
        RefreshUnasked();
    }
}
