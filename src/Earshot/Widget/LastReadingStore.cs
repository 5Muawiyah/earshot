using System.Text.Json;
using System.Text.Json.Serialization;
using Earshot.Contracts;

namespace Earshot.Widget;

// Where the last readings of the owner's pair and the learned charge rates are kept between runs.
internal interface ILastReadingStore
{
    // What was saved, or the empty book when nothing usable was. Never throws for a file that is missing or unusable.
    LastReadingBook Load();

    // Writes the book. A failure is logged with its code and the book stays in memory for this run.
    void Save(LastReadingBook book);
}

// The book as one small JSON file, last-reading.json in the widget folder (%LOCALAPPDATA%\Earshot\widget, or under
// EARSHOT_DATA_ROOT when that redirects Earshot's folders; Paths.LastReadingFile). It holds each part's value, charging
// flag, read time and model, and each learned rate's model, part, rate, time and span: no address, no name and nothing
// else about any sender, which the shape of the file itself rules out.
//
// Load: a missing file is the empty book, and a fresh start. A file that cannot be read (locked, no access) or parsed
// (corrupt, cut short) is the empty book too, logged once, but it is not replaced by a save while it is in that state: it
// may hold readings that cannot be had again, and a lock is often brief. Each save looks at it again first, and goes on
// once it can be read or is gone (deleting it is the deliberate fresh start). An entry that is out of range (a percent
// outside 0 to 100, a model of 0, a rate that is not a positive number) is left out on its own. A file of a newer schema
// is left alone: its book is not read, and nothing is written over it in this run.
//
// Order. The service takes each book to write with a sequence number, under its own lock, and the writes may reach the
// file on different threads in another order. A book with a sequence lower than one already written is dropped, so an
// older book is never put over a newer.
//
// Save writes last-reading.json.tmp, then moves it over the file (File.Move with overwrite replaces it in one step on one
// volume, https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move), so a cut-short save leaves the old file whole.
internal sealed class LastReadingStore : ILastReadingStore
{
    public const int SchemaVersion = 1;

    // A value, a charging flag, a model or a rate that changes is written at once. A read time alone changes with every
    // message (several a second with the lid open), so it is written at most this often; a restart in between shows the
    // reading up to this much older than it was. Design choice.
    public static readonly TimeSpan ReadTimeWriteInterval = TimeSpan.FromMinutes(1);

    // A rate above this is not a charge: the seed is 150 points an hour, and a learned one many times that is a misread.
    // Design choice.
    private const double MaxPercentPerHour = 1000;

    private readonly ILog _log;
    private readonly Lock _gate = new();
    private bool _newerSchema;
    private bool _unreadable;
    private bool _unreadableLogged;
    private long _lastSequence;

    public LastReadingStore(string path, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);
        FilePath = Path.GetFullPath(path);
        TempPath = FilePath + ".tmp";
        _log = log;
    }

    public string FilePath { get; }

    public string TempPath { get; }

    public LastReadingBook Load()
    {
        lock (_gate)
        {
            return ReadLocked(quiet: false);
        }
    }

    // Reads the file and says whether it could be, in _unreadable. Quiet when it is a look before a save, which has its own
    // line to log.
    private LastReadingBook ReadLocked(bool quiet)
    {
        _unreadable = false;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(FilePath);
        }
        catch (FileNotFoundException)
        {
            return LastReadingBook.Empty;
        }
        catch (DirectoryNotFoundException)
        {
            return LastReadingBook.Empty;
        }
        catch (IOException ex)
        {
            _unreadable = true;
            if (!quiet)
            {
                _log.Warn("Widget: the last battery readings could not be read (0x" + ex.HResult.ToString("X8") + "). None are shown until new ones are heard, and the file is not written over.", ex);
            }

            return LastReadingBook.Empty;
        }
        catch (UnauthorizedAccessException ex)
        {
            _unreadable = true;
            if (!quiet)
            {
                _log.Warn("Widget: the last battery readings could not be read (0x" + ex.HResult.ToString("X8") + "). None are shown until new ones are heard, and the file is not written over.", ex);
            }

            return LastReadingBook.Empty;
        }

        LastReadingFileData? data;
        try
        {
            data = JsonSerializer.Deserialize(bytes, LastReadingJsonContext.Default.LastReadingFileData);
        }
        catch (JsonException ex)
        {
            _unreadable = true;
            if (!quiet)
            {
                _log.Warn("Widget: the last battery readings file is not usable. It is left as it is until it is read or removed.", ex);
            }

            return LastReadingBook.Empty;
        }

        if (data is null)
        {
            _unreadable = true;
            if (!quiet)
            {
                _log.Warn("Widget: the last battery readings file is not usable. It is left as it is until it is read or removed.");
            }

            return LastReadingBook.Empty;
        }

        if (data.SchemaVersion > SchemaVersion)
        {
            _newerSchema = true;
            _unreadable = false;
            _log.Warn("Widget: the last battery readings file is from a newer Earshot (schema " + data.SchemaVersion + "). It is left as it is.");
            return LastReadingBook.Empty;
        }

        var rates = new List<LearnedRate>();
        foreach (RateData rate in data.Rates ?? [])
        {
            if (ToRate(rate) is { } usable && !rates.Any(r => r.Model == usable.Model && r.Part == usable.Part))
            {
                rates.Add(usable);
            }
        }

        var marks = new List<EstimateMark>();
        foreach (MarkData mark in data.Marks ?? [])
        {
            if (ToMark(mark) is { } usable && !marks.Any(m => m.Component == usable.Component))
            {
                marks.Add(usable);
            }
        }

        return new LastReadingBook(ToReading(data.Left), ToReading(data.Right), ToReading(data.Case), rates) { Marks = marks };
    }

    public void Save(LastReadingBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        var data = new LastReadingFileData
        {
            SchemaVersion = SchemaVersion,
            Left = ToData(book.Left),
            Right = ToData(book.Right),
            Case = ToData(book.Case),
            Rates = book.Rates.Select(r => new RateData
            {
                Model = r.Model,
                Part = r.Part == ChargePart.Case ? PartCase : PartBud,
                PercentPerHour = r.PercentPerHour,
                MeasuredAt = r.MeasuredAt,
                SpanMinutes = r.Span.TotalMinutes,
            }).ToList(),
            Marks = book.Marks.Select(m => new MarkData
            {
                Part = PartName(m.Component),
                ReadAt = m.ReadAt,
                ReadPercent = m.ReadPercent,
                Percent = m.Percent,
            }).ToList(),
        };

        lock (_gate)
        {
            if (_newerSchema)
            {
                return;
            }

            if (book.Sequence != 0 && book.Sequence < _lastSequence)
            {
                return; // a newer book has been written since this one was taken
            }

            if (_unreadable)
            {
                // Look again: a lock is brief, and a file that is gone is a fresh start.
                ReadLocked(quiet: true);
                if (_newerSchema)
                {
                    return;
                }

                if (_unreadable)
                {
                    if (!_unreadableLogged)
                    {
                        _unreadableLogged = true;
                        _log.Warn("Widget: the last battery readings are not saved while the file cannot be read. They stay in memory for this run.");
                    }

                    return;
                }
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, data, LastReadingJsonContext.Default.LastReadingFileData);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(TempPath, FilePath, overwrite: true);
                _lastSequence = Math.Max(_lastSequence, book.Sequence);
            }
            catch (IOException ex)
            {
                _log.Warn("Widget: the last battery readings could not be saved (0x" + ex.HResult.ToString("X8") + "). They stay in memory for this run.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Warn("Widget: the last battery readings could not be saved (0x" + ex.HResult.ToString("X8") + "). They stay in memory for this run.", ex);
            }
        }
    }

    private const string PartBud = "bud";
    private const string PartCase = "case";
    private const string PartLeft = "left";
    private const string PartRight = "right";

    private static string PartName(ChargeComponent component) => component switch
    {
        ChargeComponent.Left => PartLeft,
        ChargeComponent.Right => PartRight,
        _ => PartCase,
    };

    // A mark is kept only when it says an estimate rose above its reading, to at most 100.
    private static EstimateMark? ToMark(MarkData data)
    {
        ChargeComponent? component = data.Part switch
        {
            PartLeft => ChargeComponent.Left,
            PartRight => ChargeComponent.Right,
            PartCase => ChargeComponent.Case,
            _ => null,
        };

        return component is ChargeComponent known && data.ReadPercent is >= 0 and < 100 && data.Percent > data.ReadPercent && data.Percent <= 100
            ? new EstimateMark(known, data.ReadAt, data.ReadPercent, data.Percent)
            : null;
    }

    private static SavedReading? ToReading(SavedReadingData? data) =>
        data is null || data.Percent is < 0 or > 100 || data.Model is <= 0 or > ushort.MaxValue
            ? null
            : new SavedReading(data.Percent, data.Charging, data.ReadAt, (ushort)data.Model);

    private static SavedReadingData? ToData(SavedReading? reading) =>
        reading is null ? null : new SavedReadingData { Percent = reading.Percent, Charging = reading.Charging, ReadAt = reading.ReadAt, Model = reading.Model };

    private static LearnedRate? ToRate(RateData data)
    {
        ChargePart? part = data.Part switch
        {
            PartBud => ChargePart.Bud,
            PartCase => ChargePart.Case,
            _ => null,
        };

        if (part is not ChargePart known || data.Model is <= 0 or > ushort.MaxValue ||
            !double.IsFinite(data.PercentPerHour) || data.PercentPerHour <= 0 || data.PercentPerHour > MaxPercentPerHour ||
            !double.IsFinite(data.SpanMinutes) || data.SpanMinutes < 0)
        {
            return null;
        }

        return new LearnedRate((ushort)data.Model, known, data.PercentPerHour, data.MeasuredAt, TimeSpan.FromMinutes(data.SpanMinutes));
    }
}

// The file's shape. Every member is a value or a time: there is no member an address or a name could go in.
internal sealed class LastReadingFileData
{
    public int SchemaVersion { get; set; }

    public SavedReadingData? Left { get; set; }

    public SavedReadingData? Right { get; set; }

    public SavedReadingData? Case { get; set; }

    public List<RateData>? Rates { get; set; }

    public List<MarkData>? Marks { get; set; }
}

// The highest estimate shown for a saved reading of a part: which part, the reading's time and value, and the estimate.
internal sealed class MarkData
{
    public string Part { get; set; } = "";

    public DateTimeOffset ReadAt { get; set; }

    public int ReadPercent { get; set; }

    public int Percent { get; set; }
}

internal sealed class SavedReadingData
{
    public int Percent { get; set; }

    public bool Charging { get; set; }

    public DateTimeOffset ReadAt { get; set; }

    public int Model { get; set; }
}

internal sealed class RateData
{
    public int Model { get; set; }

    public string Part { get; set; } = "";

    public double PercentPerHour { get; set; }

    public DateTimeOffset MeasuredAt { get; set; }

    public double SpanMinutes { get; set; }
}

// Source-generated, as reflection-based serialization is off for the whole app (SettingsJsonContext says why).
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LastReadingFileData))]
internal sealed partial class LastReadingJsonContext : JsonSerializerContext
{
}
