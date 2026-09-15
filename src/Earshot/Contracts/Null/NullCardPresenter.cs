namespace Earshot.Contracts.Null;

// Stands in until the popup card is wired. Shows nothing and notes each request at Debug level.
public sealed class NullCardPresenter : ICardPresenter
{
    private readonly ILog _log;

    public NullCardPresenter(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public void Show(CardContent content, CardAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(content);
        _log.Write(LogLevel.Debug, "Card not shown, no card in this build: " + content.Title + ": " + content.Status);
    }

    public void Hide()
    {
    }
}
