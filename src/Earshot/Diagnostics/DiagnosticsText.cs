using System.Globalization;
using System.Text;

namespace Earshot.Diagnostics;

// The lines of the frame log, the record of what the radio heard that another part of Earshot keeps. Kept behind
// an interface so this feature does not depend on that part: a build without it uses NullFrameLogSource.
public interface IFrameLogSource
{
    // The frame log's lines, oldest first, as text. Empty when there is none. Called on the thread that builds the
    // text, so an implementation reads what it already holds and does not wait.
    IReadOnlyList<string> Lines();
}

// A frame log that holds nothing.
public sealed class NullFrameLogSource : IFrameLogSource
{
    public static readonly NullFrameLogSource Instance = new();

    public IReadOnlyList<string> Lines() => [];
}

// The text "Copy diagnostics" puts on the clipboard: the app and Windows versions, the end of the log file and the
// frame log, all passed through DiagnosticsRedactor so it can be pasted into a public issue.
public static class DiagnosticsText
{
    // Design choice: enough lines to cover a session's start-up and a few switches, few enough to paste.
    public const int DefaultLogLines = 200;

    // The most of the log file read from its end. The log rolls at 1 MiB (Infra\FileLog.DefaultMaxBytes), so this
    // reads at most a quarter of what it can hold.
    internal const int MaxTailBytes = 256 * 1024;

    internal const string NoLog = "(no log yet)";
    internal const string NoFrameLog = "(none)";

    // appVersion and windowsVersion are plain text. logLines are the log file's lines; only the last maxLogLines are
    // kept. knownNames are removed wherever they appear (see DiagnosticsRedactor.Redact). The result is already
    // redacted: the caller has nothing more to do before copying it.
    public static string Build(
        string appVersion,
        string windowsVersion,
        IEnumerable<string> logLines,
        IFrameLogSource frameLog,
        IEnumerable<string>? knownNames = null,
        int maxLogLines = DefaultLogLines,
        string? userName = null)
    {
        ArgumentNullException.ThrowIfNull(appVersion);
        ArgumentNullException.ThrowIfNull(windowsVersion);
        ArgumentNullException.ThrowIfNull(logLines);
        ArgumentNullException.ThrowIfNull(frameLog);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLogLines, 1);

        string[] log = [.. logLines];
        string[] tail = log.Length > maxLogLines ? log[^maxLogLines..] : log;
        IReadOnlyList<string> frames = frameLog.Lines();

        var sb = new StringBuilder();
        sb.Append("Earshot diagnostics\n");
        sb.Append("Names, addresses, ids and paths are removed.\n\n");
        sb.Append("Version: ").Append(appVersion).Append('\n');
        sb.Append("Windows: ").Append(windowsVersion).Append("\n\n");
        sb.Append("Log (last ").Append(maxLogLines.ToString(CultureInfo.InvariantCulture)).Append(" lines)\n");
        AppendLines(sb, tail, NoLog);
        sb.Append("\nFrame log\n");
        AppendLines(sb, frames, NoFrameLog);

        return userName is null
            ? DiagnosticsRedactor.Redact(sb.ToString(), knownNames)
            : DiagnosticsRedactor.Redact(sb.ToString(), knownNames, userName);
    }

    private static void AppendLines(StringBuilder sb, IReadOnlyList<string> lines, string whenEmpty)
    {
        if (lines.Count == 0)
        {
            sb.Append(whenEmpty).Append('\n');
            return;
        }

        foreach (string line in lines)
        {
            sb.Append(line.TrimEnd('\r', '\n')).Append('\n');
        }
    }

    // The lines of the log file. Reads the end of the file only, opened so the running log can keep writing to it
    // (FileShare.ReadWrite | Delete, as Infra\FileLog opens it). A file that is not there gives no lines. A file that
    // cannot be read is not hidden: the one line returned says so with the exception's type and HRESULT, and the
    // same words are returned through failure for the caller to log.
    internal static IReadOnlyList<string> ReadLogLines(string path, out string? failure)
    {
        failure = null;
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - MaxTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            string text = reader.ReadToEnd();
            var lines = new List<string>(text.Split('\n'));

            // A read that began mid-file starts in the middle of a line.
            if (start > 0 && lines.Count > 0)
            {
                lines.RemoveAt(0);
            }

            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = "The log could not be read: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ".";
            return [failure];
        }
    }
}
