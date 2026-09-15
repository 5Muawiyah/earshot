using System.Text.Json;
using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.AudioProtection.Gate;

// A protection request the gate could not apply because the device was blocked.
internal sealed record ProtectionIntent(bool Protect);

// %ProgramData%\Earshot\protection-intent.json, next to protection.json and written only by the gate:
// { "SchemaVersion": 1, "Protect": true }. It holds the protection state asked for while the device was
// blocked, so the next allow can apply it. A later protect verb that completes, or a restore, clears it.
//
// protection.json has a fixed shape (DisabledServices only) that GateStore reads strictly, so the intent
// lives in its own file with the same rules: a size cap, no reparse point, exactly the expected members,
// and a write to a temporary file in the same folder that is then moved over the target.
// https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsondocument
// https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move
internal sealed class ProtectionIntentFile
{
    public const string FileName = "protection-intent.json";
    public const int SchemaVersion = 1;

    // ERROR_FILE_NOT_FOUND and ERROR_INVALID_DATA in HRESULT form, as GateStore records them.
    private const int HResultFileNotFound = unchecked((int)0x80070002);
    private const int HResultInvalidData = unchecked((int)0x8007000D);

    private const int MoveAttempts = 10;
    private static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(25);

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4,
    };

    public ProtectionIntentFile(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        FilePath = Path.Combine(folder, FileName);
    }

    public string FilePath { get; }

    public GateRead<ProtectionIntent> Read()
    {
        const string step = "read-protection-intent";
        byte[] bytes;
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists)
            {
                return Missing(step);
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return Invalid(step, "The file is a reparse point.");
            }

            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None);
            if (stream.Length > GateStore.MaxFileBytes)
            {
                return Invalid(step, "The file is larger than " + GateStore.MaxFileBytes + " bytes.");
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (FileNotFoundException)
        {
            return Missing(step);
        }
        catch (DirectoryNotFoundException)
        {
            return Missing(step);
        }
        catch (IOException ex)
        {
            return new GateRead<ProtectionIntent>(GateReadStatus.Unreadable, null, StepOutcomes.FromHResult(step, ex.HResult, FilePath + ": " + ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return new GateRead<ProtectionIntent>(GateReadStatus.Unreadable, null, StepOutcomes.FromHResult(step, ex.HResult, FilePath + ": " + ex.Message));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, DocumentOptions);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid(step, "The root is not a JSON object.");
            }

            string? shape = GateStore.RequireShape(root, ("SchemaVersion", JsonValueKind.Number), ("Protect", JsonValueKind.True));
            if (shape is not null)
            {
                return Invalid(step, shape);
            }

            if (!root.GetProperty("SchemaVersion").TryGetInt32(out int version) || version != SchemaVersion)
            {
                return Invalid(step, "SchemaVersion is not " + SchemaVersion + ".");
            }

            return new GateRead<ProtectionIntent>(GateReadStatus.Ok, new ProtectionIntent(root.GetProperty("Protect").GetBoolean()),
                StepOutcomes.FromHResult(step, 0, FilePath));
        }
        catch (JsonException ex)
        {
            return Invalid(step, "Not valid JSON: " + ex.Message);
        }
    }

    public StepOutcome Write(bool protect)
    {
        const string step = "write-protection-intent";
        byte[] bytes = Serialize(protect);
        string temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            MoveIntoPlace(temp);
            return StepOutcomes.FromHResult(step, 0, FilePath + ": Protect " + (protect ? "on" : "off") + " is kept for the next allow.");
        }
        catch (IOException ex)
        {
            return WriteFailed(step, temp, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return WriteFailed(step, temp, ex);
        }
    }

    // Deletes the file. Null when there was none, so a run that had nothing pending adds no step.
    public StepOutcome? Clear()
    {
        const string step = "clear-protection-intent";
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            File.Delete(FilePath);
            return StepOutcomes.FromHResult(step, 0, FilePath);
        }
        catch (IOException ex)
        {
            return StepOutcomes.FromHResult(step, ex.HResult, FilePath + ": " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StepOutcomes.FromHResult(step, ex.HResult, FilePath + ": " + ex.Message);
        }
    }

    private static byte[] Serialize(bool protect)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("SchemaVersion", SchemaVersion);
            writer.WriteBoolean("Protect", protect);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    // A reader holding the file for a moment makes the replace fail; it is retried briefly and the last
    // error is what the caller records.
    private void MoveIntoPlace(string temp)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, FilePath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < MoveAttempts)
            {
                Thread.Sleep(MoveRetryDelay);
            }
            catch (UnauthorizedAccessException) when (attempt < MoveAttempts)
            {
                Thread.Sleep(MoveRetryDelay);
            }
        }
    }

    private StepOutcome WriteFailed(string step, string temp, Exception ex)
    {
        string detail = FilePath + ": " + ex.Message;
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

    private GateRead<ProtectionIntent> Missing(string step) =>
        new(GateReadStatus.Missing, null, StepOutcomes.FromHResult(step, HResultFileNotFound, "Missing: " + FilePath, ok: false));

    private GateRead<ProtectionIntent> Invalid(string step, string problem) =>
        new(GateReadStatus.Invalid, null, StepOutcomes.FromHResult(step, HResultInvalidData, FilePath + ": " + problem));
}
