using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Earshot.Contracts;

namespace Earshot.Boot.Gate;

internal enum GateReadStatus { Ok, Missing, Invalid, Unreadable }

// One read of a gate file. Value is set only when Status is Ok; Step records how the read went.
internal sealed record GateRead<T>(GateReadStatus Status, T? Value, StepOutcome Step)
    where T : class
{
    public bool IsOk => Status == GateReadStatus.Ok && Value is not null;
}

// The status file the gate writes for each request, read back by the tray (advisory; the node read is the
// ground truth).
internal sealed record GateStatusFile(
    int SchemaVersion,
    string Nonce,
    string Verb,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string Result,
    int ExitCode,
    string? State,
    IReadOnlyList<StepOutcome> Steps,
    bool StepsTruncated);

// The SYSTEM-owned files in %ProgramData%\Earshot: config.json, device.json, protection.json and the
// per-request status-<nonce>.json files.
//
// Reads are strict and never trust the content: a size cap, a reparse point refusal, exactly the expected
// members with the expected JSON types (no extras, no duplicates) and value validation. Anything else is
// Invalid and the caller does not act on it. Writes go to a temporary file in the same folder and are
// moved over the target, so a reader never sees half a file. The folder itself is hardened by install and
// checked by the gate before it trusts anything here.
// https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsondocument
// https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move
internal sealed partial class GateStore
{
    public const int SchemaVersion = 1;
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxStatusSteps = 48;
    public const int MaxStepText = 300;
    public const int MaxStatusFilesKept = 16;
    public const int MaxProtectionServices = 16;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    public GateStore(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = folder;
    }

    public string Folder { get; }

    public string ConfigFile => Path.Combine(Folder, "config.json");

    public string DeviceFile => Path.Combine(Folder, "device.json");

    public string ProtectionFile => Path.Combine(Folder, "protection.json");

    public string StatusFile(string nonce)
    {
        if (!BoundaryValidation.IsNonce(nonce))
        {
            throw new ArgumentException("A status file needs a 32 character lower-case hex nonce.", nameof(nonce));
        }

        return Path.Combine(Folder, "status-" + nonce + ".json");
    }

    // ---- config.json ----

    public GateRead<GateConfig> ReadConfig() => Read<GateConfig>(ConfigFile, "read-config", root =>
    {
        string? shape = RequireShape(root, ("SchemaVersion", JsonValueKind.Number), ("BlockAtBoot", JsonValueKind.True));
        if (shape is not null)
        {
            return (null, shape);
        }

        if (!root.GetProperty("SchemaVersion").TryGetInt32(out int version) || version != SchemaVersion)
        {
            return (null, "SchemaVersion is not " + SchemaVersion + ".");
        }

        return (new GateConfig { SchemaVersion = version, BlockAtBoot = root.GetProperty("BlockAtBoot").GetBoolean() }, null);
    });

    public StepOutcome WriteConfig(GateConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Write(ConfigFile, "write-config", w =>
        {
            w.WriteStartObject();
            w.WriteNumber("SchemaVersion", SchemaVersion);
            w.WriteBoolean("BlockAtBoot", config.BlockAtBoot);
            w.WriteEndObject();
        });
    }

    // ---- device.json ----

    public GateRead<DeviceIdentity> ReadDevice() => Read<DeviceIdentity>(DeviceFile, "read-device", root =>
    {
        string? shape = RequireShape(root, ("Address", JsonValueKind.String), ("ContainerId", JsonValueKind.String));
        if (shape is not null)
        {
            return (null, shape);
        }

        var identity = new DeviceIdentity
        {
            Address = root.GetProperty("Address").GetString() ?? "",
            ContainerId = Guid.TryParseExact(root.GetProperty("ContainerId").GetString(), "D", out Guid container) ? container : Guid.Empty,
        };
        string? problem = ValidateDevice(identity);
        return problem is null ? (identity, null) : (null, problem);
    });

    public StepOutcome WriteDevice(DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string? problem = ValidateDevice(identity);
        if (problem is not null)
        {
            return StepOutcomes.NotAttempted("write-device", problem);
        }

        return Write(DeviceFile, "write-device", w =>
        {
            w.WriteStartObject();
            w.WriteString("Address", identity.Address);
            w.WriteString("ContainerId", identity.ContainerId.ToString("D"));
            w.WriteEndObject();
        });
    }

    internal static string? ValidateDevice(DeviceIdentity identity) =>
        !BoundaryValidation.IsAddress12(identity.Address) ? "Address is not 12 upper-case hex characters."
        : !NodeMatch.IsValidTargetContainer(identity.ContainerId) ? "ContainerId is empty or the PC container."
        : null;

    // ---- protection.json ----

    public GateRead<ProtectionRecord> ReadProtection() => Read<ProtectionRecord>(ProtectionFile, "read-protection", root =>
    {
        string? shape = RequireShape(root, ("DisabledServices", JsonValueKind.Array));
        if (shape is not null)
        {
            return (null, shape);
        }

        var record = new ProtectionRecord();
        foreach (JsonElement item in root.GetProperty("DisabledServices").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParseExact(item.GetString(), "D", out Guid service))
            {
                return (null, "DisabledServices holds something other than a GUID.");
            }

            record.DisabledServices.Add(service);
        }

        string? problem = ValidateProtection(record);
        return problem is null ? (record, null) : (null, problem);
    });

    public StepOutcome WriteProtection(ProtectionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string? problem = ValidateProtection(record);
        if (problem is not null)
        {
            return StepOutcomes.NotAttempted("write-protection", problem);
        }

        return Write(ProtectionFile, "write-protection", w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("DisabledServices");
            foreach (Guid service in record.DisabledServices)
            {
                w.WriteStringValue(service.ToString("D"));
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    internal static string? ValidateProtection(ProtectionRecord record) =>
        record.DisabledServices.Count > MaxProtectionServices ? "DisabledServices has too many entries."
        : record.DisabledServices.Contains(Guid.Empty) ? "DisabledServices holds an empty GUID."
        : record.DisabledServices.Distinct().Count() != record.DisabledServices.Count ? "DisabledServices repeats a GUID."
        : null;

    // ---- status-<nonce>.json ----

    public GateRead<GateStatusFile> ReadStatus(string nonce)
    {
        string path = StatusFile(nonce);
        return Read<GateStatusFile>(path, "read-status", root =>
        {
            string? shape = RequireShape(
                root,
                ("SchemaVersion", JsonValueKind.Number),
                ("Nonce", JsonValueKind.String),
                ("Verb", JsonValueKind.String),
                ("StartedUtc", JsonValueKind.String),
                ("FinishedUtc", JsonValueKind.String),
                ("Result", JsonValueKind.String),
                ("ExitCode", JsonValueKind.Number),
                ("State", JsonValueKind.String),
                ("Steps", JsonValueKind.Array),
                ("StepsTruncated", JsonValueKind.True));
            if (shape is not null)
            {
                return (null, shape);
            }

            if (!root.GetProperty("SchemaVersion").TryGetInt32(out int version) || version != SchemaVersion)
            {
                return (null, "SchemaVersion is not " + SchemaVersion + ".");
            }

            string fileNonce = root.GetProperty("Nonce").GetString() ?? "";
            string verb = root.GetProperty("Verb").GetString() ?? "";
            string result = root.GetProperty("Result").GetString() ?? "";
            string state = root.GetProperty("State").GetString() ?? "";
            if (!string.Equals(fileNonce, nonce, StringComparison.Ordinal))
            {
                return (null, "The nonce does not match the request.");
            }

            if (!GateVerbs.All.Contains(verb))
            {
                return (null, "Verb is not a gate verb.");
            }

            if (!GateExitCodes.IsResultName(result))
            {
                return (null, "Result is not a known result.");
            }

            if (state.Length != 0 && !Enum.TryParse<BlockState>(state, ignoreCase: false, out _))
            {
                return (null, "State is not a block state.");
            }

            if (!TryReadTime(root, "StartedUtc", out DateTimeOffset started) || !TryReadTime(root, "FinishedUtc", out DateTimeOffset finished))
            {
                return (null, "A timestamp is not ISO 8601 UTC.");
            }

            if (!root.GetProperty("ExitCode").TryGetInt32(out int exitCode))
            {
                return (null, "ExitCode is not a 32-bit integer.");
            }

            JsonElement stepsElement = root.GetProperty("Steps");
            if (stepsElement.GetArrayLength() > MaxStatusSteps)
            {
                return (null, "Too many steps.");
            }

            var steps = new List<StepOutcome>();
            foreach (JsonElement item in stepsElement.EnumerateArray())
            {
                StepOutcome? step = ReadStep(item, out string? stepProblem);
                if (step is null)
                {
                    return (null, stepProblem);
                }

                steps.Add(step);
            }

            return (new GateStatusFile(version, fileNonce, verb, started, finished, result, exitCode,
                state.Length == 0 ? null : state, steps, root.GetProperty("StepsTruncated").GetBoolean()), null);
        });
    }

    public StepOutcome WriteStatus(GateStatusFile status)
    {
        ArgumentNullException.ThrowIfNull(status);
        string path = StatusFile(status.Nonce);
        bool truncated = status.StepsTruncated || status.Steps.Count > MaxStatusSteps;
        IEnumerable<StepOutcome> steps = status.Steps.Take(MaxStatusSteps);
        return Write(path, "write-status", w =>
        {
            w.WriteStartObject();
            w.WriteNumber("SchemaVersion", SchemaVersion);
            w.WriteString("Nonce", status.Nonce);
            w.WriteString("Verb", status.Verb);
            w.WriteString("StartedUtc", FormatTime(status.StartedUtc));
            w.WriteString("FinishedUtc", FormatTime(status.FinishedUtc));
            w.WriteString("Result", status.Result);
            w.WriteNumber("ExitCode", status.ExitCode);
            w.WriteString("State", status.State ?? "");
            w.WriteStartArray("Steps");
            foreach (StepOutcome step in steps)
            {
                w.WriteStartObject();
                w.WriteString("Step", Bound(step.Step));
                w.WriteBoolean("Ok", step.Ok);
                w.WriteNumber("Code", step.Code);
                w.WriteString("CodeName", Bound(step.CodeName));
                w.WriteString("Detail", Bound(step.Detail ?? ""));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteBoolean("StepsTruncated", truncated);
            w.WriteEndObject();
        });
    }

    // Deletes status files beyond the newest MaxStatusFilesKept - 1 (leaving room for the one about to be
    // written) and temporary files left by an interrupted write, so repeated requests cannot fill the folder.
    // Only names this store creates are touched. Every failed delete is a step.
    public IReadOnlyList<StepOutcome> PruneStatusFiles(DateTimeOffset now)
    {
        var steps = new List<StepOutcome>();
        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (DirectoryNotFoundException ex)
        {
            steps.Add(StepOutcomes.FromHResult("prune-status", ex.HResult, ex.Message));
            return steps;
        }
        catch (IOException ex)
        {
            steps.Add(StepOutcomes.FromHResult("prune-status", ex.HResult, ex.Message));
            return steps;
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult("prune-status", ex.HResult, ex.Message));
            return steps;
        }

        IEnumerable<FileInfo> oldStatus = files
            .Where(f => StatusName().IsMatch(f.Name))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .Skip(MaxStatusFilesKept - 1);
        IEnumerable<FileInfo> staleTemp = files
            .Where(f => TempName().IsMatch(f.Name) && now.UtcDateTime - f.LastWriteTimeUtc > TimeSpan.FromHours(1));

        foreach (FileInfo file in oldStatus.Concat(staleTemp))
        {
            try
            {
                file.Delete();
            }
            catch (IOException ex)
            {
                steps.Add(StepOutcomes.FromHResult("prune-status:" + file.Name, ex.HResult, ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                steps.Add(StepOutcomes.FromHResult("prune-status:" + file.Name, ex.HResult, ex.Message));
            }
        }

        return steps;
    }

    [GeneratedRegex("^status-[0-9a-f]{32}\\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex StatusName();

    [GeneratedRegex("^(status-[0-9a-f]{32}|config|device|protection)\\.json\\.tmp-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TempName();

    // ---- shared plumbing ----

    private delegate (T? Value, string? Problem) Parser<T>(JsonElement root) where T : class;

    private static GateRead<T> Read<T>(string path, string step, Parser<T> parse)
        where T : class
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new GateRead<T>(GateReadStatus.Missing, null, StepOutcomes.FromHResult(step, unchecked((int)0x80070002), "Missing: " + path, ok: false));
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Invalid<T>(step, path, "The file is a reparse point.");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None);
            if (stream.Length > MaxFileBytes)
            {
                return Invalid<T>(step, path, "The file is larger than " + MaxFileBytes + " bytes.");
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (FileNotFoundException ex)
        {
            return new GateRead<T>(GateReadStatus.Missing, null, StepOutcomes.FromHResult(step, ex.HResult, "Missing: " + path, ok: false));
        }
        catch (DirectoryNotFoundException ex)
        {
            return new GateRead<T>(GateReadStatus.Missing, null, StepOutcomes.FromHResult(step, ex.HResult, "Missing: " + path, ok: false));
        }
        catch (IOException ex)
        {
            return new GateRead<T>(GateReadStatus.Unreadable, null, StepOutcomes.FromHResult(step, ex.HResult, path + ": " + ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return new GateRead<T>(GateReadStatus.Unreadable, null, StepOutcomes.FromHResult(step, ex.HResult, path + ": " + ex.Message));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid<T>(step, path, "The root is not a JSON object.");
            }

            (T? value, string? problem) = parse(document.RootElement);
            return value is null
                ? Invalid<T>(step, path, problem ?? "The content is not valid.")
                : new GateRead<T>(GateReadStatus.Ok, value, new StepOutcome(step, true, 0, "S_OK", path));
        }
        catch (JsonException ex)
        {
            return Invalid<T>(step, path, "Not valid JSON: " + ex.Message);
        }
        catch (FormatException ex)
        {
            return Invalid<T>(step, path, ex.Message);
        }
    }

    // Content that fails validation is recorded as ERROR_INVALID_DATA in HRESULT form.
    private const int HResultInvalidData = unchecked((int)0x8007000D);

    private static GateRead<T> Invalid<T>(string step, string path, string problem)
        where T : class =>
        new(GateReadStatus.Invalid, null, StepOutcomes.FromHResult(step, HResultInvalidData, path + ": " + problem));

    private static StepOutcome Write(string path, string step, Action<Utf8JsonWriter> write)
    {
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                write(writer);
            }

            bytes = buffer.ToArray();
        }

        if (bytes.Length > MaxFileBytes)
        {
            return StepOutcomes.NotAttempted(step, "The content is larger than " + MaxFileBytes + " bytes.");
        }

        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            return new StepOutcome(step, true, 0, "S_OK", path);
        }
        catch (IOException ex)
        {
            return WriteFailed(step, path, temp, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return WriteFailed(step, path, temp, ex);
        }
    }

    private static StepOutcome WriteFailed(string step, string path, string temp, Exception ex)
    {
        string detail = path + ": " + ex.Message;
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        catch (IOException cleanup)
        {
            detail += " The temporary file was left behind: " + cleanup.Message;
        }
        catch (UnauthorizedAccessException cleanup)
        {
            detail += " The temporary file was left behind: " + cleanup.Message;
        }

        return StepOutcomes.FromHResult(step, ex.HResult, detail);
    }

    // Null when root has exactly these members, once each, with these kinds. JsonValueKind.True stands for
    // either boolean.
    internal static string? RequireShape(JsonElement root, params (string Name, JsonValueKind Kind)[] members)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return "Member " + property.Name + " appears twice.";
            }

            (string Name, JsonValueKind Kind)? expected = null;
            foreach ((string Name, JsonValueKind Kind) member in members)
            {
                if (string.Equals(member.Name, property.Name, StringComparison.Ordinal))
                {
                    expected = member;
                    break;
                }
            }

            if (expected is null)
            {
                return "Unexpected member " + Bound(property.Name) + ".";
            }

            JsonValueKind kind = property.Value.ValueKind;
            bool kindOk = expected.Value.Kind == JsonValueKind.True
                ? kind is JsonValueKind.True or JsonValueKind.False
                : kind == expected.Value.Kind;
            if (!kindOk)
            {
                return "Member " + property.Name + " has the wrong type.";
            }
        }

        foreach ((string name, _) in members)
        {
            if (!seen.Contains(name))
            {
                return "Member " + name + " is missing.";
            }
        }

        return null;
    }

    private static StepOutcome? ReadStep(JsonElement item, out string? problem)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            problem = "A step is not an object.";
            return null;
        }

        problem = RequireShape(
            item,
            ("Step", JsonValueKind.String),
            ("Ok", JsonValueKind.True),
            ("Code", JsonValueKind.Number),
            ("CodeName", JsonValueKind.String),
            ("Detail", JsonValueKind.String));
        if (problem is not null)
        {
            return null;
        }

        string step = item.GetProperty("Step").GetString() ?? "";
        string codeName = item.GetProperty("CodeName").GetString() ?? "";
        string detail = item.GetProperty("Detail").GetString() ?? "";
        if (step.Length is 0 or > MaxStepText || codeName.Length > MaxStepText || detail.Length > MaxStepText)
        {
            problem = "A step text is empty or too long.";
            return null;
        }

        if (!item.GetProperty("Code").TryGetInt32(out int code))
        {
            problem = "A step code is not a 32-bit integer.";
            return null;
        }

        return new StepOutcome(step, item.GetProperty("Ok").GetBoolean(), code, codeName, detail.Length == 0 ? null : detail);
    }

    private static bool TryReadTime(JsonElement root, string name, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(
            root.GetProperty(name).GetString(),
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);

    private static string FormatTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    // Keeps a text within MaxStepText characters and free of control characters.
    internal static string Bound(string text)
    {
        var sb = new StringBuilder(Math.Min(text.Length, MaxStepText));
        foreach (char c in text)
        {
            if (sb.Length == MaxStepText)
            {
                break;
            }

            sb.Append(char.IsControl(c) ? ' ' : c);
        }

        return sb.ToString();
    }
}
