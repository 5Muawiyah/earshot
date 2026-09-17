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
    ReadFailed,            // settings.json, or the backup of an unusable settings.json, could not be opened; defaults in memory only
    NewerSchema,           // settings.json is from a newer Earshot; its values (or defaults) in memory, nothing written
    DefaultsReadOnly,      // read-only store, no settings.json; defaults in memory, nothing written
    UnusableReadOnly       // read-only store, settings.json unusable; backup or defaults in memory, nothing moved or written
}

// User settings in %APPDATA%\Earshot\settings.json.
//
// Load:
//   missing file              defaults, then save
//   valid file                use it (unknown members are ignored)
//   newer schema version      the file was written by a newer Earshot. Use the members this build
//                             knows if they are valid, otherwise defaults; never move or rewrite the
//                             file; the store becomes read-only (IsReadOnly) and logs a warning.
//                             The version is read token by token before the typed read, so a newer
//                             file this build cannot deserialise (a member whose type changed, an
//                             explicit null) is still left alone
//   empty, truncated, null,
//   explicit null for a non-nullable member, schema version below 1, blank match string,
//   pinned address that is not "" or 12 upper-case hex, pinned container that is the PC container
//                             try settings.json.bak; move the bad file aside as
//                             settings.corrupt-<UTC yyyyMMddHHmmss>.json; use the backup if it is
//                             valid, otherwise defaults; save; log a warning either way
//   file cannot be opened, or unusable with a backup that cannot be opened, or unusable and
//   cannot be moved aside     defaults (or the backup) in memory, files left untouched, error logged
//
// After a load that left the files untouched, the values in memory are not what settings.json holds,
// so saving them would overwrite the user's settings. Update therefore loads again first. If the file
// can now be used, the change is applied to what it holds and saved; if not, the change is kept for
// this run only and nothing is written. A load again (in Update or Reload) that still leaves the files
// untouched keeps the values already in memory, including earlier changes kept for this run, rather than
// falling back to defaults; and Update raises Changed once, with the values it ends with.
//
// A store opened read-only (probe and diag, which must change nothing) follows the same rules but
// never saves, moves or quarantines a file. On a read-only store Update changes the values for this
// run only and logs that they were not saved.
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
    private readonly bool _openedReadOnly;
    private EarshotSettings _current = new();
    private bool _newerSchema;

    // True when the last load left settings.json untouched because it (or its backup) could not be
    // opened, or because an unusable file could not be moved aside. Update loads again before saving.
    private bool _loadUnsettled;

    public JsonSettingsStore(string path, ILog log, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
        _openedReadOnly = readOnly;
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

    // True when nothing is written: the store was opened read-only, or the file is from a newer
    // Earshot and saving would drop the settings this build does not know.
    public bool IsReadOnly
    {
        get
        {
            lock (_gate)
            {
                return _openedReadOnly || _newerSchema;
            }
        }
    }

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
            if (_loadUnsettled && !_openedReadOnly)
            {
                // Saving now would put the values in memory over settings the last load never read.
                _log.Info("Loading " + FilePath + " again before saving, because the last load left it untouched.");
                LoadAgainLocked();
            }

            EarshotSettings next = Clone(_current);
            mutate(next);
            string? problem = Validate(next);
            if (problem is not null)
            {
                throw new ArgumentException("Settings were not saved: " + problem + ".", nameof(mutate));
            }

            if (_openedReadOnly || _newerSchema || _loadUnsettled)
            {
                string reason = _newerSchema ? "the file is from a newer version of Earshot."
                              : _openedReadOnly ? "this store is read-only."
                              : "the settings file still cannot be read or moved aside.";
                _log.Warn("Settings changed for this run only and not saved to " + FilePath + ", because " + reason);
            }
            else
            {
                Save(next);
            }

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
            LoadAgainLocked();
            published = Clone(_current);
        }

        Changed?.Invoke(this, published);
    }

    // Loads the files again. A load that still leaves them untouched (unreadable, or unusable and not moved
    // aside) says nothing about the settings, so the values in memory stay as they were rather than becoming
    // defaults: they may hold what an earlier load read, or changes kept for this run.
    private void LoadAgainLocked()
    {
        EarshotSettings before = Clone(_current);
        LoadLocked();
        if (_loadUnsettled)
        {
            _current = before;
            _log.Warn("Settings still cannot be read or moved aside, so the values already in memory are kept: " + FilePath);
        }
    }

    // Returns why the settings cannot be used, or null when they can.
    internal static string? Validate(EarshotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion != CurrentSchemaVersion)
        {
            return "unknown schema version " + settings.SchemaVersion.ToString(CultureInfo.InvariantCulture);
        }

        return ValidateMembers(settings);
    }

    // The checks that do not depend on the schema version.
    private static string? ValidateMembers(EarshotSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DeviceMatch))
        {
            return "the device match string is blank";
        }

        if (settings.PinnedAddress is null)
        {
            return "the pinned address is missing";
        }

        if (settings.PinnedAddress.Length != 0 && !BoundaryValidation.IsAddress12(settings.PinnedAddress))
        {
            return "the pinned address is not 12 upper-case hex characters";
        }

        if (settings.PinnedContainerId != Guid.Empty && !NodeMatch.IsValidTargetContainer(settings.PinnedContainerId))
        {
            return "the pinned container is the PC container";
        }

        return null;
    }

    private void LoadLocked()
    {
        QuarantinedFile = null;
        _newerSchema = false;
        _loadUnsettled = false;
        ReadResult main = TryRead(FilePath);

        if (main.Kind == ReadKind.Missing)
        {
            _current = new EarshotSettings();
            if (_openedReadOnly)
            {
                LastLoadStatus = SettingsLoadStatus.DefaultsReadOnly;
                _log.Info("No settings file at " + FilePath + ", so defaults are used and nothing is written.");
                return;
            }

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

        if (main.Kind == ReadKind.Newer)
        {
            // Resetting or rewriting would lose settings a newer build relies on, so the file is
            // left exactly as it is. This build only understands its own members, so the copy in
            // memory carries this build's schema version.
            _newerSchema = true;
            LastLoadStatus = SettingsLoadStatus.NewerSchema;
            string version = main.Version.ToString(CultureInfo.InvariantCulture);
            if (main.Settings is EarshotSettings newer && main.Problem is null)
            {
                newer.SchemaVersion = CurrentSchemaVersion;
                _current = newer;
                _log.Warn("Settings file " + FilePath + " is schema version " + version + " from a newer version of Earshot. " +
                          "The settings this version knows are used, and the file is not changed.");
            }
            else
            {
                _current = new EarshotSettings();
                _log.Warn("Settings file " + FilePath + " is schema version " + version + " from a newer version of Earshot, but " +
                          main.Problem + ", so defaults are used for this run. The file is not changed.");
            }

            return;
        }

        if (main.Kind == ReadKind.Unreadable)
        {
            _current = new EarshotSettings();
            LastLoadStatus = SettingsLoadStatus.ReadFailed;
            _loadUnsettled = true;
            _log.Error("Settings could not be opened, so defaults are used for now and the file is left as it is: " + FilePath, main.Error);
            return;
        }

        // The file is there but unusable. Read the backup before anything moves.
        ReadResult backup = TryRead(BackupPath);
        if (!_openedReadOnly && backup.Kind == ReadKind.Unreadable)
        {
            // The backup may be the only good copy. Resetting now would save defaults and, on the
            // next save, replace that backup, so nothing moves until it can be read.
            _current = new EarshotSettings();
            LastLoadStatus = SettingsLoadStatus.ReadFailed;
            _loadUnsettled = true;
            _log.Error("Settings file " + FilePath + " is unusable (" + main.Problem + ") and its backup could not be opened, " +
                       "so defaults are used for now and both files are left as they are: " + BackupPath, backup.Error);
            return;
        }

        if (_openedReadOnly)
        {
            bool backupUsable = backup.Kind == ReadKind.Valid;
            _current = backupUsable ? backup.Settings! : new EarshotSettings();
            LastLoadStatus = SettingsLoadStatus.UnusableReadOnly;
            _log.Warn("Settings file " + FilePath + " is unusable (" + main.Problem + "). " +
                      (backupUsable ? "The backup is used" : "Defaults are used") +
                      " for this run, and no file is moved or written because this store is read-only.");
            return;
        }

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
            string backupNote = backup.Kind switch
            {
                ReadKind.Missing => "no backup",
                ReadKind.Newer => "backup is from a newer version of Earshot",
                _ => "backup also unusable (" + backup.Problem + ")",
            };
            _log.Warn("Settings were unusable (" + main.Problem + "; " + backupNote + ") and have been reset to defaults. The unusable file is kept as " + keptAs);
        }

        if (quarantined is null)
        {
            // Saving now would push the bad file over the good backup. Keep the loaded values in
            // memory and leave the files; the next Update loads again first.
            _loadUnsettled = true;
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

        long? declaredNewer = DeclaredNewerSchemaVersion(json);

        EarshotSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.EarshotSettings);
        }
        catch (JsonException ex)
        {
            return declaredNewer is long version
                ? ReadResult.Newer(null, version, "its values could not be read (" + ex.Message + ")")
                : ReadResult.Invalid("not valid settings JSON: " + ex.Message);
        }

        if (parsed is null)
        {
            return ReadResult.Invalid("the file holds null");
        }

        if (declaredNewer is not null || parsed.SchemaVersion > CurrentSchemaVersion)
        {
            return ReadResult.Newer(parsed, declaredNewer ?? parsed.SchemaVersion, ValidateMembers(parsed));
        }

        string? problem = Validate(parsed);
        return problem is null ? ReadResult.Valid(parsed) : ReadResult.Invalid(problem);
    }

    // The schema version a file declares in a top-level SchemaVersion member when it is above this
    // build's, or null. Read token by token, so the answer does not depend on the file's other members
    // deserialising into this build's types. Any such member counts, and a version read before a
    // malformed token still counts: when in doubt the file is treated as newer and left alone.
    // https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/use-utf8jsonreader
    private static long? DeclaredNewerSchemaVersion(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read())
            {
                if (reader.CurrentDepth == 1 &&
                    reader.TokenType == JsonTokenType.PropertyName &&
                    reader.ValueTextEquals("SchemaVersion"u8) &&
                    reader.Read() &&
                    reader.TokenType == JsonTokenType.Number &&
                    reader.TryGetInt64(out long version) &&
                    version > CurrentSchemaVersion)
                {
                    return version;
                }
            }
        }
        catch (JsonException)
        {
            // The rest of the file is malformed and no newer version was declared before that point.
            // The typed read that follows reports the problem.
            return null;
        }

        return null;
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

    private enum ReadKind { Missing, Valid, Newer, Invalid, Unreadable }

    private readonly record struct ReadResult(ReadKind Kind, EarshotSettings? Settings, string? Problem, Exception? Error, long Version)
    {
        public static ReadResult Missing => new(ReadKind.Missing, null, null, null, 0);

        public static ReadResult Valid(EarshotSettings settings) => new(ReadKind.Valid, settings, null, null, settings.SchemaVersion);

        public static ReadResult Invalid(string problem) => new(ReadKind.Invalid, null, problem, null, 0);

        // A file from a newer Earshot. Settings is null when its values could not be deserialised;
        // Problem is null only when the members this build knows were read and are valid.
        public static ReadResult Newer(EarshotSettings? settings, long version, string? problem) =>
            new(ReadKind.Newer, settings, problem, null, version);

        public static ReadResult Unreadable(Exception error) => new(ReadKind.Unreadable, null, error.Message, error, 0);
    }
}
