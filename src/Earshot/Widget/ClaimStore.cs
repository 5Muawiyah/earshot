using System.Text.Json;
using Earshot.Contracts;

namespace Earshot.Widget;

// claim.json, read once at construction and after every save. A file that is missing, unreadable or not
// valid reads as "no claim", is logged with the reason, and is left exactly where it is: unlike
// settings.json there is no backup and no quarantine, since losing a claim only costs the owner re-running
// the claim from his own case, not a settings reset.
internal sealed class ClaimStore
{
    private readonly string _path;
    private readonly ILog _log;
    private readonly Lock _gate = new();
    private WidgetClaim? _current;

    public ClaimStore(string path, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);

        _path = path;
        _log = log;
        lock (_gate)
        {
            _current = Load();
        }
    }

    public WidgetClaim? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Save(WidgetClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_gate)
        {
            WriteAtomic(claim);
            _current = Load();
        }
    }

    public void ForgetClaim()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException ex)
            {
                _log.Error("Could not delete the widget claim: " + _path, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Error("Could not delete the widget claim: " + _path, ex);
            }

            _current = null;
        }
    }

    private WidgetClaim? Load()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_path);
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
            _log.Warn("The widget claim could not be read, so nothing is claimed: " + _path + " (" + ex.Message + ")");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("The widget claim could not be read, so nothing is claimed: " + _path + " (" + ex.Message + ")");
            return null;
        }

        try
        {
            WidgetClaim? claim = JsonSerializer.Deserialize(bytes, WidgetJsonContext.Default.WidgetClaim);
            if (claim is null)
            {
                _log.Warn("The widget claim file holds null, so nothing is claimed: " + _path);
                return null;
            }

            return claim;
        }
        catch (JsonException ex)
        {
            _log.Warn("The widget claim file is not valid, so nothing is claimed: " + _path + " (" + ex.Message + ")");
            return null;
        }
    }

    private void WriteAtomic(WidgetClaim claim)
    {
        string folder = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(folder);
        string tempPath = _path + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, claim, WidgetJsonContext.Default.WidgetClaim);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(_path))
        {
            File.Replace(tempPath, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, _path, overwrite: true);
        }
    }
}
