using System.Text.Json;
using System.Text.Json.Serialization;
using Earshot.Contracts;

namespace Earshot.Widget;

// Where the link memory is kept between runs.
internal interface ILinkMemoryStore
{
    // What was saved, or null when nothing usable was. Never throws for a file that is missing or unusable.
    LinkMemory? Load();

    // Writes the memory, or removes the file when memory is null (the link was forgotten). A failure is logged with its
    // code and the memory stays in memory for this run.
    void Save(LinkMemory? memory);
}

// The memory as one small JSON file, link.json beside last-reading.json in the widget folder. It holds the paired model and
// colour and two times: no address, no tag, no name and nothing else about any sender, which the shape of the file itself
// rules out.
//
// Load: a missing file is no memory. A file that cannot be read, parsed, is of another schema or holds a value out of range
// is no memory too, logged with the raw reason; it grants nothing, so the next save simply replaces it.
//
// Save writes link.json.tmp, then moves it over the file (File.Move with overwrite replaces it in one step on one volume,
// https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move), so a cut-short save leaves the old file whole. Saving
// nothing deletes the file, so a forgotten link is not remembered after a restart.
internal sealed class LinkMemoryStore : ILinkMemoryStore
{
    public const int SchemaVersion = 1;

    private readonly ILog _log;
    private readonly Lock _gate = new();

    public LinkMemoryStore(string path, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);
        FilePath = Path.GetFullPath(path);
        TempPath = FilePath + ".tmp";
        _log = log;
    }

    public string FilePath { get; }

    public string TempPath { get; }

    public LinkMemory? Load()
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
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException ex)
            {
                _log.Warn("Widget: the remembered link (link.json) could not be read (0x" + ex.HResult.ToString("X8") + "). No link is remembered.", ex);
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Warn("Widget: the remembered link (link.json) could not be read (0x" + ex.HResult.ToString("X8") + "). No link is remembered.", ex);
                return null;
            }

            LinkMemoryFileData? data;
            try
            {
                data = JsonSerializer.Deserialize(bytes, LinkMemoryJsonContext.Default.LinkMemoryFileData);
            }
            catch (JsonException ex)
            {
                _log.Warn("Widget: the remembered link (link.json) is not usable: " + ex.Message + " No link is remembered.", ex);
                return null;
            }

            if (data is null)
            {
                _log.Warn("Widget: the remembered link (link.json) is empty. No link is remembered.");
                return null;
            }

            if (data.SchemaVersion != SchemaVersion)
            {
                _log.Warn("Widget: the remembered link (link.json) is of schema " + data.SchemaVersion + ", not " + SchemaVersion + ". No link is remembered.");
                return null;
            }

            if (data.Model is <= 0 or > ushort.MaxValue || data.Colour is < 0 or > byte.MaxValue || data.LastHeardAt < data.LinkedAt)
            {
                _log.Warn("Widget: the remembered link (link.json) holds a value out of range. No link is remembered.");
                return null;
            }

            return new LinkMemory((ushort)data.Model, (byte)data.Colour, data.LinkedAt, data.LastHeardAt);
        }
    }

    public void Save(LinkMemory? memory)
    {
        lock (_gate)
        {
            try
            {
                if (memory is null)
                {
                    File.Delete(FilePath);
                    return;
                }

                var data = new LinkMemoryFileData
                {
                    SchemaVersion = SchemaVersion,
                    Model = memory.Model,
                    Colour = memory.Colour,
                    LinkedAt = memory.LinkedAt,
                    LastHeardAt = memory.LastHeardAt,
                };

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, data, LinkMemoryJsonContext.Default.LinkMemoryFileData);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(TempPath, FilePath, overwrite: true);
            }
            catch (IOException ex)
            {
                _log.Warn("Widget: the remembered link (link.json) could not be saved (0x" + ex.HResult.ToString("X8") + "). It stays in memory for this run.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Warn("Widget: the remembered link (link.json) could not be saved (0x" + ex.HResult.ToString("X8") + "). It stays in memory for this run.", ex);
            }
        }
    }
}

// The file's shape. Every member is a value or a time: there is no member an address or a name could go in.
internal sealed class LinkMemoryFileData
{
    public int SchemaVersion { get; set; }

    public int Model { get; set; }

    public int Colour { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    public DateTimeOffset LastHeardAt { get; set; }
}

// Source-generated, as reflection-based serialization is off for the whole app (SettingsJsonContext says why).
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LinkMemoryFileData))]
internal sealed partial class LinkMemoryJsonContext : JsonSerializerContext
{
}
