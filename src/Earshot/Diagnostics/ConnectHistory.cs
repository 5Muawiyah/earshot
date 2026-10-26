using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Earshot.Contracts;

namespace Earshot.Diagnostics;

// The values an entry's strings take. Strings, not enums, because the file outlives the build that wrote it.
internal static class ConnectHistoryActions
{
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";
    public const string ConnectInWindows = "connect-in-windows";
    public const string WindowsConnection = "windows-connection";
    public const string HandBack = "hand-back";

    // One entry per Pair AirPods run (and per Fix re-pair), whatever its outcome.
    public const string Pair = "pair";
}

internal static class ConnectHistoryOutcomes
{
    public const string Connected = "connected";
    public const string Disconnected = "disconnected";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Allowed = "allowed";
    public const string Blocked = "blocked";
    public const string Paired = "paired";

    // The pairing worked, but the device is not AirPods by Earshot's rule, so nothing was adopted.
    public const string NotAirPods = "not-airpods";

    // The pairing worked, and the device could not be looked at, so it was neither adopted nor called not AirPods.
    public const string Unverified = "unverified";
}

// One step of a switch as the history keeps it: what kind of step, and the raw code. Never an instance id, an address, a
// container or a name.
internal sealed record HistoryStep(string Name, int Code, string CodeName);

// One switch. StartState is the verified state of the nodes just before it began (present-enabled, present-disabled,
// away-flagged, away, mixed, unknown, none-paired); Path is how it went (direct, allow-first, hands-free-assisted,
// windows, none); Retry says the temporary class had its one retry.
internal sealed record ConnectHistoryEntry(
    DateTimeOffset At,
    string Action,
    string StartState,
    string Path,
    IReadOnlyList<HistoryStep> Steps,
    bool Retry,
    string Outcome,
    string FailureClass,
    long DurationMs);

// Every connect, disconnect, Connect in Windows, connection made in Windows and hand-back, one JSON line each, kept 14 days
// (pruned when a line is written). It carries no identifier: no address, container, instance id or device name, so the file
// can be copied into a report as it is. Written by the tray, read by Copy diagnostics and by the Fix offer rule.
internal sealed class ConnectHistory
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(14);
    public const int MaxStepsPerEntry = 24;
    private const int MaxFileBytes = 512 * 1024;

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private readonly Func<string, Mutex> _openLock;

    public ConnectHistory(string path, TimeProvider time, ILog log)
        : this(path, time, log, OpenLockForThisAccount)
    {
    }

    // The same, with the call that opens the append lock given, so a test can make it fail as another account's lock would.
    internal ConnectHistory(string path, TimeProvider time, ILog log, Func<string, Mutex> openLock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(openLock);
        _path = path;
        _time = time;
        _log = log;
        _openLock = openLock;
    }

    public string FilePath => _path;

    // The kind of a step and nothing that names a device: "cm-enable:BTHENUM\{...}\b&..." becomes "cm-enable:service-node",
    // "ks-reconnect:{0.0.0.00000000}.{guid}" becomes "ks-reconnect", and a name with no colon is kept as it is.
    public static string StepKind(string step)
    {
        ArgumentNullException.ThrowIfNull(step);
        int colon = step.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return Safe(step);
        }

        string head = Safe(step[..colon]);
        string tail = step[(colon + 1)..];
        if (tail.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase))
        {
            return head + ":device-node";
        }

        if (tail.StartsWith(@"BTHENUM\", StringComparison.OrdinalIgnoreCase) || tail.StartsWith(@"BTH\", StringComparison.OrdinalIgnoreCase))
        {
            return head + ":service-node";
        }

        if (tail.StartsWith(@"SWD\MMDEVAPI", StringComparison.OrdinalIgnoreCase) || tail.StartsWith("{0.0.", StringComparison.Ordinal))
        {
            return head + ":endpoint";
        }

        return head;
    }

    // Only letters, digits and a few separators survive in a step name, so a stray id cannot ride along in the head.
    private static string Safe(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, 48));
        foreach (char c in text)
        {
            if (sb.Length >= 48)
            {
                break;
            }

            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' ? c : '-');
        }

        return sb.ToString();
    }

    public static IReadOnlyList<HistoryStep> FromSteps(IEnumerable<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.Take(MaxStepsPerEntry).Select(s => new HistoryStep(StepKind(s.Step), s.Code, Safe(s.CodeName))).ToList();
    }

    // Appends the entry and drops what is older than Keep. A failure is logged with its code and returned as a step; the
    // switch it describes is never held up by it.
    //
    // The tray and a fix run both append, from different threads and processes, and an append reads, adds and writes the whole
    // file: a named mutex for this file keeps two appends from losing each other's entry, and each writes its own temp file.
    // The mutex gives the account that runs Earshot full control, so an elevated fix run of the same account makes one the tray
    // can still open. One that cannot be opened (another account's, under Administrator protection) is logged with its code
    // and the entry is written without it.
    // https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex
    // https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutexacl.create
    public StepOutcome Append(ConnectHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using Mutex? mutex = OpenLock();
        bool owned = false;
        if (mutex is not null)
        {
            try
            {
                owned = mutex.WaitOne(AppendWait);
            }
            catch (AbandonedMutexException)
            {
                // The writer that held it ended part way; the file it left is read like any other.
                owned = true;
            }

            if (!owned)
            {
                _log.Warn("The connect history is busy after " + AppendWait.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s, so this entry is written without waiting longer.");
            }
        }

        try
        {
            return AppendLocked(entry);
        }
        finally
        {
            if (owned)
            {
                mutex!.ReleaseMutex();
            }
        }
    }

    private Mutex? OpenLock()
    {
        try
        {
            return _openLock(MutexName(_path));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            _log.Warn("The connect history's lock could not be opened (" + ex.GetType().Name + ", HRESULT 0x" +
                ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "), so this entry is written without it.");
            return null;
        }
    }

    private static Mutex OpenLockForThisAccount(string name)
    {
        var security = new MutexSecurity();
        using (WindowsIdentity self = WindowsIdentity.GetCurrent())
        {
            if (self.User is { } user)
            {
                security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
            }
        }

        return MutexAcl.Create(false, name, out _, security);
    }

    private static readonly TimeSpan AppendWait = TimeSpan.FromSeconds(5);

    // One name per history file, in this session's namespace, from a hash of its path so no path text is in the name.
    private static string MutexName(string path)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToLowerInvariant()));
        return @"Local\Earshot.ConnectHistory." + Convert.ToHexString(hash, 0, 8);
    }

    private StepOutcome AppendLocked(ConnectHistoryEntry entry)
    {
        // A run as administrator writes no history (Program.OpenFixHistory), so this appends as the person. A junction or link
        // on the history file, its folder or the folder above still refuses the write, so an entry never lands somewhere else.
        // The look is not held through the write: a program of the person's could make the junction just after it, and the
        // write would then go only where that person could write anyway.
        if (ReparsePointOnTheWay() is { } redirected)
        {
            _log.Warn("The connect history was not written, because " + redirected + " is a reparse point.");
            return StepOutcomes.NotAttempted("connect-history", redirected + " is a reparse point.");
        }

        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string? folder = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            List<string> lines = ReadLines().Where(l => IsRecent(l)).ToList();
            lines.Add(Serialize(entry));
            while (lines.Count > 1 && lines.Sum(l => l.Length + 2) > MaxFileBytes)
            {
                lines.RemoveAt(0);
            }

            File.WriteAllLines(temp, lines, new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
            return StepOutcomes.FromWin32("connect-history", 0, "written");
        }
        catch (IOException ex)
        {
            DeleteTemp(temp);
            return Failed(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            DeleteTemp(temp);
            return Failed(ex);
        }
    }

    // A temp file a failed append left is removed, so failures do not pile files up beside the history. A delete that fails is
    // logged with its code; the append's own failure is what is returned.
    private void DeleteTemp(string temp)
    {
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("The connect history's temp file could not be deleted (" + ex.GetType().Name + ", HRESULT 0x" +
                ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ").");
        }
    }

    // The first of the history file, its folder and the folder above that is a reparse point, or null. One that is not there
    // is not a reparse point; a look that fails counts as one, so nothing is written through what could not be looked at.
    private string? ReparsePointOnTheWay()
    {
        string? folder = System.IO.Path.GetDirectoryName(_path);
        string? parent = folder is null ? null : System.IO.Path.GetDirectoryName(folder);
        foreach (string? path in new[] { parent, folder, _path })
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            try
            {
                FileSystemInfo info = new FileInfo(path);
                if (!info.Exists)
                {
                    info = new DirectoryInfo(path);
                }

                if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return path;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("The connect history's folder could not be looked at (" + ex.GetType().Name + ", HRESULT 0x" +
                    ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ").");
                return path;
            }
        }

        return null;
    }

    private StepOutcome Failed(Exception ex)
    {
        StepOutcome step = StepOutcomes.FromHResult("connect-history", ex.HResult, ex.GetType().Name + ": " + ex.Message, ok: false);
        _log.Warn("The connect history could not be written. " + step.CodeName + " " + ex.Message);
        return step;
    }

    // The entries not older than Keep, oldest first.
    public IReadOnlyList<ConnectHistoryEntry> Read()
    {
        var entries = new List<ConnectHistoryEntry>();
        foreach (string line in ReadLines())
        {
            if (!TryParse(line, out ConnectHistoryEntry? entry))
            {
                _log.Warn("A line of the connect history was not valid and was skipped.");
                continue;
            }

            if (_time.GetUtcNow() - entry.At <= Keep)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    // The last Keep of history as lines of text for Copy diagnostics. Nothing in it names a device.
    public IReadOnlyList<string> ReadForDiagnostics() => Read().Select(Describe).ToList();

    internal static string Describe(ConnectHistoryEntry e) =>
        e.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + " " + e.Action +
        " start=" + e.StartState + " path=" + e.Path + (e.Retry ? " retried" : "") + " outcome=" + e.Outcome +
        (e.FailureClass.Length > 0 && e.FailureClass != "None" ? " class=" + e.FailureClass : "") +
        " " + e.DurationMs.ToString(CultureInfo.InvariantCulture) + " ms steps=" +
        string.Join("; ", e.Steps.Select(s => s.Name + " " + s.CodeName + " 0x" + s.Code.ToString("X8", CultureInfo.InvariantCulture)));

    private List<string> ReadLines()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return File.ReadAllLines(_path).Where(l => l.Length > 0).ToList();
        }
        catch (IOException ex)
        {
            _log.Warn("The connect history could not be read. " + ex.Message);
            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("The connect history could not be read. " + ex.Message);
            return [];
        }
    }

    private bool IsRecent(string line) =>
        TryParse(line, out ConnectHistoryEntry? entry) && _time.GetUtcNow() - entry.At <= Keep;

    private static string Serialize(ConnectHistoryEntry e)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            // time, step, outcome, code, attempt, durationMs and reason are the plain tokens the doctor report keeps from a line.
            w.WriteString("time", e.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            w.WriteString("step", Safe(e.Action));
            w.WriteString("outcome", Safe(e.Outcome));
            w.WriteString("code", Safe(e.Steps.FirstOrDefault(st => st.Code != 0)?.CodeName ?? "S_OK"));
            w.WriteNumber("attempt", e.Retry ? 2 : 1);
            w.WriteNumber("durationMs", e.DurationMs);
            w.WriteString("reason", Safe(e.FailureClass.Length == 0 ? "None" : e.FailureClass));
            w.WriteString("start", Safe(e.StartState));
            w.WriteString("path", Safe(e.Path));
            w.WriteStartArray("steps");
            foreach (HistoryStep s in e.Steps.Take(MaxStepsPerEntry))
            {
                w.WriteStartObject();
                w.WriteString("n", Safe(s.Name));
                w.WriteNumber("c", s.Code);
                w.WriteString("cn", Safe(s.CodeName));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool TryParse(string line, out ConnectHistoryEntry entry)
    {
        entry = null!;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object ||
                !DateTimeOffset.TryParseExact(r.GetProperty("time").GetString(), "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at))
            {
                return false;
            }

            var steps = new List<HistoryStep>();
            foreach (JsonElement s in r.GetProperty("steps").EnumerateArray())
            {
                steps.Add(new HistoryStep(s.GetProperty("n").GetString() ?? "", s.GetProperty("c").GetInt32(), s.GetProperty("cn").GetString() ?? ""));
            }

            entry = new ConnectHistoryEntry(
                at, r.GetProperty("step").GetString() ?? "", r.GetProperty("start").GetString() ?? "", r.GetProperty("path").GetString() ?? "",
                steps, r.GetProperty("attempt").GetInt32() > 1, r.GetProperty("outcome").GetString() ?? "", r.GetProperty("reason").GetString() ?? "",
                r.GetProperty("durationMs").GetInt64());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
