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

    public void Show(CardContent content, CardAnchor anchor, System.Drawing.Point clickPoint)
    {
        ArgumentNullException.ThrowIfNull(content);
        _log.Write(LogLevel.Debug, "Card not shown, no card in this build: " + content.Title + ": " + content.Status +
            " (after a click at " + clickPoint.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
            clickPoint.Y.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    }

    // Nothing is ever on screen, so a notice that waits to be seen stays due.
    public Task<bool> ShowAsync(CardContent content, CardAnchor anchor, System.Drawing.Point? clickPoint)
    {
        ArgumentNullException.ThrowIfNull(content);
        _log.Write(LogLevel.Debug, "Card not shown, no card in this build: " + content.Title + ": " + content.Status);
        return Task.FromResult(false);
    }

    public void Hide()
    {
    }
}
