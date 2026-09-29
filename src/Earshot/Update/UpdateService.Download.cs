using System.Net;
using System.Security.Cryptography;
using Earshot.Contracts;

namespace Earshot.Update;

// The download half: fetch the checksum, fetch the zip while hashing it, and only when the two agree unpack it once
// in a folder of its own as a check (a release that cannot be used is refused before the person is asked for the
// administrator prompt), then delete the unpacked copy and keep the zip. What gets installed is never that copy: the
// elevated update run copies the zip into a folder only administrators can write, hashes it there against the hash
// this download verified, and unpacks it itself. Any failure, or a cancel, deletes the folder this run made, so a
// failed update leaves nothing in the staging root and touches nothing outside it.
internal sealed partial class UpdateService
{
    // The name of the download inside its staging folder.
    internal const string ZipFileName = "update.zip";

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
            string verified = await DownloadAndCheckAsync(release, expected, work, zipPath, progress, ct).ConfigureAwait(false);
            finished = true;

            _log.Info("Update download: " + release.Version + " was downloaded, matched its checksum (" + verified + ") and unpacked cleanly as a check. " +
                      "The elevated update run checks the zip again from a copy only administrators can write.");
            return UpdateDownloadResult.Success(new StagedUpdate(release.Version, work, zipPath, verified, _log));
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

    // Downloads the zip, checks its SHA-256 against the checksum file and unpacks it once as a check. Returns the
    // verified hash as upper-case hex. The unpacked copy is deleted: it is not what gets installed.
    private async Task<string> DownloadAndCheckAsync(
        ReleaseInfo release, string expectedHex, string work, string zipPath, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        // The file is opened once, for writing, and stays open (readers only) until it has been unpacked, so
        // nothing can replace it between the check against the checksum and the unpacking.
        using var zip = new FileStream(zipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, BufferBytes, FileOptions.None);
        string actualHex;
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

            actualHex = await CopyAndHashAsync(response.Content, zip, declared, progress, deadline, ct).ConfigureAwait(false);
            if (!string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException(UpdateFailureKind.ChecksumMismatch,
                    "The download's SHA-256 is " + actualHex + " but the checksum file says " + expectedHex.ToUpperInvariant() + ".");
            }
        }

        zip.Position = 0;
        ReleaseArchive.Unpack(zip, work);
        StagingFolders.DeleteTree(Path.Combine(work, ReleaseArchive.AppFolderName), _log);
        return actualHex;
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
}
