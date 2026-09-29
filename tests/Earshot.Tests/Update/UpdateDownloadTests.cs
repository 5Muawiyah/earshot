using System.Net;
using System.Text;
using Earshot.Contracts;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// The download half of the update service, against the same fake feed. Every failure must leave the staging folder
// empty and touch nothing outside it; the one success must leave the unpacked files held.
[TestClass]
public sealed class UpdateDownloadTests
{
    private static readonly ReleaseVersion Running = UpdateAsserts.Running;

    private sealed class Run : IDisposable
    {
        public Run(FeedFixture feed, Func<ReleaseInfo, ReleaseInfo>? adjust = null, UpdateTimeouts? timeouts = null)
        {
            Feed = feed;
            Temp = new TempFolder();
            Staging = Temp.File("staging");
            Service = feed.Service(Log, Staging, Running, timeouts);
            UpdateCheckResult check = Service.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(UpdateCheckOutcome.Available, check.Outcome, check.Failure?.Detail);
            Release = adjust is null ? check.Release! : adjust(check.Release!);
        }

        public FeedFixture Feed { get; }

        public TempFolder Temp { get; }

        public string Staging { get; }

        public CapturingLog Log { get; } = new();

        public UpdateService Service { get; }

        public ReleaseInfo Release { get; }

        public List<UpdateProgress> Progress { get; } = new();

        public Task<UpdateDownloadResult> DownloadAsync(CancellationToken ct = default) =>
            Service.DownloadAsync(Release, new SyncProgress(Progress), ct);

        public void Dispose()
        {
            Temp.Dispose();
        }
    }

    // Progress<T> posts to the pool; this one is synchronous, so a test reads the reports as they were made.
    private sealed class SyncProgress(List<UpdateProgress> into) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value)
        {
            lock (into)
            {
                into.Add(value);
            }
        }
    }

    private static async Task<UpdateFailure> FailsAsync(Run run, UpdateFailureKind kind)
    {
        UpdateDownloadResult result = await run.DownloadAsync();

        Assert.IsNull(result.Staged, "The download was not supposed to succeed.");
        Assert.AreEqual(kind, result.Failure!.Kind, result.Failure.Detail);
        UpdateAsserts.StagingIsEmpty(run.Staging);
        Assert.IsTrue(run.Log.Has(LogLevel.Warn, "Update download failed (" + kind + ")"), "The failure is logged with its cause.");
        return result.Failure;
    }

    // ----- success -----

    [TestMethod]
    public async Task AVerifiedDownloadIsUnpackedAndItsFilesAreHeld()
    {
        using var feed = new FeedFixture("v1.2.0");
        using var run = new Run(feed);

        UpdateDownloadResult result = await run.DownloadAsync();

        Assert.IsNotNull(result.Staged, result.Failure?.Detail);
        using StagedUpdate staged = result.Staged;
        Assert.AreEqual(new ReleaseVersion(1, 2, 0), staged.Version);
        Assert.IsTrue(staged.WorkFolder.StartsWith(run.Staging, StringComparison.OrdinalIgnoreCase), "The staging folder is under the staging root.");
        Assert.AreEqual(Path.Combine(staged.AppFolder, "Earshot.exe"), staged.ExecutablePath);
        Assert.IsTrue(File.Exists(staged.ExecutablePath));
        Assert.IsTrue(File.Exists(Path.Combine(staged.AppFolder, "Earshot.files.json")));
        Assert.IsTrue(File.Exists(Path.Combine(staged.AppFolder, "runtimes", "native.txt")), "A nested file is unpacked in place.");
        Assert.IsFalse(File.Exists(Path.Combine(staged.WorkFolder, UpdateService.ZipFileName)), "The zip is removed once it is unpacked.");
        Assert.AreEqual(1, feed.Server.Count("/dl/" + feed.ZipName));
        Assert.AreEqual(1, feed.Server.Count("/dl/" + feed.ChecksumName));

        // Held: nobody else can write to a staged file, replace it or delete it while the hand-over runs it.
        Assert.ThrowsExactly<IOException>(() => File.WriteAllBytes(staged.ExecutablePath, [1, 2, 3]));
        Assert.ThrowsExactly<IOException>(() => File.Delete(staged.ExecutablePath));
        Assert.ThrowsExactly<IOException>(() => Directory.Move(staged.AppFolder, staged.AppFolder + "-moved"));

        // Reading is still allowed, which the elevated program needs.
        using (new FileStream(staged.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
        }
    }

    [TestMethod]
    public async Task ProgressReachesTheFullSizeAndNeverGoesBackwards()
    {
        using var feed = new FeedFixture("v1.2.0");
        using var run = new Run(feed);

        UpdateDownloadResult result = await run.DownloadAsync();

        using StagedUpdate staged = result.Staged!;
        UpdateProgress[] reports = run.Progress.ToArray();
        Assert.IsNotEmpty(reports);
        Assert.AreEqual(feed.Zip.Length, reports[^1].Received);
        Assert.AreEqual(100, reports[^1].Percent);
        for (int i = 1; i < reports.Length; i++)
        {
            Assert.IsGreaterThanOrEqualTo(reports[i - 1].Received, reports[i].Received);
        }
    }

    [TestMethod]
    public async Task AnUpperCaseChecksumIsAccepted()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Server.MapText("/dl/" + feed.ChecksumName, feed.ZipHash.ToUpperInvariant() + "  " + feed.ZipName + "\r\n");
        using var run = new Run(feed);

        UpdateDownloadResult result = await run.DownloadAsync();

        Assert.IsNotNull(result.Staged, result.Failure?.Detail);
        result.Staged.Dispose();
    }

    [TestMethod]
    public async Task DownloadsThroughAnAllowedRedirectAreFollowed()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Server.MapRedirect("/dl/" + feed.ZipName, "/cdn/" + feed.ZipName);
        feed.Server.MapBytes("/cdn/" + feed.ZipName, feed.Zip);
        using var run = new Run(feed);

        UpdateDownloadResult result = await run.DownloadAsync();

        Assert.IsNotNull(result.Staged, result.Failure?.Detail);
        result.Staged.Dispose();
        Assert.AreEqual(1, feed.Server.Count("/cdn/" + feed.ZipName));
    }

    [TestMethod]
    public async Task DownloadsSendTheUserAgentToo()
    {
        using var feed = new FeedFixture("v1.2.0");
        using var run = new Run(feed);

        UpdateDownloadResult result = await run.DownloadAsync();

        result.Staged!.Dispose();
        foreach (RecordedRequest request in feed.Server.Requests)
        {
            StringAssert.StartsWith(request.UserAgent, "Earshot/1.1.0", request.Path);
        }
    }

    // ----- the checksum -----

    [TestMethod]
    public async Task AChecksumThatDoesNotMatchStopsTheUpdateAndLeavesNothing()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Server.MapText("/dl/" + feed.ChecksumName, new string('0', 64) + "  " + feed.ZipName + "\n");
        using var run = new Run(feed);

        UpdateFailure failure = await FailsAsync(run, UpdateFailureKind.ChecksumMismatch);

        StringAssert.Contains(failure.Detail, feed.ZipHash, "The log names the hash that was computed.");
    }

    [TestMethod]
    public async Task AZipChangedByOneByteDoesNotMatchTheChecksumThePublisherGave()
    {
        using var feed = new FeedFixture("v1.2.0");
        byte[] tampered = (byte[])feed.Zip.Clone();
        tampered[tampered.Length / 2] ^= 0x01;
        feed.Server.MapBytes("/dl/" + feed.ZipName, tampered);
        using var run = new Run(feed);

        await FailsAsync(run, UpdateFailureKind.ChecksumMismatch);
    }

    [TestMethod]
    public async Task ADownloadWithNoChecksumAssetIsRefusedAndNothingIsDownloaded()
    {
        using var feed = new FeedFixture("v1.2.0");
        using var run = new Run(feed);
        feed.Server.MapStatus("/dl/" + feed.ChecksumName, HttpStatusCode.NotFound);

        UpdateFailure failure = await FailsAsync(run, UpdateFailureKind.HttpStatus);

        StringAssert.Contains(failure.Detail, "404");
        Assert.AreEqual(0, feed.Server.Count("/dl/" + feed.ZipName), "The zip is not fetched when the checksum cannot be.");
    }

    [TestMethod]
    public async Task AReleaseListedWithoutTheChecksumIsRefusedByTheCheck()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Publish(checksumAsset: false);
        using var temp = new TempFolder();

        UpdateCheckResult check = await feed.Service(new CapturingLog(), temp.File("staging"), Running).CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateFailureKind.NoChecksumAsset, check.Failure!.Kind);
        Assert.AreEqual(0, feed.Server.Count("/dl/" + feed.ZipName));
    }

    [TestMethod]
    [DataRow("", DisplayName = "empty")]
    [DataRow("nothex", DisplayName = "not hex")]
    [DataRow("{s}  Earshot-1.2.0-win-x64.zip", DisplayName = "hash too short")]
    [DataRow("{h} Earshot-1.2.0-win-x64.zip", DisplayName = "one space")]
    [DataRow("{h}   Earshot-1.2.0-win-x64.zip", DisplayName = "three spaces")]
    [DataRow("{h}  other-name.zip", DisplayName = "another file's name")]
    [DataRow("{h}", DisplayName = "no name")]
    [DataRow("{h}  Earshot-1.2.0-win-x64.zip extra", DisplayName = "trailing text")]
    [DataRow("{h}  Earshot-1.2.0-win-x64.zip\n{h}  Earshot-1.2.0-win-x64.zip", DisplayName = "two lines")]
    [DataRow("{g}  Earshot-1.2.0-win-x64.zip", DisplayName = "a non-hex letter")]
    public async Task AMalformedChecksumFileStopsTheUpdate(string template)
    {
        using var feed = new FeedFixture("v1.2.0");
        string hash = feed.ZipHash.ToLowerInvariant();
        string content = template.Replace("{h}", hash, StringComparison.Ordinal).Replace("{g}", "g" + hash[1..], StringComparison.Ordinal).Replace("{s}", hash[1..], StringComparison.Ordinal);
        feed.Server.MapText("/dl/" + feed.ChecksumName, content);
        using var run = new Run(feed);

        await FailsAsync(run, UpdateFailureKind.ChecksumMalformed);
        Assert.AreEqual(0, feed.Server.Count("/dl/" + feed.ZipName), "Nothing is downloaded against a checksum that cannot be read.");
    }

    [TestMethod]
    public async Task AChecksumFileWithAByteOrderMarkOrUtf16IsHandledSafely()
    {
        using var feed = new FeedFixture("v1.2.0");
        string line = feed.ZipHash + "  " + feed.ZipName + "\r\n";
        feed.Server.MapBytes("/dl/" + feed.ChecksumName, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(line)]);
        using (var run = new Run(feed))
        {
            UpdateDownloadResult ok = await run.DownloadAsync();
            Assert.IsNotNull(ok.Staged, "A UTF-8 byte order mark is not part of the hash.");
            ok.Staged.Dispose();
        }

        // What PowerShell 5.1 writes by default: UTF-16. It is not readable as the sha256sum format, so it fails closed.
        feed.Server.MapBytes("/dl/" + feed.ChecksumName, Encoding.Unicode.GetBytes(line));
        using var run2 = new Run(feed);
        await FailsAsync(run2, UpdateFailureKind.ChecksumMalformed);
    }

    [TestMethod]
    public void TheChecksumParserAcceptsTheSha256sumFormatOnly()
    {
        string hash = new('a', 64);
        Assert.IsTrue(UpdateService.TryParseChecksum(Encoding.ASCII.GetBytes(hash + "  x.zip\n"), "x.zip", out string hex));
        Assert.AreEqual(hash, hex);
        Assert.IsTrue(UpdateService.TryParseChecksum(Encoding.ASCII.GetBytes(hash + " *x.zip"), "x.zip", out _), "Binary mode of sha256sum.");
        Assert.IsTrue(UpdateService.TryParseChecksum(Encoding.ASCII.GetBytes(hash.ToUpperInvariant() + "  x.zip"), "x.zip", out _));
        Assert.IsFalse(UpdateService.TryParseChecksum(Encoding.ASCII.GetBytes(hash + "  X.zip"), "x.zip", out _), "The name is compared exactly.");
        Assert.IsFalse(UpdateService.TryParseChecksum([0xFF, 0xFE, 0x00], "x.zip", out _), "Not UTF-8.");
    }

    // ----- the download itself -----

    [TestMethod]
    public async Task ADownloadCutShortFailsAndLeavesNothing()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Server.MapTruncated("/dl/" + feed.ZipName, feed.Zip, feed.Zip.Length / 2);
        using var run = new Run(feed);

        UpdateDownloadResult result = await run.DownloadAsync();

        Assert.IsNull(result.Staged);
        Assert.IsTrue(result.Failure!.Kind is UpdateFailureKind.Truncated or UpdateFailureKind.Network, "Cut short was " + result.Failure.Kind + ": " + result.Failure.Detail);
        UpdateAsserts.StagingIsEmpty(run.Staging);
        Assert.IsTrue(run.Log.Entries.Any(e => e.Level == LogLevel.Warn && e.Message.Contains("Update download failed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ADownloadThatStallsTimesOutAndLeavesNothing()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Server.MapStalled("/dl/" + feed.ZipName, feed.Zip, feed.Zip.Length / 2);
        using var run = new Run(feed, timeouts: new UpdateTimeouts(TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(400)));

        await FailsAsync(run, UpdateFailureKind.TimedOut);
    }

    [TestMethod]
    public async Task ADownloadThatIsCancelledIsCancelledAndLeavesNothing()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Server.MapStalled("/dl/" + feed.ZipName, feed.Zip, feed.Zip.Length / 2);
        using var run = new Run(feed);
        using var cancel = new CancellationTokenSource();

        Task<UpdateDownloadResult> download = run.DownloadAsync(cancel.Token);
        await Task.Delay(500);
        await cancel.CancelAsync();
        UpdateDownloadResult result = await download;

        Assert.IsNull(result.Staged);
        Assert.AreEqual(UpdateFailureKind.Cancelled, result.Failure!.Kind);
        UpdateAsserts.StagingIsEmpty(run.Staging);
    }

    [TestMethod]
    public async Task ADownloadThatAnswers404Or500IsRecordedWithItsStatusAndLeavesNothing()
    {
        foreach ((HttpStatusCode status, string text) in new[] { (HttpStatusCode.NotFound, "404"), (HttpStatusCode.InternalServerError, "500") })
        {
            using var feed = new FeedFixture("v1.2.0");
            using var run = new Run(feed);
            feed.Server.MapStatus("/dl/" + feed.ZipName, status);

            UpdateFailure failure = await FailsAsync(run, UpdateFailureKind.HttpStatus);

            StringAssert.Contains(failure.Detail, text);
            Assert.IsTrue(run.Log.Has(LogLevel.Warn, text), "The raw status is in the log.");
        }
    }

    [TestMethod]
    public async Task ADownloadRedirectedToANonHttpsAddressIsRefusedAndLeavesNothing()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Server.MapRedirect("/dl/" + feed.ZipName, "http://downloads.example.test/" + feed.ZipName);
        using var run = new Run(feed);

        UpdateFailure failure = await FailsAsync(run, UpdateFailureKind.NotHttps);

        StringAssert.Contains(failure.Detail, "downloads.example.test");
    }

    [TestMethod]
    public async Task AChecksumRedirectedToANonHttpsAddressIsRefused()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Server.MapRedirect("/dl/" + feed.ChecksumName, "http://downloads.example.test/x.sha256");
        using var run = new Run(feed);

        await FailsAsync(run, UpdateFailureKind.NotHttps);
        Assert.AreEqual(0, feed.Server.Count("/dl/" + feed.ZipName));
    }

    [TestMethod]
    public async Task AReleaseInfoWithANonHttpsAddressIsRefusedBeforeAnyRequest()
    {
        using var feed = new FeedFixture("v1.2.0");
        using var run = new Run(feed, r => r with { ZipUri = new Uri("http://downloads.example.test/" + feed.ZipName) });
        int before = feed.Server.Requests.Count;

        await FailsAsync(run, UpdateFailureKind.NotHttps);

        Assert.AreEqual(before + 1, feed.Server.Requests.Count, "Only the checksum was requested; the zip address was refused without a request.");
    }

    [TestMethod]
    public async Task ADownloadLargerThanTheLimitIsRefusedFromItsHeader()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Server.Map("/dl/" + feed.ZipName, context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = UpdateService.MaxZipBytes + 1;
            context.Response.OutputStream.Write(new byte[16], 0, 16);
            context.Response.OutputStream.Flush();
            context.Response.Abort();
        });
        using var run = new Run(feed);

        await FailsAsync(run, UpdateFailureKind.TooLarge);
    }

    // ----- the archive -----

    private static async Task ArchiveIsRefusedAsync(Action<ReleaseZipBuilder> tweak, string mustMention)
    {
        var builder = new ReleaseZipBuilder();
        tweak(builder);
        using var feed = new FeedFixture("v1.2.0", builder);
        using var run = new Run(feed);

        UpdateFailure failure = await FailsAsync(run, UpdateFailureKind.BadArchive);

        StringAssert.Contains(failure.Detail, mustMention);
        string escaped = Path.Combine(run.Temp.Path, "evil.txt");
        Assert.IsFalse(File.Exists(escaped), "Nothing was written outside the staging folder.");
        Assert.IsFalse(File.Exists(Path.Combine(run.Staging, "evil.txt")));
    }

    [TestMethod]
    public Task AZipWithNoFileListIsRefused() =>
        ArchiveIsRefusedAsync(b => b.IncludeManifest = false, "Earshot.files.json");

    [TestMethod]
    public Task AFileThatDoesNotMatchTheReleasesOwnFileListIsRefused() =>
        ArchiveIsRefusedAsync(b => b.WrongHash["Earshot.dll"] = new string('1', 64), "Earshot.dll");

    [TestMethod]
    public Task AFileTheFileListDoesNotNameIsRefused() =>
        ArchiveIsRefusedAsync(b => b.Unlisted.Add("runtimes/native.txt"), "runtimes/native.txt");

    [TestMethod]
    public Task AZipWithoutTheProgramIsRefused() =>
        ArchiveIsRefusedAsync(b => b.Files.Remove("Earshot.exe"), "Earshot.exe");

    [TestMethod]
    public Task AnEntryThatClimbsOutOfTheFolderIsRefused() =>
        ArchiveIsRefusedAsync(b => b.RawEntries.Add(("Earshot/../../evil.txt", [1])), "evil.txt");

    [TestMethod]
    public Task AnEntryThatClimbsOutWithBackslashesIsRefused() =>
        ArchiveIsRefusedAsync(b => b.RawEntries.Add(("Earshot\\..\\..\\evil.txt", [1])), "evil.txt");

    [TestMethod]
    public Task AnAbsolutePathEntryIsRefused() =>
        ArchiveIsRefusedAsync(b => b.RawEntries.Add(("C:/evil.txt", [1])), "evil.txt");

    [TestMethod]
    public Task AnEntryOutsideTheReleaseFolderIsRefused() =>
        ArchiveIsRefusedAsync(b => b.RawEntries.Add(("evil.txt", [1])), "evil.txt");

    [TestMethod]
    public Task AnAlternateStreamEntryIsRefused() =>
        ArchiveIsRefusedAsync(b => b.RawEntries.Add(("Earshot/Earshot.dll:evil", [1])), "Earshot.dll:evil");

    [TestMethod]
    public Task ADuplicateEntryIsRefused() =>
        ArchiveIsRefusedAsync(b => b.RawEntries.Add(("Earshot/Earshot.dll", [1])), "twice");

    [TestMethod]
    public async Task AFileThatIsNotAZipIsRefusedAsAnArchiveNotAnUnhandledError()
    {
        using var feed = new FeedFixture("v1.2.0");
        byte[] junk = Encoding.ASCII.GetBytes("this is not a zip file at all");
        feed.Server.MapBytes("/dl/" + feed.ZipName, junk);
        feed.Server.MapText("/dl/" + feed.ChecksumName, ReleaseZipBuilder.Sha256Hex(junk) + "  " + feed.ZipName);
        using var run = new Run(feed);

        await FailsAsync(run, UpdateFailureKind.BadArchive);
    }

    // ----- the staging folder -----

    [TestMethod]
    public async Task FoldersAnEarlierUpdateLeftBehindAreRemovedBeforeTheNextDownload()
    {
        using var feed = new FeedFixture("v1.2.0");
        using var run = new Run(feed);
        Directory.CreateDirectory(Path.Combine(run.Staging, "u12345678", "app"));
        File.WriteAllText(Path.Combine(run.Staging, "u12345678", "app", "Earshot.exe"), "old");
        File.WriteAllText(Path.Combine(run.Staging, "last-check.txt"), "kept");

        UpdateDownloadResult result = await run.DownloadAsync();

        using StagedUpdate staged = result.Staged!;
        Assert.IsFalse(Directory.Exists(Path.Combine(run.Staging, "u12345678")));
        Assert.IsTrue(File.Exists(Path.Combine(run.Staging, "last-check.txt")), "Only download folders are cleaned.");
        Assert.HasCount(1, Directory.GetDirectories(run.Staging));
    }

    [TestMethod]
    public async Task ADiscardedStagedUpdateLeavesNothing()
    {
        using var feed = new FeedFixture("v1.2.0");
        using var run = new Run(feed);
        UpdateDownloadResult result = await run.DownloadAsync();

        result.Staged!.Discard();

        UpdateAsserts.StagingIsEmpty(run.Staging);
    }

    [TestMethod]
    public void CleanStaleReportsAFolderItCouldNotRemoveInsteadOfHidingIt()
    {
        using var temp = new TempFolder();
        string root = temp.File("staging");
        string held = Path.Combine(root, "u00000001");
        Directory.CreateDirectory(held);
        var log = new CapturingLog();

        using (new FileStream(Path.Combine(held, "busy.dll"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            int removed = StagingFolders.CleanStale(root, log);

            Assert.AreEqual(0, removed, "A folder in use cannot be removed.");
            Assert.IsTrue(log.Has(LogLevel.Warn, "was not removed"), "The refusal is logged with its cause.");
        }

        Assert.AreEqual(1, StagingFolders.CleanStale(root, log));
    }
}
