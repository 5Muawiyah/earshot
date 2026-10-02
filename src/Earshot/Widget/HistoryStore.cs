using System.Text.Json;
using System.Text.Json.Serialization;
using Earshot.Contracts;

namespace Earshot.Widget;

// One kept sample: a part, its level and charging flag as heard live, and when. Nothing else: no address, no name, no model.
internal sealed record HistorySample(ChargeComponent Part, int Percent, bool Charging, DateTimeOffset At);

// A stretch of one part's history with nothing heard in it: the two samples either side of it.
internal sealed record HistoryGap(ChargeComponent Part, DateTimeOffset From, DateTimeOffset To);

// What a 24 hour window holds: the samples (oldest first) and the gaps between a part's samples.
internal sealed record HistoryWindow(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<HistorySample> Samples, IReadOnlyList<HistoryGap> Gaps);

// Where the live readings of the linked pair are kept for the history page. WidgetStatusService records into it.
internal interface IHistoryStore
{
    // Keeps what a live reading of the linked set says of each part it gives a level for. Never throws for a file problem.
    void Record(DecodedReading reading, DateTimeOffset at);

    // The samples and gaps of the 24 hours ending at end, for the history page.
    HistoryWindow Query(DateTimeOffset end);
}

// The battery history as one small JSON file, battery-history.json in the widget folder (Paths.BatteryHistoryFile, under
// EARSHOT_DATA_ROOT when that redirects Earshot's folders). It holds live readings only, of the linked pair only (the
// caller hands in only those): never a last reading, an estimate or Windows' figure. Each sample is a part, a level, a
// charging flag and a time, and the file's shape has no member an address, a name or a model could go in.
//
// Design choices, none of them from a source: at most one sample per part per minute (SampleInterval; readings come
// several a second and a chart of a day cannot show more); nothing older than 7 days is kept (Retention), dropped when the
// file is loaded and on each write; a gap is more than 10 minutes between two samples of a part (GapAfter), so the page
// can say "not heard" for a lid that was shut or a PC that was away instead of drawing a line across it.
//
// Load and save follow LastReadingStore: a missing file is a fresh start; a file that cannot be read (locked) or parsed
// (corrupt, cut short) is no history, logged once, and is not replaced while it is in that state, since a lock is often
// brief and the file may hold what cannot be had again (each write looks at it again, and when it can be read what it holds
// is joined with what was heard meanwhile); a file of a newer schema is left alone for the run. A write goes to
// battery-history.json.tmp and is moved over the file (File.Move with overwrite replaces it in one step on one volume,
// https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move), so a cut-short write leaves the old file whole.
internal sealed class HistoryStore : IHistoryStore
{
    public const int SchemaVersion = 1;

    public static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static readonly TimeSpan GapAfter = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan WindowLength = TimeSpan.FromHours(24);

    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<HistorySample> _samples = [];
    private bool _loaded;
    private bool _newerSchema;
    private bool _unreadable;
    private bool _unreadableLogged;

    public HistoryStore(string path, ILog log, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        FilePath = Path.GetFullPath(path);
        TempPath = FilePath + ".tmp";
        _log = log;
        _time = time;
    }

    public string FilePath { get; }

    public string TempPath { get; }

    // Reads the file, once, and drops what is older than the retention. Record and Query do it first if it has not been.
    // Does not write: the pruned history reaches the file with the next sample.
    public void Load()
    {
        lock (_gate)
        {
            LoadLocked();
        }
    }

    public void Record(DecodedReading reading, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(reading);
        lock (_gate)
        {
            LoadLocked();
            bool added = false;
            foreach ((ChargeComponent part, PartReading value) in new[]
            {
                (ChargeComponent.Left, reading.Left), (ChargeComponent.Right, reading.Right), (ChargeComponent.Case, reading.Case),
            })
            {
                if (value.Percent is int percent && !TooSoonLocked(part, at))
                {
                    _samples.Add(new HistorySample(part, Math.Clamp(percent, 0, 100), value.Charging == true, at));
                    added = true;
                }
            }

            if (added)
            {
                PruneLocked(at);
                SaveLocked();
            }
        }
    }

    // The samples and gaps of the 24 hours ending at end (the window's start is excluded, its end included).
    public HistoryWindow Query(DateTimeOffset end)
    {
        lock (_gate)
        {
            LoadLocked();
            return Window(_samples, end);
        }
    }

    // Pure: the window of a list of samples. Gaps are between a part's own consecutive samples inside the window.
    public static HistoryWindow Window(IReadOnlyList<HistorySample> samples, DateTimeOffset end)
    {
        ArgumentNullException.ThrowIfNull(samples);
        DateTimeOffset start = end - WindowLength;
        List<HistorySample> inside = samples.Where(s => s.At > start && s.At <= end).OrderBy(s => s.At).ToList();
        var gaps = new List<HistoryGap>();
        foreach (ChargeComponent part in Enum.GetValues<ChargeComponent>())
        {
            HistorySample? previous = null;
            foreach (HistorySample sample in inside.Where(s => s.Part == part))
            {
                if (previous is not null && sample.At - previous.At > GapAfter)
                {
                    gaps.Add(new HistoryGap(part, previous.At, sample.At));
                }

                previous = sample;
            }
        }

        return new HistoryWindow(start, end, inside, gaps.OrderBy(g => g.From).ToList());
    }

    private bool TooSoonLocked(ChargeComponent part, DateTimeOffset at)
    {
        for (int i = _samples.Count - 1; i >= 0; i--)
        {
            if (_samples[i].Part == part)
            {
                // A clock put back is not a new minute either: it would put a sample before one already kept.
                return at < _samples[i].At + SampleInterval;
            }
        }

        return false;
    }

    private void PruneLocked(DateTimeOffset now) =>
        _samples.RemoveAll(s => s.At < now - Retention);

    private void LoadLocked()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _samples.AddRange(ReadLocked(quiet: false));
        _samples.Sort((a, b) => a.At.CompareTo(b.At));
        PruneLocked(_time.GetUtcNow());
    }

    // The samples the file holds, and whether it could be read, in _unreadable.
    private List<HistorySample> ReadLocked(bool quiet)
    {
        _unreadable = false;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(FilePath);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (IOException ex)
        {
            _unreadable = true;
            if (!quiet)
            {
                _log.Warn("Widget: the battery history could not be read (0x" + ex.HResult.ToString("X8") + "). It is not written over.", ex);
            }

            return [];
        }
        catch (UnauthorizedAccessException ex)
        {
            _unreadable = true;
            if (!quiet)
            {
                _log.Warn("Widget: the battery history could not be read (0x" + ex.HResult.ToString("X8") + "). It is not written over.", ex);
            }

            return [];
        }

        HistoryFileData? data;
        try
        {
            data = JsonSerializer.Deserialize(bytes, HistoryJsonContext.Default.HistoryFileData);
        }
        catch (JsonException ex)
        {
            _unreadable = true;
            if (!quiet)
            {
                _log.Warn("Widget: the battery history file is not usable. It is left as it is until it is read or removed.", ex);
            }

            return [];
        }

        if (data is null)
        {
            _unreadable = true;
            if (!quiet)
            {
                _log.Warn("Widget: the battery history file is not usable. It is left as it is until it is read or removed.");
            }

            return [];
        }

        if (data.SchemaVersion > SchemaVersion)
        {
            _newerSchema = true;
            _log.Warn("Widget: the battery history file is from a newer Earshot (schema " + data.SchemaVersion + "). It is left as it is.");
            return [];
        }

        var kept = new List<HistorySample>();
        foreach (SampleData item in data.Samples ?? [])
        {
            if (ToSample(item) is { } sample)
            {
                kept.Add(sample);
            }
        }

        return kept;
    }

    private void SaveLocked()
    {
        if (_newerSchema)
        {
            return;
        }

        if (_unreadable)
        {
            // Look again: a lock is brief, and a file that is gone is a fresh start. What it holds is joined with what was
            // heard since, so a lock that lifts loses neither.
            List<HistorySample> found = ReadLocked(quiet: true);
            if (_newerSchema)
            {
                return;
            }

            if (_unreadable)
            {
                if (!_unreadableLogged)
                {
                    _unreadableLogged = true;
                    _log.Warn("Widget: the battery history is not saved while the file cannot be read. It stays in memory for this run.");
                }

                return;
            }

            foreach (HistorySample sample in found)
            {
                if (!_samples.Any(s => s.Part == sample.Part && s.At == sample.At))
                {
                    _samples.Add(sample);
                }
            }

            _samples.Sort((a, b) => a.At.CompareTo(b.At));
            PruneLocked(_time.GetUtcNow());
        }

        var data = new HistoryFileData
        {
            SchemaVersion = SchemaVersion,
            Samples = _samples.Select(s => new SampleData
            {
                Part = PartName(s.Part),
                Percent = s.Percent,
                Charging = s.Charging,
                At = s.At.ToUnixTimeSeconds(),
            }).ToList(),
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, HistoryJsonContext.Default.HistoryFileData);
                stream.Flush(flushToDisk: true);
            }

            File.Move(TempPath, FilePath, overwrite: true);
        }
        catch (IOException ex)
        {
            _log.Warn("Widget: the battery history could not be saved (0x" + ex.HResult.ToString("X8") + "). It stays in memory for this run.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("Widget: the battery history could not be saved (0x" + ex.HResult.ToString("X8") + "). It stays in memory for this run.", ex);
        }
    }

    private static string PartName(ChargeComponent part) => part switch
    {
        ChargeComponent.Left => "left",
        ChargeComponent.Right => "right",
        _ => "case",
    };

    // A sample is kept only when it names a part and its level is 0 to 100.
    private static HistorySample? ToSample(SampleData data)
    {
        ChargeComponent? part = data.Part switch
        {
            "left" => ChargeComponent.Left,
            "right" => ChargeComponent.Right,
            "case" => ChargeComponent.Case,
            _ => null,
        };

        return part is ChargeComponent known && data.Percent is >= 0 and <= 100
            ? new HistorySample(known, data.Percent, data.Charging, DateTimeOffset.FromUnixTimeSeconds(data.At))
            : null;
    }
}

// The file's shape: every member is a name of a part, a level, a flag or a time.
internal sealed class HistoryFileData
{
    public int SchemaVersion { get; set; }

    public List<SampleData>? Samples { get; set; }
}

// Times are whole seconds since 1970 (UTC), to keep a week of samples small.
internal sealed class SampleData
{
    public string Part { get; set; } = "";

    public int Percent { get; set; }

    public bool Charging { get; set; }

    public long At { get; set; }
}

// Source-generated, as reflection-based serialization is off for the whole app (SettingsJsonContext says why). Not
// indented: a week of samples is the file's whole size.
[JsonSerializable(typeof(HistoryFileData))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext
{
}
