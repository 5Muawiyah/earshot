using System.Globalization;
using System.Text;
using Earshot.Contracts;

namespace Earshot.Widget;

// The kinds of thing the gauge does that an owner might see as a flicker.
internal enum GaugeEventKind { Show, Move, Hide, Cover, Raise }

// The stable reason names. A name is written into the log and read by a person hunting a flicker, so a
// name never changes meaning; a new cause gets a new name.
internal static class GaugeReasons
{
    // Hide.
    public const string SettingOff = "setting-off";
    public const string Disposed = "disposed";
    public const string NoTaskbar = "no-taskbar";
    public const string ReadFailed = "read-failed";
    public const string FullScreenState = "full-screen-state";
    public const string FullScreenAppNotified = "full-screen-app-notified";
    public const string Covered = "covered";
    public const string NoFreeSpace = "no-free-space";
    public const string WindowFailed = "window-failed";

    // Show and move.
    public const string Placed = "placed";
    public const string LayoutChanged = "layout-changed";

    // Cover and raise.
    public const string WindowOverGauge = "window-over-gauge";
    public const string CoveredAtCentre = "covered-at-centre";
}

// One line's worth of what happened. Optional parts are written only when set.
internal sealed record GaugeEvent(GaugeEventKind Kind, string Reason, DateTimeOffset At)
{
    public Rectangle? Bounds { get; init; }

    // The rectangle before a move.
    public Rectangle? From { get; init; }

    // How long the gauge had been on screen when it hid, moved or was covered.
    public TimeSpan? ShownFor { get; init; }

    // The window that covers the gauge or the taskbar. Class and Explorer ownership only, never a title.
    public WindowIdentity? Window { get; init; }

    // The foreground window at the time.
    public WindowIdentity? Foreground { get; init; }

    // A failed step, when the event came from one.
    public StepOutcome? Failure { get; init; }

    public string? Detail { get; init; }
}

// Formats and writes gauge events: one line each, the prefix "Gauge:" first, then key=value fields in a
// fixed order so a line can be searched and compared.
//
//   Gauge: <kind> reason=<reason> t=<UTC, ms> [bounds=x,y,WxH] [from=x,y,WxH] [shown_ms=n]
//          [window.class=C window.explorer=true|false] [fg.class=C fg.explorer=true|false]
//          [code=n name=NAME] [detail=text to the end of the line]
internal static class GaugeEventLog
{
    public const string Prefix = "Gauge:";

    public static string Format(GaugeEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var sb = new StringBuilder(Prefix);
        sb.Append(' ').Append(e.Kind.ToString().ToLowerInvariant());
        sb.Append(" reason=").Append(Token(e.Reason));
        sb.Append(" t=").Append(e.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        if (e.Bounds is { } bounds)
        {
            sb.Append(" bounds=").Append(Rect(bounds));
        }

        if (e.From is { } from)
        {
            sb.Append(" from=").Append(Rect(from));
        }

        if (e.ShownFor is { } shown)
        {
            sb.Append(" shown_ms=").Append(((long)shown.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
        }

        AppendWindow(sb, "window", e.Window);
        AppendWindow(sb, "fg", e.Foreground);
        if (e.Failure is { } failure)
        {
            sb.Append(" code=").Append(failure.Code.ToString(CultureInfo.InvariantCulture));
            sb.Append(" name=").Append(Token(failure.CodeName));
            sb.Append(" step=").Append(Token(failure.Step));
        }

        if (!string.IsNullOrWhiteSpace(e.Detail))
        {
            sb.Append(" detail=").Append(OneLine(e.Detail));
        }

        return sb.ToString();
    }

    // A failed step is an Error the first time it is written (the controller does not repeat it); every
    // other event is Info.
    public static void Write(ILog log, GaugeEvent e)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(e);        log.Write(e.Failure is { Ok: false } ? LogLevel.Error : LogLevel.Info, Format(e));
    }

    private static void AppendWindow(StringBuilder sb, string key, WindowIdentity? window)
    {
        if (window is { } w)
        {
            sb.Append(' ').Append(key).Append(".class=").Append(Token(w.ClassName));
            sb.Append(' ').Append(key).Append(".explorer=").Append(w.BelongsToExplorer ? "true" : "false");
        }
    }

    private static string Rect(Rectangle r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.X},{r.Y},{r.Width}x{r.Height}");

    // A single token: letters, digits and . _ : $ # - only, so a class name or a code name can never carry a
    // space or an equals sign into the next field.
    private static string Token(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "?";
        }

        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '$' or '#' or '-' ? c : '_');
        }

        return sb.ToString();
    }

    private static string OneLine(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString().Trim();
    }
}
