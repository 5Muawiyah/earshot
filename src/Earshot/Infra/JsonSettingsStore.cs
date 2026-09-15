using System.Globalization;
using System.Text.Json;
using Earshot.Contracts;

namespace Earshot.Infra;

// How the last load went, so the tray can tell the user once when settings were reset.
internal enum SettingsLoadStatus
{
    Loaded,                // settings.json read and valid
    CreatedDefaults,       // no settings.json; defaults written
    RestoredFromBackup,    // settings.json unusable; settings.json.bak used
    ResetAfterCorruption,  // settings.json and the backup unusable; defaults written
    ReadFailed             // settings.json exists but could not be opened; defaults in memory only
}

// User settings in %APPDATA%\Earshot\settings.json.
//
// Load:
//   missing file              defaults, then save
//   valid file                use it (unknown members are ignored)
//   empty, truncated, null,
//   explicit null for a non-nullable member, unknown schema version or blank match string
//                             try settings.json.bak; move the bad file aside as
//                             settings.corrupt-<UTC yyyyMMddHHmmss>.json; use the backup if it is
//                             valid, otherwise defaults; save; log a warning either way
//   file cannot be opened     defaults in memory, file left untouched, error logged
//
// Save writes settings.json.tmp, flushes it to disk, then File.Replace swaps it in and keeps the
// previous file as settings.json.bak. File.Replace needs an existing destination, so the first
// save uses File.Move. All three files sit in one folder, as ReplaceFileW requires one volume.
// A sharing violation is retried three times with a short backoff, then surfaced.
// https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace
// https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew
//
// Current and the Changed argument are copies: changing them does not change the store. Changes
// go through Update, which runs under the store's lock; do not call Update from inside mutate.
internal sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly int CurrentSchemaVersion = new EarshotSettings().SchemaVersion;
    private static readonly int[] RetryDelaysMs = [50, 100, 200];

    private readonly Lock _gate = new();
    private readonly ILog _log;
    private EarshotSettings _current = new();

    public JsonSettingsStore(string path, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
        FilePath = Path.GetFullPath(path);
        BackupPath = FilePath + ".bak";
        TempPath = FilePath + ".tmp";
        lock (_gate)
        {
            LoadLocked();
        }
    }

    public event EventHandler<EarshotSettings>? Changed;

    public string FilePath { get; }

    public string BackupPath { get; }

    public string TempPath { get; }

    public SettingsLoadStatus LastLoadStatus { get; private set; }

    // Where the last unusable settings file was moved, or null.
    public string? QuarantinedFile { get; private set; }

    public EarshotSettings Current
    {
        get
        {
            lock (_gate)
            {
                return Clone(_current);
            }
        }
    }

    public void Update(Action<EarshotSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        EarshotSettings published;
        lock (_gate)
        {
            EarshotSettings next = Clone(_current);
            mutate(next);
            string? problem = Validate(next);
            if (problem is not null)
            {
                throw new ArgumentException("Settings were not saved: " + problem + ".", nameof(mutate));
            }

            Save(next);
            _current = next;
            published = Clone(next);
        }

        Changed?.Invoke(this, published);
    }

    public void Reload()
    {
        EarshotSettings published;
        lock (_gate)
        {
            LoadLocked();
            published = Clone(_current);
        }

        Changed?.Invoke(this, published);
    }

    // Returns why the settings cannot be used, or null when they can.
    internal static string? Validate(EarshotSettings settings)
    {
        if (settings.SchemaVersion != CurrentSchemaVersion)
        {
            return "unknown schema version " + settings.SchemaVersion.ToString(CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(settings.DeviceMatch))
        {
            return "the device match string is blank";
        }

        if (settings.PinnedAddress is null)
        {
            return "the pinned address is missing";
        }

        return null;
    }

    private void LoadLocked()
    {
        QuarantinedFile = null;
        ReadResult main = TryRead(FilePath);

        if (main.Kind == ReadKind.Missing)
        {
            _current = new EarshotSettings();
            LastLoadStatus = SettingsLoadStatus.CreatedDefaults;
            _log.Info("No settings file, so defaults are saved to " + FilePath);
            TrySaveDuringLoad();
            return;
        }

        if (main.Kind == ReadKind.Valid)
        {
            _current = main.Settings!;
            LastLoadStatus = SettingsLoadStatus.Loaded;
            return;
        }

        if (main.Kind == ReadKind.Unreadable)
        {
            _current = new EarshotSettings();
            LastLoadStatus = SettingsLoadStatus.ReadFailed;
            _log.Error("Settings could not be opened, so defaults are used for now and the file is left as it is: " + FilePath, main.Error);
            return;
        }

        // The file is there but unusable. Read the backup before anything moves.
        ReadResult backup = TryRead(BackupPath);
        string? quarantined = Quarantine();
        QuarantinedFile = quarantined;
        string keptAs = quarantined ?? "(could not be moved)";

        if (backup.Kind == ReadKind.Valid)
        {
            _current = backup.Settings!;
            LastLoadStatus = SettingsLoadStatus.RestoredFromBackup;
            _log.Warn("Settings file was unusable (" + main.Problem + "). Restored the previous copy from " + BackupPath + ". The unusable file is kept as " + keptAs);
        }
        else
        {
            _current = new EarshotSettings();
            LastLoadStatus = SettingsLoadStatus.ResetAfterCorruption;
            string backupNote = backup.Kind == ReadKind.Missing ? "no backup" : "backup also unusable (" + backup.Problem + ")";
            _log.Warn("Settings were unusable (" + main.Problem + "; " + backupNote + ") and have been reset to defaults. The unusable file is kept as " + keptAs);
        }

        if (quarantined is null)
        {
            // Saving now would push the bad file over the good backup. Keep the loaded values in
            // memory and leave the files for the next successful save.
            _log.Error("Settings were not saved because the unusable file could not be moved aside: " + FilePath);
            return;
        }

        TrySaveDuringLoad();
    }

    private void TrySaveDuringLoad()
    {
        try
        {
            Save(_current);
        }
        catch (IOException ex)
        {
            _log.Error("Settings could not be saved to " + FilePath + ". They stay in memory for this session.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Error("Settings could not be saved to " + FilePath + ". They stay in memory for this session.", ex);
        }
    }

    private void Save(EarshotSettings settings)
    {
        string folder = Path.GetDirectoryName(FilePath)!;
        WithRetry("save settings", () =>
        {
            Directory.CreateDirectory(folder);
            using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.EarshotSettings);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath))
            {
                File.Replace(TempPath, FilePath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(TempPath, FilePath, overwrite: true);
            }
        });
    }

    private ReadResult TryRead(string path)
    {
        byte[] bytes;
        try
        {
            bytes = ReadWithRetry(path);
        }
        catch (FileNotFoundException)
        {
            return ReadResult.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return ReadResult.Missing;
        }
        catch (IOException ex)
        {
            return ReadResult.Unreadable(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ReadResult.Unreadable(ex);
        }

        // Notepad and other editors may add a UTF-8 byte order mark; the span reader rejects it.
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            json = json[3..];
        }

        EarshotSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.EarshotSettings);
        }
        catch (JsonException ex)
        {
            return ReadResult.Invalid("not valid settings JSON: " + ex.Message);
        }

        if (parsed is null)
        {
            return ReadResult.Invalid("the file holds null");
        }

        string? problem = Validate(parsed);
        return problem is null ? ReadResult.Valid(parsed) : ReadResult.Invalid(problem);
    }

    private byte[] ReadWithRetry(string path)
    {
        byte[] result = [];
        WithRetry("read settings", () => result = File.ReadAllBytes(path));
        return result;
    }

    private void WithRetry(string what, Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (attempt < RetryDelaysMs.Length && ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                _log.Write(LogLevel.Debug, "Retrying " + what + " after: " + ex.Message);
                Thread.Sleep(RetryDelaysMs[attempt]);
            }
        }
    }

    // Moves the unusable settings file aside with a UTC timestamp. Returns the new path, or null.
    private string? Quarantine()
    {
        string folder = Path.GetDirectoryName(FilePath)!;
        string stem = Path.GetFileNameWithoutExtension(FilePath) + ".corrupt-" +
                      DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string extension = Path.GetExtension(FilePath);

        for (int n = 1; n <= 50; n++)
        {
            string target = Path.Combine(folder, n == 1 ? stem + extension : stem + "-" + n.ToString(CultureInfo.InvariantCulture) + extension);
            if (File.Exists(target))
            {
                continue;
            }

            try
            {
                File.Move(FilePath, target);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // Another writer took this name first; try the next one.
            }
            catch (IOException ex)
            {
                _log.Error("Could not move the unusable settings file aside: " + FilePath, ex);
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                _log.Error("Could not move the unusable settings file aside: " + FilePath, ex);
                return null;
            }
        }

        _log.Error("Could not find a free name to move the unusable settings file aside: " + FilePath);
        return null;
    }

    private static EarshotSettings Clone(EarshotSettings settings) =>
        JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.EarshotSettings),
            SettingsJsonContext.Default.EarshotSettings)
        ?? throw new InvalidOperationException("Copying the settings produced null.");

    private enum ReadKind { Missing, Valid, Invalid, Unreadable }

    private readonly record struct ReadResult(ReadKind Kind, EarshotSettings? Settings, string? Problem, Exception? Error)
    {
        public static ReadResult Missing => new(ReadKind.Missing, null, null, null);

        public static ReadResult Valid(EarshotSettings settings) => new(ReadKind.Valid, settings, null, null);

        public static ReadResult Invalid(string problem) => new(ReadKind.Invalid, null, problem, null);

        public static ReadResult Unreadable(Exception error) => new(ReadKind.Unreadable, null, error.Message, error);
    }
}
