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
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();
    private WidgetClaim? _current;
    private Task _pendingWrite = Task.CompletedTask;
    private int _diskWriteCount;

    public ClaimStore(string path, ILog log)
        : this(path, log, static () => WidgetDefaults.SignalThresholdDbm)
    {
    }

    // Test-only: supplies the "phase 0 has proved this threshold" comparison directly, so the claim's
    // threshold validation can be exercised without WidgetDefaults.SignalThresholdDbm ever holding
    // anything but its shipped null, the way ClaimFlow's internal overload does for the same constant.
    internal ClaimStore(string path, ILog log, Func<sbyte?> currentSignalThreshold)
        : this(path, log, currentSignalThreshold, static () => DateTimeOffset.UtcNow)
    {
    }

    // Test-only: supplies "now" directly, so a claim whose ClaimedAtUtc is in the future can be exercised
    // without waiting for the real clock to catch up to a fixture's fixed date.
    internal ClaimStore(string path, ILog log, Func<sbyte?> currentSignalThreshold, Func<DateTimeOffset> now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(currentSignalThreshold);
        ArgumentNullException.ThrowIfNull(now);

        _path = path;
        _log = log;
        _currentSignalThreshold = currentSignalThreshold;
        _now = now;
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

    // Updates the in-memory claim immediately (so a reader never blocks on disk I/O and always sees what
    // was just decided), then queues the actual write, serialised with any other write or forget this store
    // has pending, so neither the UI thread (the service's callback) nor ClaimFlow's own caller ever waits on
    // file I/O here. Saving is skipped outright, with nothing queued, when the claim carries an older
    // generation (ClaimedAtUtc) or an older reading (Last.AtUtc within the same generation) than what is
    // already current, or when nothing about it actually differs from what is current.
    //
    // The decision of what _current becomes and the enqueue of the matching disk action happen inside the
    // same lock as each other (here and in ForgetClaim), so the order those two things are decided in always
    // matches the order the queued disk actions run in. Each queued action re-reads _current, under this same
    // lock, immediately before touching disk: a write proceeds only while its claim is still current, and
    // ForgetClaim's delete proceeds only while _current is still null, both decided and acted upon without
    // releasing the lock in between, so nothing can land in the gap the way a save used to resurrect a
    // deliberately forgotten claim.
    public void Save(WidgetClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_gate)
        {
            if (_current is WidgetClaim existing)
            {
                if (claim.ClaimedAtUtc < existing.ClaimedAtUtc ||
                    (claim.ClaimedAtUtc == existing.ClaimedAtUtc && claim.Last.AtUtc < existing.Last.AtUtc))
                {
                    return;
                }

                if (existing.Equals(claim))
                {
                    return;
                }
            }

            _current = claim;
            _pendingWrite = _pendingWrite.ContinueWith(
                _ => WriteOne(claim), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    // Test-only: lets a test wait for every write or forget Save/ForgetClaim has queued so far to finish, so
    // it can assert on the file or the log without racing the background worker.
    internal Task IdleAsync()
    {
        lock (_gate)
        {
            return _pendingWrite;
        }
    }

    // Test-only: how many times WriteAtomic actually ran, to prove that repeated saves of an unchanged
    // reading write the file once, not once per advertisement.
    internal int DiskWriteCount => Volatile.Read(ref _diskWriteCount);

    // Test-only: fires, from the background worker's own thread, right after a write's "is this claim still
    // current" check passes and before the disk write happens. Exists so a test can force the exact
    // interleaving the fix above makes impossible, rather than depending on the thread pool's own scheduling.
    internal Action? TestHookAfterWriteCheckPassed;

    private void WriteOne(WidgetClaim claim)
    {
        lock (_gate)
        {
            // A later Save or a ForgetClaim already moved _current past this write's claim: nothing to do.
            // The check and the write happen without releasing the lock in between, so nothing else can move
            // _current on after this check passes and before the write actually reaches disk.
            if (!claim.Equals(_current))
            {
                return;
            }

            TestHookAfterWriteCheckPassed?.Invoke();

            Interlocked.Increment(ref _diskWriteCount);
            try
            {
                WriteAtomic(claim);
            }
            catch (IOException ex)
            {
                _log.Warn(
                    "The widget claim could not be saved, so only the in-memory value is current (0x" +
                    ex.HResult.ToString("X8") + "): " + _path);
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Warn(
                    "The widget claim could not be saved, so only the in-memory value is current (0x" +
                    ex.HResult.ToString("X8") + "): " + _path);
            }
            catch (Exception ex)
            {
                // Anything else here would otherwise be an unobserved exception on a background task and
                // simply vanish: logged rather than left for that mechanism to lose silently.
                _log.Error("The widget claim could not be saved because of an unexpected error: " + _path, ex);
            }
        }
    }

    public void ForgetClaim()
    {
        lock (_gate)
        {
            _current = null;
            _pendingWrite = _pendingWrite.ContinueWith(
                _ => ForgetOne(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void ForgetOne()
    {
        lock (_gate)
        {
            // A later Save already replaced what was forgotten: leave its write (already queued behind this
            // one) to run and do not delete what it is about to write or has already written.
            if (_current is not null)
            {
                return;
            }

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
            catch (Exception ex)
            {
                _log.Error("Could not delete the widget claim because of an unexpected error: " + _path, ex);
            }
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

    // The file is trusted only after every one of these holds. Anything that fails leaves the claim
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

        // A claim dated in the future (a corrupted file, or a clock that ran backwards before it was written)
        // would otherwise become the baseline every later, genuine Save is compared against: Save refuses
        // anything with an older or equal generation than what is already current, so a future ClaimedAtUtc
        // would make every real claim look older forever and silently never persist.
        if (claim.ClaimedAtUtc > _now())
        {
            _log.Warn("The widget claim file is dated in the future, so nothing is claimed: " + _path);
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
