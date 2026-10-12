using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Earshot.Setup;

internal sealed class PayloadException(string message) : Exception(message);

// Unpacks the release zip into a folder Setup has made and checked. Nothing in the archive is trusted: every name must be a
// plain relative path that lands inside the target (a name with "..", a drive, a stream colon or a leading slash is refused
// before anything is written), no name may repeat, files are only ever created (never over another file), and the numbers of
// entries and bytes are capped. The folder that holds the release's file list is the release folder, at the top of the zip or
// one folder down, and only that folder is unpacked.
internal static class ZipExtractor
{
    public const string ManifestName = "Earshot.files.json";
    public const int MaxEntries = 20_000;
    public const long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024;
    private const int BufferBytes = 81920;

    // Unpacks into target and returns the number of files written. Throws PayloadException for anything wrong with the archive.
    public static int Extract(Stream zip, string target)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        Directory.CreateDirectory(root);
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaxEntries)
        {
            throw new PayloadException("The zip holds " + archive.Entries.Count + " entries.");
        }

        string prefix = FindRootPrefix(archive);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        int files = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new PayloadException("The zip holds an entry outside the release folder: " + Bound(name) + ".");
            }

            string relative = name[prefix.Length..];
            if (relative.Length == 0 || relative.EndsWith('/'))
            {
                if (relative.Length > 0)
                {
                    // A folder entry: checked like a file, created when a file needs it.
                    CheckName(relative.TrimEnd('/'), root);
                }

                continue;
            }

            string destination = CheckName(relative, root);
            if (!seen.Add(relative))
            {
                throw new PayloadException("The zip holds " + Bound(relative) + " twice.");
            }

            total += entry.Length;
            if (total > MaxUnpackedBytes)
            {
                throw new PayloadException("The zip unpacks to more than " + MaxUnpackedBytes + " bytes.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            WriteEntry(entry, destination);
            files++;
        }

        return files;
    }

    // The path a name lands on, or an exception. Plain segments only; the full path must be inside the target.
    internal static string CheckName(string relative, string root)
    {
        if (relative.Length == 0 || relative.Length > 400 || relative[0] == '/' || relative.Any(char.IsControl) || relative.Contains(':', StringComparison.Ordinal))
        {
            throw new PayloadException("The zip holds a name that is not a plain relative path: " + Bound(relative) + ".");
        }

        foreach (string segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new PayloadException("The zip holds a name that is not a plain relative path: " + Bound(relative) + ".");
            }
        }

        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new PayloadException("The zip holds a name that lands outside the release folder: " + Bound(relative) + ".");
        }

        return full;
    }

    private static string FindRootPrefix(ZipArchive archive)
    {
        string? prefix = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name == ManifestName)
            {
                return prefix is null || prefix.Length == 0 ? "" : throw new PayloadException("The zip holds more than one file list.");
            }

            if (name.EndsWith("/" + ManifestName, StringComparison.Ordinal) && name.IndexOf('/', StringComparison.Ordinal) == name.Length - ManifestName.Length - 1)
            {
                if (prefix is not null)
                {
                    throw new PayloadException("The zip holds more than one file list.");
                }

                prefix = name[..^ManifestName.Length];
            }
        }

        return prefix ?? throw new PayloadException("The zip holds no " + ManifestName + ".");
    }

    private static void WriteEntry(ZipArchiveEntry entry, string destination)
    {
        using Stream source = entry.Open();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.None);
        byte[] buffer = new byte[BufferBytes];
        long written = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > entry.Length)
            {
                throw new PayloadException("An entry is longer than the size the zip declares for it.");
            }

            output.Write(buffer, 0, read);
        }

        if (written != entry.Length)
        {
            throw new PayloadException("An entry is shorter than the size the zip declares for it.");
        }
    }

    private static string Bound(string text) => text.Length <= 80 ? text : text[..80] + "...";
}

// The release's own file list against what was unpacked: every listed file there with its hash, nothing else there. This
// catches a damaged or truncated unpack; the list comes from the same zip, so it proves nothing about who made the zip.
internal static class ReleaseFileList
{
    public static void Verify(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string manifestPath = Path.Combine(root, ZipExtractor.ManifestName);
        JsonDocument document;
        try
        {
            using FileStream stream = File.OpenRead(manifestPath);
            if (stream.Length > 1024 * 1024)
            {
                throw new PayloadException(ZipExtractor.ManifestName + " is larger than a file list should be.");
            }

            document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            throw new PayloadException(ZipExtractor.ManifestName + " could not be read: " + ex.Message);
        }

        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ZipExtractor.ManifestName };
        using (document)
        {
            if (!document.RootElement.TryGetProperty("Files", out JsonElement files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
            {
                throw new PayloadException(ZipExtractor.ManifestName + " lists no files.");
            }

            foreach (JsonElement item in files.EnumerateArray())
            {
                string? path = item.TryGetProperty("Path", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                string? hash = item.TryGetProperty("Sha256", out JsonElement h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                if (path is null || !PayloadCheck.IsSha256(hash))
                {
                    throw new PayloadException(ZipExtractor.ManifestName + " has an entry that is not a path and a SHA-256.");
                }

                string full = ZipExtractor.CheckName(path, root);
                if (!listed.Add(path))
                {
                    throw new PayloadException(ZipExtractor.ManifestName + " lists " + path + " twice.");
                }

                if (!File.Exists(full))
                {
                    throw new PayloadException("The unpacked release is missing " + path + ".");
                }

                using FileStream file = File.OpenRead(full);
                string actual = Convert.ToHexString(SHA256.HashData(file));
                if (!string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PayloadException(path + " does not match the hash in the release's file list.");
                }
            }
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!listed.Contains(relative))
            {
                throw new PayloadException("The unpacked release holds " + relative + ", which its file list does not.");
            }
        }
    }
}
