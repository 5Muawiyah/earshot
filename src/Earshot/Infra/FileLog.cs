using System.Diagnostics;
using System.Globalization;
using System.Text;
using Earshot.Contracts;

namespace Earshot.Infra;

// Append-only text log shared by every run mode.
//
// One entry per line: "<UTC ISO 8601> <LEVEL> <message>", with exception text on the following
// lines, each indented by two spaces. When the file reaches the size cap it is renamed to
// "<name>.1.log", replacing the previous one, and a new file starts; the log never holds much more
// than twice the cap.
//
// Write never throws. A write that cannot reach the disk is counted, sent to the debugger output,
// kept in LastWriteError, and reported at the top of the next entry that does reach the file.
//
// rolls false: the file is only ever appended to, never renamed or replaced. An elevated run that logs under a
// user's own profile uses that, so a link the user planted there cannot turn the roll's rename into a
// replacement of some other file; the unelevated tray still rolls the same file.
internal sealed class FileLog : ILog
{
    public const long DefaultMaxBytes = 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _gate = new();
    private readonly string _folder;
    private readonly string _file;
    private readonly string _rolledFile;
    private readonly long _maxBytes;
    private readonly bool _rolls;
    private int _failedWrites;
    private string? _lastWriteError;

    public FileLog(string folder, string fileName = "earshot.log", long maxBytes = DefaultMaxBytes, bool rolls = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1024);

        _folder = folder;
        _file = Path.Combine(folder, fileName);
        _rolledFile = Path.Combine(folder, Path.GetFileNameWithoutExtension(fileName) + ".1" + Path.GetExtension(fileName));
        _maxBytes = maxBytes;
        _rolls = rolls;
    }

    public string FilePath => _file;

    public string RolledFilePath => _rolledFile;

    // Writes that failed since the last successful one.
    public int FailedWrites
    {
        get { lock (_gate) { return _failedWrites; } }
    }

    public string? LastWriteError
    {
        get { lock (_gate) { return _lastWriteError; } }
    }

    public void Write(LogLevel level, string message, Exception? ex = null)
    {
        DateTime now = DateTime.UtcNow;
        lock (_gate)
        {
            try
            {
                var entry = new StringBuilder();
                if (_failedWrites > 0)
                {
                    AppendEntry(entry, now, LogLevel.Warn,
                        _failedWrites.ToString(CultureInfo.InvariantCulture) + " earlier log writes failed. Last error: " + _lastWriteError, null);
                }

                AppendEntry(entry, now, level, message, ex);
                byte[] bytes = Utf8NoBom.GetBytes(entry.ToString());

                Directory.CreateDirectory(_folder);
                RollIfFull(bytes.Length);
                using (var stream = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                _failedWrites = 0;
                _lastWriteError = null;
            }
            catch (Exception failure)
            {
                // The log is the place errors go, so a failure here has nowhere better to go than
                // the counters above and the debugger output. Rethrowing would take down the caller
                // over a diagnostics problem.
                _failedWrites++;
                _lastWriteError = failure.GetType().Name + ": " + failure.Message;
                Trace.WriteLine("Earshot log write failed: " + _lastWriteError);
            }
        }
    }

    private void RollIfFull(int incomingBytes)
    {
        if (!_rolls)
        {
            return;
        }

        var info = new FileInfo(_file);
        if (info.Exists && info.Length + incomingBytes > _maxBytes)
        {
            File.Move(_file, _rolledFile, overwrite: true);
        }
    }

    internal static void AppendEntry(StringBuilder sb, DateTime utc, LogLevel level, string? message, Exception? ex)
    {
        sb.Append(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        sb.Append(' ');
        sb.Append(LevelName(level));
        sb.Append(' ');
        AppendIndented(sb, message ?? "", firstLineIndented: false);
        if (ex is not null)
        {
            AppendIndented(sb, ex.ToString(), firstLineIndented: true);
        }
    }

    private static void AppendIndented(StringBuilder sb, string text, bool firstLineIndented)
    {
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0 || firstLineIndented)
            {
                sb.Append("  ");
            }

            sb.Append(lines[i]);
            sb.Append("\r\n");
        }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "LEVEL" + ((int)level).ToString(CultureInfo.InvariantCulture),
    };
}
