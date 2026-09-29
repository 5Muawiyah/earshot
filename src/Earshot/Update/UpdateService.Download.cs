using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.Update;

// The download half: fetch the checksum, fetch the zip while hashing it, and only when the two agree unpack it into
// a folder of its own, check the unpacked files against the release's own file list and hold them. Any failure, or
// a cancel, deletes the folder this run made, so a failed update leaves nothing in the staging root and touches
// nothing outside it.
internal sealed partial class UpdateService
{
    // The name of the download inside its staging folder, and of the unpacked application folder.
    internal const string ZipFileName = "update.zip";
    internal const string AppFolderName = "app";
    internal const long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024;

    public async Task<UpdateDownloadResult> DownloadAsync(ReleaseInfo release, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        string? work = null;
        bool finished = false;
        try
        {
            StagingFolders.CleanStale(_stagingRoot, _log);
            work = Path.Combine(_stagingRoot, StagingFolders.FolderPrefix + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);

            string expected = await FetchChecksumAsync(release, ct).ConfigureAwait(false);
            string zipPath = Path.Combine(work, ZipFileName);
            StagedUpdate staged = await DownloadAndUnpackAsync(release, expected, work, zipPath, progress, ct).ConfigureAwait(false);
            finished = true;

            // The zip has done its job; the unpacked files are what the hand-over uses. A zip that cannot be
            // deleted now is logged and left in the folder the next clean-up removes.
            DeleteFile(zipPath);
            _log.Info("Update download: " + release.Version + " was downloaded, matched its checksum and was unpacked into " + staged.AppFolder + ".");
            return UpdateDownloadResult.Success(staged);
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException)
        {
            UpdateFailure failure = Describe(ex, "download", ct);
            _log.Warn("Update download failed (" + failure.Kind + "): " + failure.Detail, failure.Kind == UpdateFailureKind.Cancelled ? null : ex);
            return UpdateDownloadResult.Failed(failure);
        }
        finally
        {
            // Whatever the way out, a folder this run made is deleted unless it became a StagedUpdate.
            if (!finished && work is not null)
            {
                StagingFolders.DeleteTree(work, _log);
            }
        }
    }

    private void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            _log.Warn("Update staging: " + path + " was not removed (" + ex.GetType().Name + "): " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("Update staging: " + path + " was not removed (" + ex.GetType().Name + "): " + ex.Message);
        }
    }

    private async Task<string> FetchChecksumAsync(ReleaseInfo release, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeouts.Request);
        using HttpClient http = CreateClient();
        using HttpResponseMessage response = await GetAsync(http, release.ChecksumUri, "application/octet-stream", deadline.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw StatusFailure(response, "the checksum file " + release.ChecksumName);
        }

        byte[] body = await ReadBoundedAsync(response.Content, MaxChecksumBytes, "the checksum file", deadline.Token).ConfigureAwait(false);
        if (!TryParseChecksum(body, release.ZipName, out string hex))
        {
            throw new UpdateException(UpdateFailureKind.ChecksumMalformed,
                "The checksum file " + release.ChecksumName + " is not '<64 hex characters>  " + release.ZipName + "' on one line (" + body.Length + " bytes).");
        }

        return hex;
    }

    private async Task<StagedUpdate> DownloadAndUnpackAsync(
        ReleaseInfo release, string expectedHex, string work, string zipPath, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        // The file is opened once, for writing, and stays open (readers only) until it has been unpacked, so
        // nothing can replace it between the check against the checksum and the unpacking.
        using var zip = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, BufferBytes, FileOptions.None);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(_timeouts.Request);
            using HttpClient http = CreateClient();
            using HttpResponseMessage response = await GetAsync(http, release.ZipUri, "application/octet-stream", deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw StatusFailure(response, "the download " + release.ZipName);
            }

            long? declared = response.Content.Headers.ContentLength;
            if (declared > MaxZipBytes)
            {
                throw new UpdateException(UpdateFailureKind.TooLarge, "The download declared " + declared + " bytes, over the " + MaxZipBytes + " byte limit.");
            }

            string actualHex = await CopyAndHashAsync(response.Content, zip, declared, progress, deadline, ct).ConfigureAwait(false);
            if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException(UpdateFailureKind.ChecksumMismatch,
                    "The download's SHA-256 is " + actualHex + " but the checksum file says " + expectedHex.ToUpperInvariant() + ".");
            }
        }

        zip.Position = 0;
        StagedUpdate staged = Unpack(release, zip, work);
        return staged;
    }

    // Writes the body to the file while hashing it, checks the length against what the server declared, and returns
    // the SHA-256 as upper-case hex. The stall timer is set again before each read.
    private async Task<string> CopyAndHashAsync(
        HttpContent content, FileStream destination, long? declared, IProgress<UpdateProgress>? progress, CancellationTokenSource deadline, CancellationToken ct)
    {
        using Stream source = await content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferBytes];
        long received = 0;
        int lastPercent = -1;
        long lastReport = 0;
        progress?.Report(new UpdateProgress(0, declared));
        while (true)
        {
            deadline.CancelAfter(_timeouts.Stall);
            int read = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            received += read;
            if (received > MaxZipBytes)
            {
                throw new UpdateException(UpdateFailureKind.TooLarge, "The download passed the " + MaxZipBytes + " byte limit.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            if (progress is not null)
            {
                var now = new UpdateProgress(received, declared);
                if (now.Percent is int percent ? percent != lastPercent : received - lastReport >= 512 * 1024)
                {
                    lastPercent = now.Percent ?? lastPercent;
                    lastReport = received;
                    progress.Report(now);
                }
            }
        }

        if (declared is long expected && received != expected)
        {
            throw new UpdateException(UpdateFailureKind.Truncated, "The download ended after " + received + " of " + expected + " bytes.");
        }

        await destination.FlushAsync(ct).ConfigureAwait(false);
        progress?.Report(new UpdateProgress(received, declared ?? received));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Unpacks the verified zip into <work>\app, entry by entry, hashing what it writes. Nothing is trusted from the
    // archive: every name must be a plain relative path inside the release's folder, none may repeat, and the totals
    // are capped. Then the release's own file list is read and every unpacked file must match it, with nothing
    // unlisted, before the files are opened and held.
    private StagedUpdate Unpack(ReleaseInfo release, FileStream zipStream, string work)
    {
        string app = Path.Combine(work, AppFolderName);
        Directory.CreateDirectory(app);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true))
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

        return HoldAndCheck(release, work, app, hashes);
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

    // The release's file list against what was unpacked, then every unpacked file opened for reading and held,
    // and hashed once more through the held handle, so what the hand-over runs is what was checked.
    private StagedUpdate HoldAndCheck(ReleaseInfo release, string work, string app, Dictionary<string, string> unpacked)
    {
        FileManifest? manifest = FileManifest.Read(app, out StepOutcome step);
        if (manifest is null)
        {
            throw new UpdateException(UpdateFailureKind.BadArchive, "The release's file list could not be used: " + Bound(step.Detail ?? step.Step) + ".");
        }

        if (manifest.HashOf(TaskPlanExecutableName) is null)
        {
            throw new UpdateException(UpdateFailureKind.BadArchive, "The release's file list does not name " + TaskPlanExecutableName + ".");
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

        var held = new List<FileStream>(unpacked.Count);
        try
        {
            foreach ((string relative, string expected) in unpacked)
            {
                string path = Path.Combine(app, relative.Replace('/', Path.DirectorySeparatorChar));
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.SequentialScan);
                held.Add(stream);
                string again = Convert.ToHexString(SHA256.HashData(stream));
                stream.Position = 0;
                if (!string.Equals(again, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UpdateException(UpdateFailureKind.BadArchive, Bound(relative) + " changed on disk after it was unpacked.");
                }
            }
        }
        catch
        {
            foreach (FileStream stream in held)
            {
                stream.Dispose();
            }

            throw;
        }

        string exe = Path.Combine(app, TaskPlanExecutableName);
        return new StagedUpdate(release.Version, work, app, exe, held, _log);
    }

    private const string TaskPlanExecutableName = Boot.TaskPlan.ExecutableName;
}
