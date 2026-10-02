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
// Load: a missing file is the empty book. A file that cannot be read or parsed is the empty book too, logged once, and is
// replaced at the next save; an entry that is out of range (a percent outside 0 to 100, a model of 0, a rate that is not a
// positive number) is left out on its own. A file of a newer schema is left alone: its book is not read, and nothing is
// written over it in this run.
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
                _log.Warn("Widget: the last battery readings could not be read (0x" + ex.HResult.ToString("X8") + "). None are shown until new ones are heard.", ex);
                return LastReadingBook.Empty;
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Warn("Widget: the last battery readings could not be read (0x" + ex.HResult.ToString("X8") + "). None are shown until new ones are heard.", ex);
                return LastReadingBook.Empty;
            }

            LastReadingFileData? data;
            try
            {
                data = JsonSerializer.Deserialize(bytes, LastReadingJsonContext.Default.LastReadingFileData);
            }
            catch (JsonException ex)
            {
                _log.Warn("Widget: the last battery readings file is not usable and will be replaced at the next save.", ex);
                return LastReadingBook.Empty;
            }

            if (data is null)
            {
                return LastReadingBook.Empty;
            }

            if (data.SchemaVersion > SchemaVersion)
            {
                _newerSchema = true;
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

            return new LastReadingBook(ToReading(data.Left), ToReading(data.Right), ToReading(data.Case), rates);
        }
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
        };

        lock (_gate)
        {
            if (_newerSchema)
            {
                return;
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
