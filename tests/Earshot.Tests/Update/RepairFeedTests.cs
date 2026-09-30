using System.Net;
using Earshot.Contracts;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// The release of one version, which a repair fetches when the installed files cannot be trusted: read from the feed's
// own tag route, downloaded and checked against its checksum file exactly as an update's is. The feed is a fake server on
// a loopback port in this process, so the real service runs here and the network is never reached.
[TestClass]
public sealed class RepairFeedTests
{
    // The running program is newer than the release that is asked for: a repair fetches the version that is installed,
    // which is the running one or an older one, never only a newer one.
    private static readonly ReleaseVersion Running = new(1, 3, 0);

    private static Task<UpdateCheckResult> FindAsync(FeedFixture feed, TempFolder temp, ReleaseVersion version, CapturingLog? log = null) =>
        feed.Service(log ?? new CapturingLog(), temp.File("staging"), Running).FindReleaseAsync(version, CancellationToken.None);

    [TestMethod]
    public async Task TheReleaseOfAnOlderVersionIsFoundFromItsOwnTagRouteWithItsTwoFiles()
    {
        using var feed = new FeedFixture("v1.2.0");
        using var temp = new TempFolder();

        UpdateCheckResult result = await FindAsync(feed, temp, new ReleaseVersion(1, 2, 0));

        Assert.AreEqual(UpdateCheckOutcome.Available, result.Outcome, result.Failure?.Detail);
        ReleaseInfo release = result.Release!;
        Assert.AreEqual(new ReleaseVersion(1, 2, 0), release.Version);
        Assert.AreEqual("Earshot-1.2.0-win-x64.zip", release.ZipName);
        Assert.AreEqual(feed.ZipAddress, release.ZipUri.ToString());
        Assert.AreEqual(feed.ChecksumAddress, release.ChecksumUri.ToString());
        Assert.AreEqual("/repos/5Muawiyah/earshot/releases/tags/v1.2.0", feed.Server.Requests.Single().Path,
            "Only the tag's own release was read: not the latest, and not the zip.");
        Assert.IsFalse(Directory.Exists(temp.File("staging")), "Finding a release makes no staging folder and downloads nothing.");
    }

    [TestMethod]
    public void TheTagAddressIsBuiltFromTheFeedAndAPartOfTheTagsName()
    {
        var service = new UpdateService(new CapturingLog(), Running, @"C:\staging");

        Assert.AreEqual("https://api.github.com/repos/5Muawiyah/earshot/releases/tags/v1.2.0", service.TagAddress(new ReleaseVersion(1, 2, 0)).ToString());
        Assert.AreEqual("https://api.github.com/repos/5Muawiyah/earshot/releases/tags/v1.2.0.1", service.TagAddress(new ReleaseVersion(1, 2, 0, 1)).ToString());
    }

    [TestMethod]
    public async Task AFeedThatAnswersWithAnotherVersionThanTheOneAskedForIsRefused()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Publish(tagOverride: "v1.9.0");
        using var temp = new TempFolder();

        UpdateCheckResult result = await FindAsync(feed, temp, new ReleaseVersion(1, 2, 0));

        Assert.AreEqual(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.AreEqual(UpdateFailureKind.BadResponse, result.Failure!.Kind);
        StringAssert.Contains(result.Failure.Detail, "v1.9.0");
    }

    [TestMethod]
    public async Task AVersionThatWasNeverPublishedIsAPlainFailureWithTheStatus()
    {
        using var feed = new FeedFixture("v1.2.0");
        using var temp = new TempFolder();

        UpdateCheckResult result = await FindAsync(feed, temp, new ReleaseVersion(1, 1, 7));

        Assert.AreEqual(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.AreEqual(UpdateFailureKind.HttpStatus, result.Failure!.Kind);
        Assert.AreEqual("GitHub has no such release or file (404).", result.Failure.Reason);
    }

    [TestMethod]
    public async Task ADraftOrAReleaseWithNoChecksumIsRefusedAsForAnUpdate()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Publish(extra: "\"draft\":true");
        using var temp = new TempFolder();
        UpdateCheckResult draft = await FindAsync(feed, temp, new ReleaseVersion(1, 2, 0));
        Assert.AreEqual(UpdateFailureKind.BadResponse, draft.Failure!.Kind);

        using var bare = new FeedFixture("v1.2.0");
        bare.Publish(checksumAsset: false);
        UpdateCheckResult noChecksum = await FindAsync(bare, temp, new ReleaseVersion(1, 2, 0));
        Assert.AreEqual(UpdateFailureKind.NoChecksumAsset, noChecksum.Failure!.Kind, "The same rules as an update: no checksum, no install.");
    }

    [TestMethod]
    public async Task AnAddressThatIsNotHttpsIsRefusedAtTheTagRouteToo()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Publish(zipUrl: "http://example.test/a.zip");
        using var temp = new TempFolder();

        UpdateCheckResult result = await FindAsync(feed, temp, new ReleaseVersion(1, 2, 0));

        Assert.AreEqual(UpdateFailureKind.NotHttps, result.Failure!.Kind);
    }

    // The release found for a repair is then downloaded and verified by the very code an update uses: the checksum file
    // must match, and a zip that does not match is deleted.
    [TestMethod]
    public async Task TheFoundReleaseIsDownloadedAndVerifiedLikeAnUpdateAndACorruptZipIsRefused()
    {
        using var feed = new FeedFixture("v1.2.0");
        using var temp = new TempFolder();
        var log = new CapturingLog();
        UpdateService service = feed.Service(log, temp.File("staging"), Running);
        UpdateCheckResult found = await service.FindReleaseAsync(new ReleaseVersion(1, 2, 0), CancellationToken.None);

        UpdateDownloadResult good = await service.DownloadAsync(found.Release!, null, CancellationToken.None);

        Assert.IsNotNull(good.Staged, good.Failure?.Detail);
        Assert.AreEqual(feed.ZipHash, good.Staged.ZipSha256);
        Assert.IsTrue(File.Exists(good.Staged.ZipPath));
        good.Staged.Discard();

        feed.Server.MapText("/dl/" + feed.ChecksumName, new string('0', 64) + "  " + feed.ZipName + "\n");
        UpdateDownloadResult bad = await service.DownloadAsync(found.Release!, null, CancellationToken.None);

        Assert.IsNull(bad.Staged);
        Assert.AreEqual(UpdateFailureKind.ChecksumMismatch, bad.Failure!.Kind);
        UpdateAsserts.StagingIsEmpty(temp.File("staging"));
    }

    [TestMethod]
    public async Task AnUpdateCheckStillReadsTheLatestRouteAndNotTheTag()
    {
        using var feed = new FeedFixture("v1.4.0");
        using var temp = new TempFolder();

        UpdateCheckResult result = await feed.Service(new CapturingLog(), temp.File("staging"), Running).CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckOutcome.Available, result.Outcome);
        CollectionAssert.AreEqual(new[] { FeedFixture.FeedPath }, feed.Server.Requests.Select(r => r.Path).ToArray());
    }

    [TestMethod]
    public async Task AServerErrorOnTheTagRouteIsAFailureWithThePlainReason()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Server.MapStatus(feed.TagPath, HttpStatusCode.InternalServerError);
        using var temp = new TempFolder();

        UpdateCheckResult result = await FindAsync(feed, temp, new ReleaseVersion(1, 2, 0));

        Assert.AreEqual(UpdateFailureKind.HttpStatus, result.Failure!.Kind);
        Assert.AreEqual("GitHub answered with an error (500).", result.Failure.Reason);
    }
}
