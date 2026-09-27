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
    private readonly Func<sbyte?> _currentSignalThreshold;
    private readonly Lock _gate = new();
    private WidgetClaim? _current;

    public ClaimStore(string path, ILog log)
        : this(path, log, static () => WidgetDefaults.SignalThresholdDbm)
    {
    }

    // Test-only: supplies the "phase 0 has proved this threshold" comparison directly, so a claim's
    // threshold validation (M1) can be exercised without WidgetDefaults.SignalThresholdDbm ever holding
    // anything but its shipped null, the way ClaimFlow's internal overload does for the same constant.
    internal ClaimStore(string path, ILog log, Func<sbyte?> currentSignalThreshold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(currentSignalThreshold);

        _path = path;
        _log = log;
        _currentSignalThreshold = currentSignalThreshold;
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

        WidgetClaim claim;
        try
        {
            WidgetClaim? deserialized = JsonSerializer.Deserialize(bytes, WidgetJsonContext.Default.WidgetClaim);
            if (deserialized is null)
            {
                _log.Warn("The widget claim file holds null, so nothing is claimed: " + _path);
                return null;
            }

            claim = deserialized;
        }
        catch (JsonException ex)
        {
            _log.Warn("The widget claim file is not valid, so nothing is claimed: " + _path + " (" + ex.Message + ")");
            return null;
        }

        return Validate(claim) ? claim : null;
    }

    // M1: the file is trusted only after every one of these holds. Anything that fails leaves the claim
    // unusable (read as "no claim") and the file exactly as it was; nothing here ever rewrites or deletes it.
    private bool Validate(WidgetClaim claim)
    {
        if (claim.SchemaVersion != 1)
        {
            _log.Warn("The widget claim file is schema version " + claim.SchemaVersion + ", not 1, so it is not used and is left as it is: " + _path);
            return false;
        }

        object? last = claim.Last;
        if (last is null)
        {
            _log.Warn("The widget claim file has no last reading, so nothing is claimed: " + _path);
            return false;
        }

        if (!NibbleInRange(claim.Last.NibbleHigh) || !NibbleInRange(claim.Last.NibbleLow) || !NibbleInRange(claim.Last.Case))
        {
            _log.Warn("The widget claim file holds a battery value out of range, so nothing is claimed: " + _path);
            return false;
        }

        sbyte? currentThreshold = _currentSignalThreshold();
        if (currentThreshold != claim.SignalThresholdDbm)
        {
            _log.Warn(
                "The widget claim's signal threshold no longer matches phase 0's current one, so nothing is claimed: " + _path);
            return false;
        }

        return true;
    }

    private static bool NibbleInRange(int? nibble) => nibble is null || (nibble.Value >= 0 && nibble.Value <= 10);

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
