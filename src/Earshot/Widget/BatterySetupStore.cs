using System.Text.Json;
using Earshot.Contracts;

namespace Earshot.Widget;

// widget\setup\*.json: one file per completed set-up, written the atomic way ClaimStore writes (a temp file,
// flushed, then moved into place) and never rewritten or deleted by the app.
internal sealed class BatterySetupStore
{
    private const string FilePattern = "setup-*.json";

    private readonly string _folder;
    private readonly ILog _log;

    public BatterySetupStore(string folder, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(log);
        _folder = folder;
        _log = log;
    }

    public string Folder => _folder;

    // Returns the file name written, or null when the file could not be written (logged with the raw code).
    // A file already there is never replaced: a record is history.
    public string? Save(BatterySetupRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        string name = record.FileName;
        string target = Path.Combine(_folder, name);
        string temp = target + ".tmp";
        try
        {
            Directory.CreateDirectory(_folder);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, SetupJsonContext.Default.BatterySetupRecord);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, target, overwrite: false);
            return name;
        }
        catch (IOException ex)
        {
            _log.Warn("The battery set-up record could not be saved (0x" + ex.HResult.ToString("X8") + "): " + target);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("The battery set-up record could not be saved (0x" + ex.HResult.ToString("X8") + "): " + target);
            return null;
        }
    }

    // Every record that reads and validates, in file-name order. One that does not is skipped, logged with the
    // reason and the file name, and left where it is.
    public IReadOnlyList<BatterySetupRecord> LoadAll()
    {
        var records = new List<BatterySetupRecord>();
        string[] files;
        try
        {
            if (!Directory.Exists(_folder))
            {
                return records;
            }

            files = Directory.GetFiles(_folder, FilePattern);
        }
        catch (IOException ex)
        {
            _log.Warn("The battery set-up folder could not be read (0x" + ex.HResult.ToString("X8") + "): " + _folder);
            return records;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("The battery set-up folder could not be read (0x" + ex.HResult.ToString("X8") + "): " + _folder);
            return records;
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (IOException ex)
            {
                _log.Warn("A battery set-up record could not be read and is skipped (0x" + ex.HResult.ToString("X8") + "): " + name);
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Warn("A battery set-up record could not be read and is skipped (0x" + ex.HResult.ToString("X8") + "): " + name);
                continue;
            }

            BatterySetupRecord? record;
            try
            {
                record = JsonSerializer.Deserialize(bytes, SetupJsonContext.Default.BatterySetupRecord);
            }
            catch (JsonException ex)
            {
                _log.Warn("A battery set-up record is not valid and is skipped (" + ex.Message + "): " + name);
                continue;
            }

            if (record is null)
            {
                _log.Warn("A battery set-up record holds null and is skipped: " + name);
                continue;
            }

            string? problem = record.Problem();
            if (problem is not null)
            {
                _log.Warn("A battery set-up record is skipped because of " + problem + ": " + name);
                continue;
            }

            records.Add(record);
        }

        return records;
    }
}
