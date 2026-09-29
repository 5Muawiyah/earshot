using System.IO.Compression;
using System.Security.Cryptography;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using static Earshot.Update.UpdateService;

namespace Earshot.Update;

// A release zip unpacked and checked, for the two places that do it: the tray, before it asks Windows for the
// administrator prompt (so a bad release is refused with a plain sentence and no prompt), and the elevated update run,
// which unpacks the copy it made in a folder only administrators can write and trusts nothing the tray checked.
//
// Nothing is trusted from the archive: every name must be a plain relative path inside the release's folder, none may
// repeat, and the totals are capped. Then the release's own file list is read and every unpacked file must match it,
// with nothing unlisted.
internal static class ReleaseArchive
{
    // The unpacked application folder inside a work folder.
    internal const string AppFolderName = "app";

    private const int BufferBytes = 81920;

    internal const long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024;

    private const string ExecutableName = Boot.TaskPlan.ExecutableName;

    // Unpacks zip into <work>\app, entry by entry, hashing what it writes, and checks the result against the
    // release's file list. Returns the application folder. Throws UpdateException for anything wrong with the archive.
    internal static string Unpack(Stream zip, string work)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentException.ThrowIfNullOrWhiteSpace(work);
        string app = Path.Combine(work, AppFolderName);
        Directory.CreateDirectory(app);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
        {
            string prefix = FindRootPrefix(archive);
            if (archive.Entries.Count > IntegrityCopy.MaxFiles * 2)
            {
                throw new UpdateException(UpdateFailureKind.BadArchive, "The zip holds " + archive.Entries.Count + " entries.");
            }

            long total = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/'))
                {
                    if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        throw new UpdateException(UpdateFailureKind.BadArchive, "The zip holds a folder outside the release folder: " + Bound(name) + ".");
                    }

                    continue;
                }

                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    throw new UpdateException(UpdateFailureKind.BadArchive, "The zip holds a file outside the release folder: " + Bound(name) + ".");
                }

                string relative = name[prefix.Length..];
                if (!FileManifest.IsRelativePath(relative))
                {
                    throw new UpdateException(UpdateFailureKind.BadArchive, "The zip holds a path that is not a plain relative path: " + Bound(name) + ".");
                }

                total += entry.Length;
                if (total > MaxUnpackedBytes)
                {
                    throw new UpdateException(UpdateFailureKind.TooLarge, "The zip unpacks to more than " + MaxUnpackedBytes + " bytes.");
                }

                string target = Path.GetFullPath(Path.Combine(app, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IntegrityCopy.IsInside(target, app) || string.Equals(target, app, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateException(UpdateFailureKind.BadArchive, "The zip holds a path that lands outside the release folder: " + Bound(name) + ".");
                }

                if (hashes.ContainsKey(relative))
                {
                    throw new UpdateException(UpdateFailureKind.BadArchive, "The zip holds " + Bound(relative) + " twice.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                hashes[relative] = WriteEntry(entry, target);
            }
        }

        CheckAgainstFileList(app, hashes);
        return app;
    }

    // The zip's release folder: the folder that holds Earshot.files.json, at the top of the zip or one folder down
    // (the release script puts everything under Earshot\). "" for the top.
    private static string FindRootPrefix(ZipArchive archive)
    {
        string? prefix = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            string manifest = FileManifest.FileName;
            if (name == manifest)
            {
                return prefix is null || prefix.Length == 0 ? "" : throw new UpdateException(UpdateFailureKind.BadArchive, "The zip holds more than one file list.");
            }

            if (name.EndsWith("/" + manifest, StringComparison.Ordinal) && name.IndexOf('/', StringComparison.Ordinal) == name.Length - manifest.Length - 1)
            {
                if (prefix is not null)
                {
                    throw new UpdateException(UpdateFailureKind.BadArchive, "The zip holds more than one file list.");
                }

                prefix = name[..^manifest.Length];
            }
        }

        return prefix ?? throw new UpdateException(UpdateFailureKind.BadArchive, "The zip holds no " + FileManifest.FileName + ".");
    }

    // Writes one entry to a new file (never over one), hashing the bytes as they go, and returns the hash. The
    // number of bytes written is bounded by the entry's declared length, so an entry that lies about its size
    // cannot fill the disk.
    private static string WriteEntry(ZipArchiveEntry entry, string target)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using Stream source = entry.Open();
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.None);
        byte[] buffer = new byte[BufferBytes];
        long written = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > entry.Length)
            {
                throw new UpdateException(UpdateFailureKind.BadArchive, "An entry is longer than the size the zip declares for it.");
            }

            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }

        if (written != entry.Length)
        {
            throw new UpdateException(UpdateFailureKind.BadArchive, "An entry is shorter than the size the zip declares for it.");
        }

        output.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // The release's file list against what was unpacked: every listed file is there with the listed hash, and nothing
    // else is.
    private static void CheckAgainstFileList(string app, Dictionary<string, string> unpacked)
    {
        FileManifest? manifest = FileManifest.Read(app, out StepOutcome step);
        if (manifest is null)
        {
            throw new UpdateException(UpdateFailureKind.BadArchive, "The release's file list could not be used: " + Bound(step.Detail ?? step.Step) + ".");
        }

        if (manifest.HashOf(ExecutableName) is null)
        {
            throw new UpdateException(UpdateFailureKind.BadArchive, "The release's file list does not name " + ExecutableName + ".");
        }

        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FileManifest.FileName };
        foreach (ManifestFile file in manifest.Files)
        {
            string key = file.RelativePath.Replace(Path.DirectorySeparatorChar, '/');
            if (!unpacked.TryGetValue(key, out string? actual))
            {
                throw new UpdateException(UpdateFailureKind.BadArchive, "The zip lacks " + Bound(key) + ", which the release's file list names.");
            }

            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException(UpdateFailureKind.BadArchive, Bound(key) + " does not match the hash the release's file list records.");
            }

            listed.Add(key);
        }

        foreach (string name in unpacked.Keys)
        {
            if (!listed.Contains(name))
            {
                throw new UpdateException(UpdateFailureKind.BadArchive, "The zip holds " + Bound(name) + ", which the release's file list does not name.");
            }
        }
    }
}
