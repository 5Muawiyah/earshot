using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.Repair;

// The plan on disk, for the elevated side to read. The unelevated tray writes it, so it sits in a folder the user can write:
// nothing about the file is trusted. The elevated side reads it once through one handle that allows no writer, hashes
// exactly the bytes it read and compares the hash with the one it was given on the command line, then re-derives the plan
// from the machine and applies only what the file and the machine both name. The file can therefore only narrow a run; a
// forged file, or one swapped after it was hashed, adds nothing.

internal sealed record FixPlanFileInfo(string Path, string Sha256);

// What the file names: nodes by instance id and action, and services. Compared with the plan re-derived from the machine.
internal sealed record FixPlanSelection(IReadOnlySet<(string Id, FixNodeAction Action)> Nodes, IReadOnlySet<Guid> Services);

internal sealed record FixPlanRead(bool Ok, string Problem, FixPlanSelection? Selection)
{
    public static FixPlanRead Failed(string problem) => new(false, problem, null);
}

internal sealed class FixPlanFiles : IFixPlanFiles
{
    public const string FileName = "fix-plan.json";

    public FixPlanFiles(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
    }

    public string Root { get; }

    // A new folder named by a random id, and the plan in it. CreateNew: a file or a link already there is a failure.
    public FixPlanFileInfo Write(FixPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        byte[] bytes = Serialise(plan);
        string folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, FileName);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
        }

        return new FixPlanFileInfo(path, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public void Delete(FixPlanFileInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        string? folder = Path.GetDirectoryName(info.Path);
        try
        {
            if (folder is not null && Directory.Exists(folder) && string.Equals(Path.GetDirectoryName(folder), Root, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A plan left behind is only a file nothing reads without its hash.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    internal static byte[] Serialise(FixPlan plan)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("SchemaVersion", 1);
            w.WriteStartArray("Nodes");
            foreach (FixNodeStep node in plan.Devices.SelectMany(d => d.Nodes))
            {
                w.WriteStartObject();
                w.WriteString("Id", node.InstanceId);
                w.WriteString("Action", node.Action == FixNodeAction.Enable ? "enable" : "clear-flag");
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("Services");
            foreach (FixServiceStep service in plan.Services)
            {
                w.WriteStringValue(service.Service.ToString("D", CultureInfo.InvariantCulture));
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }
}

internal static class FixPlanFileReader
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxNodes = 64;
    public const int MaxServices = 16;
    public const int MaxPathLength = 300;

    // No writer may open the file while this handle is. Reading once under this share and hashing those bytes is what makes
    // a swap after the read harmless.
    public const FileShare ShareMode = FileShare.Read;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    // The shape of the path alone: a drive letter, a normalised path, the plan's own file name. Used by the command line
    // check, which reads nothing.
    public static bool IsPlanPath(string path, out string problem)
    {
        if (path.Length is 0 or > MaxPathLength || path.Any(char.IsControl) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
        {
            problem = "the plan path does not start with a drive letter and a backslash";
            return false;
        }

        if (!string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(path), FixPlanFiles.FileName, StringComparison.OrdinalIgnoreCase))
        {
            problem = "the plan path is not a normalised path to " + FixPlanFiles.FileName;
            return false;
        }

        problem = "";
        return true;
    }

    public static FixPlanRead ReadOnce(string path, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        if (!IsPlanPath(path, out string shape))
        {
            return FixPlanRead.Failed("The plan was refused: " + shape + ".");
        }

        if (!FileManifest.IsSha256(expectedSha256))
        {
            return FixPlanRead.Failed("The plan was refused: the hash is not 64 hex characters.");
        }

        try
        {
            string? link = FirstLink(path);
            if (link is not null)
            {
                return FixPlanRead.Failed("The plan was refused: " + link + " is a link or a reparse point.");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, ShareMode);
            if (stream.Length > MaxBytes)
            {
                return FixPlanRead.Failed("The plan was refused: it is larger than " + MaxBytes.ToString(CultureInfo.InvariantCulture) + " bytes.");
            }

            byte[] bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            string actual = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return FixPlanRead.Failed("The plan was refused: its hash is not the one given.");
            }

            return Parse(bytes);
        }
        catch (IOException ex)
        {
            return FixPlanRead.Failed("The plan could not be read: HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + ex.GetType().Name + ".");
        }
        catch (UnauthorizedAccessException ex)
        {
            return FixPlanRead.Failed("The plan could not be read: HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + " " + ex.GetType().Name + ".");
        }
    }

    // The first of the file and its folders (up to, not including, the drive root) that is a reparse point, or null.
    private static string? FirstLink(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The plan file is not there.");
        }

        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return "the file";
        }

        for (DirectoryInfo? folder = file.Directory; folder?.Parent is not null; folder = folder.Parent)
        {
            if ((folder.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return "a folder on its path";
            }
        }

        return null;
    }

    internal static FixPlanRead Parse(byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, DocumentOptions);
        }
        catch (JsonException)
        {
            return FixPlanRead.Failed("The plan was refused: it is not valid JSON.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return FixPlanRead.Failed("The plan was refused: it is not an object.");
            }

            var names = root.EnumerateObject().Select(p => p.Name).ToList();
            if (names.Count != 3 || !names.Contains("SchemaVersion") || !names.Contains("Nodes") || !names.Contains("Services"))
            {
                return FixPlanRead.Failed("The plan was refused: it does not have exactly SchemaVersion, Nodes and Services.");
            }

            if (root.GetProperty("SchemaVersion").ValueKind != JsonValueKind.Number || !root.GetProperty("SchemaVersion").TryGetInt32(out int version) || version != 1)
            {
                return FixPlanRead.Failed("The plan was refused: SchemaVersion is not 1.");
            }

            JsonElement nodes = root.GetProperty("Nodes");
            JsonElement services = root.GetProperty("Services");
            if (nodes.ValueKind != JsonValueKind.Array || services.ValueKind != JsonValueKind.Array ||
                nodes.GetArrayLength() > MaxNodes || services.GetArrayLength() > MaxServices)
            {
                return FixPlanRead.Failed("The plan was refused: Nodes or Services is not a short array.");
            }

            var nodeSet = new HashSet<(string, FixNodeAction)>();
            foreach (JsonElement item in nodes.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Count() != 2 ||
                    !item.TryGetProperty("Id", out JsonElement id) || id.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("Action", out JsonElement action) || action.ValueKind != JsonValueKind.String)
                {
                    return FixPlanRead.Failed("The plan was refused: a node is not exactly an Id and an Action.");
                }

                string idText = id.GetString() ?? "";
                FixNodeAction? parsed = action.GetString() switch { "enable" => FixNodeAction.Enable, "clear-flag" => FixNodeAction.ClearFlag, _ => null };
                if (idText.Length is 0 or > 300 || parsed is null)
                {
                    return FixPlanRead.Failed("The plan was refused: a node has an empty or long id, or an unknown action.");
                }

                nodeSet.Add((idText.ToUpperInvariant(), parsed.Value));
            }

            var serviceSet = new HashSet<Guid>();
            foreach (JsonElement item in services.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !Guid.TryParseExact(item.GetString(), "D", out Guid service) || service == Guid.Empty)
                {
                    return FixPlanRead.Failed("The plan was refused: a service is not a GUID.");
                }

                serviceSet.Add(service);
            }

            return new FixPlanRead(true, "", new FixPlanSelection(nodeSet, serviceSet));
        }
    }
}
