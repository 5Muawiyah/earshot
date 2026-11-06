using Earshot.Contracts;
using Earshot.Tray;

namespace Earshot.Widget;

// One compact notice asking to be shown: what the row says, where it goes (a card after a click, or a card nobody clicked for),
// the click it follows, and what its button does. OnAction runs after the card has closed.
internal sealed record CompactRequest(CompactContent Content, CardAnchor Anchor, Point? ClickPoint = null, Action? OnAction = null);

// The card that shows compact notices. WidgetCardPresenter is the only one. ShowCompact completes with whether the notice went on
// screen, or into the asked card, and is false when it was held back, waits behind a card that outranks it, or could not be drawn.
internal interface ICompactNoticeTarget
{
    Task<bool> ShowCompact(CompactRequest request);

    // Closes a compact notice, and forgets one that waits. A card for the case opening stays.
    void HideCompact();
}

// The ICardPresenter behind registry.Cards. It turns a CardContent into a compact notice and hands it to the one card, so about
// eighty callers keep their calls. The target is set by the tray once its card exists; until then a notice is logged and dropped
// and ShowAsync completes false. Show and Hide may be called from any thread: the target queues its own work for the UI thread.
internal sealed class CompactNoticePresenter : ICardPresenter
{
    private readonly ILog _log;
    private ICompactNoticeTarget? _target;
    private Func<string?> _deviceName = static () => null;

    public CompactNoticePresenter(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    // The card that shows the notices, or null to drop them again.
    public void SetTarget(ICompactNoticeTarget? target) => Volatile.Write(ref _target, target);

    // The device's name as the tray shows it: a title that is the device's name is not repeated on a one-row card.
    public void SetDeviceName(Func<string?> deviceName)
    {
        ArgumentNullException.ThrowIfNull(deviceName);
        Volatile.Write(ref _deviceName, deviceName);
    }

    internal bool HasTarget => Volatile.Read(ref _target) is not null;

    public void Show(CardContent content, CardAnchor anchor) => _ = Send(content, anchor, null);

    public void Show(CardContent content, CardAnchor anchor, Point clickPoint) => _ = Send(content, anchor, clickPoint);

    public Task<bool> ShowAsync(CardContent content, CardAnchor anchor, Point? clickPoint) => Send(content, anchor, clickPoint);

    public void Hide()
    {
        if (Volatile.Read(ref _target) is { } target)
        {
            target.HideCompact();
        }
    }

    // A request already built (a low battery alert's line): the same route, the same answer.
    internal Task<bool> ShowCompact(CompactRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _target) is not { } target)
        {
            _log.Write(LogLevel.Debug, "Card: not shown (" + KindWords(request.Content.Kind) + "), there is no card yet.");
            return Task.FromResult(false);
        }

        _log.Write(LogLevel.Debug, "Card: compact (" + KindWords(request.Content.Kind) + ").");
        return target.ShowCompact(request);
    }

    private Task<bool> Send(CardContent content, CardAnchor anchor, Point? clickPoint)
    {
        ArgumentNullException.ThrowIfNull(content);
        return ShowCompact(RequestFor(content, anchor, clickPoint, Volatile.Read(ref _deviceName)()));
    }

    // A notice's row: no glyph (the caller's content has none), the title dropped when it is the app's name or the device's, a
    // CardAction as a text button, and the hold the arbiter gives a notice (6 s, 12 s with a button, none when it stays up).
    internal static CompactRequest RequestFor(CardContent content, CardAnchor anchor, Point? clickPoint, string? deviceName)
    {
        string title = content.Title.Trim();
        bool dropTitle = title.Length == 0
            || string.Equals(title, TrayStatus.AppName, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(deviceName) && string.Equals(title, deviceName.Trim(), StringComparison.OrdinalIgnoreCase));
        string text = dropTitle ? content.Status : title + ": " + content.Status;
        CompactAction? action = content.Action is { } card ? new CompactAction(card.Label) : null;
        var row = new CompactContent(null, InkRole.Primary, text, action, OneCardArbiter.NoticeHold(content.Action is not null, content.StaysUp))
        {
            Kind = UnaskedKind.Notice,
        };
        return new CompactRequest(row, anchor, clickPoint, content.Action?.OnClick);
    }

    private static string KindWords(UnaskedKind kind) => kind switch
    {
        UnaskedKind.MovedTo => "moved to",
        UnaskedKind.LowFallback => "low battery",
        UnaskedKind.ChargedFallback => "fully charged",
        _ => "notice",
    };
}
