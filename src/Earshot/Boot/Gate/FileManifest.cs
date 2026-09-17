using System.Security.Cryptography;
using System.Text.Json;
using Earshot.Contracts;

namespace Earshot.Boot.Gate;

// One published file: its path relative to the application folder, with "/" separators, and the SHA-256 of its
// content as hex.
internal sealed record ManifestFile(string RelativePath, string Sha256);

// Earshot.files.json, written next to the published files by the publish step and read by install. It lists
// every published file except itself, so install copies exactly what was published and nothing else that may
// sit in the folder the release was unzipped into, and can check each copy against the hash recorded at
// publish time. Install copies the manifest too, so a repair run from the install folder has one to check the
// installed files against.
//
// The file sits in a user-writable folder, so it is read as strictly as the gate's own files: a size cap, no
// reparse point, exactly the expected members with the expected types, and a relative path that cannot leave
// the folder.
// https://learn.microsoft.com/en-us/visualstudio/msbuild/getfilehash-task
// https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsondocument
internal sealed class FileManifest
{
    public const string FileName = "Earshot.files.json";
    public const int SchemaVersion = 1;
    public const int MaxBytes = 1024 * 1024;
    public const int MaxFiles = IntegrityCopy.MaxFiles;
    public const string ReadStep = "read-file-manifest";

    // What the tray shows when there is no manifest to install from.
    public const string MissingMessage = "Install from a release build.";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    private FileManifest(IReadOnlyList<ManifestFile> files, string sha256)
    {
        Files = files;
        ContentSha256 = sha256;
    }

    public IReadOnlyList<ManifestFile> Files { get; }

    // The SHA-256 of the manifest file's bytes as read, as upper-case hex, so a copy of it can be checked too.
    public string ContentSha256 { get; }

    // The manifest in folder, or null with a failed step saying why. A missing file and an invalid one are both
    // refusals: install only ever runs from a published folder.
    public static FileManifest? Read(string folder, out StepOutcome step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string path = Path.Combine(folder, FileName);
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                step = StepOutcomes.FromHResult(ReadStep, unchecked((int)0x80070002), "Missing: " + path + ". " + MissingMessage, ok: false);
                return null;
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                step = Invalid(path, "The file is a reparse point.");
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.None);
            if (stream.Length > MaxBytes)
            {
                step = Invalid(path, "The file is larger than " + MaxBytes + " bytes.");
                return null;
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (IOException ex)
        {
            step = StepOutcomes.FromHResult(ReadStep, ex.HResult, path + ": " + ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            step = StepOutcomes.FromHResult(ReadStep, ex.HResult, path + ": " + ex.Message);
            return null;
        }

        // The publish step writes UTF-8; a byte order mark in front of it is not part of the JSON.
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            json = json[3..];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray(), DocumentOptions);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                step = Invalid(path, "The root is not a JSON object.");
                return null;
            }

            string? shape = GateStore.RequireShape(root, ("SchemaVersion", JsonValueKind.Number), (nameof(Files), JsonValueKind.Array));
            if (shape is not null)
            {
                step = Invalid(path, shape);
                return null;
            }

            if (!root.GetProperty("SchemaVersion").TryGetInt32(out int version) || version != SchemaVersion)
            {
                step = Invalid(path, "SchemaVersion is not " + SchemaVersion + ".");
                return null;
            }

            JsonElement files = root.GetProperty(nameof(Files));
            if (files.GetArrayLength() is 0 or > MaxFiles)
            {
                step = Invalid(path, nameof(Files) + " holds no file, or more than " + MaxFiles + ".");
                return null;
            }

            var read = new List<ManifestFile>(files.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement item in files.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    step = Invalid(path, "A file is not an object.");
                    return null;
                }

                string? itemShape = GateStore.RequireShape(item, ("Path", JsonValueKind.String), ("Sha256", JsonValueKind.String));
                if (itemShape is not null)
                {
                    step = Invalid(path, itemShape);
                    return null;
                }

                string relative = item.GetProperty("Path").GetString() ?? "";
                string hash = item.GetProperty("Sha256").GetString() ?? "";
                if (!IsRelativePath(relative))
                {
                    step = Invalid(path, "A file path is not a plain relative path: " + GateStore.Bound(relative, 80) + ".");
                    return null;
                }

                if (string.Equals(relative, FileName, StringComparison.OrdinalIgnoreCase))
                {
                    step = Invalid(path, "The manifest lists itself.");
                    return null;
                }

                if (!IsSha256(hash))
                {
                    step = Invalid(path, "A file hash is not 64 hex characters.");
                    return null;
                }

                if (!seen.Add(relative))
                {
                    step = Invalid(path, "A file path appears twice.");
                    return null;
                }

                read.Add(new ManifestFile(relative.Replace('/', Path.DirectorySeparatorChar), hash));
            }

            step = new StepOutcome(ReadStep, true, 0, "S_OK", path + ": " + read.Count + " files.");
            return new FileManifest(read, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (JsonException ex)
        {
            step = Invalid(path, "Not valid JSON: " + ex.Message);
            return null;
        }
    }

    // The hash the manifest records for a copied file, or null when it lists no such file.
    public string? HashOf(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        foreach (ManifestFile file in Files)
        {
            if (string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                return file.Sha256;
            }
        }

        return null;
    }

    // A path of one or more plain segments, separated by "/", that cannot leave the folder: no root, no drive,
    // no "..", no backslash, and nothing a file name cannot hold.
    internal static bool IsRelativePath(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 240 || value.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        string[] segments = value.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." ||
                segment[0] == ' ' || segment[^1] is ' ' or '.' ||
                segment.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsSha256(string value) =>
        value.Length == 64 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    private static StepOutcome Invalid(string path, string problem) =>
        StepOutcomes.FromHResult(ReadStep, unchecked((int)0x8007000D), path + ": " + problem + " " + MissingMessage);
}
