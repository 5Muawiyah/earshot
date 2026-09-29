using System.Net;
using System.Text;
using Earshot.Contracts;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// The check half of the update service, against a fake release feed on a loopback port in this process.
[TestClass]
public sealed class UpdateCheckTests
{
    private static readonly ReleaseVersion Running = UpdateAsserts.Running;

    private static async Task<UpdateCheckResult> CheckAsync(FeedFixture feed, TempFolder temp, CapturingLog? log = null) =>
        await feed.Service(log ?? new CapturingLog(), temp.File("staging"), Running).CheckAsync(CancellationToken.None);

    [TestMethod]
    public async Task ANewerVersionIsFoundWithItsTwoFiles()
    {
        using var feed = new FeedFixture("v1.2.0");
        using var temp = new TempFolder();

        UpdateCheckResult result = await CheckAsync(feed, temp);

        Assert.AreEqual(UpdateCheckOutcome.Available, result.Outcome);
        Assert.AreEqual(new ReleaseVersion(1, 2, 0), result.Latest);
        ReleaseInfo release = result.Release!;
        Assert.AreEqual("Earshot-1.2.0-win-x64.zip", release.ZipName);
        Assert.AreEqual("Earshot-1.2.0-win-x64.zip.sha256", release.ChecksumName);
        Assert.AreEqual(feed.ZipAddress, release.ZipUri.ToString());
        Assert.AreEqual(feed.ChecksumAddress, release.ChecksumUri.ToString());
        Assert.AreEqual(feed.Zip.Length, release.ZipSize);
    }

    [TestMethod]
    public async Task ACheckDownloadsNothing()
    {
        using var feed = new FeedFixture("v1.2.0");
        using var temp = new TempFolder();

        await CheckAsync(feed, temp);

        CollectionAssert.AreEqual(new[] { FeedFixture.FeedPath }, feed.Server.Requests.Select(r => r.Path).ToArray(),
            "Only the feed was read. The zip and its checksum were not requested.");
        Assert.IsFalse(Directory.Exists(temp.File("staging")), "A check makes no staging folder.");
    }

    [TestMethod]
    public async Task TheSameVersionIsUpToDate()
    {
        using var feed = new FeedFixture("v1.1.0");
        using var temp = new TempFolder();

        UpdateCheckResult result = await CheckAsync(feed, temp);

        Assert.AreEqual(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.AreEqual(new ReleaseVersion(1, 1, 0), result.Latest);
        Assert.IsNull(result.Release);
    }

    [TestMethod]
    public async Task AnOlderVersionIsUpToDateEvenWithoutFiles()
    {
        using var feed = new FeedFixture("v1.0.0");
        feed.Publish(zipAsset: false, checksumAsset: false);
        using var temp = new TempFolder();

        UpdateCheckResult result = await CheckAsync(feed, temp);

        Assert.AreEqual(UpdateCheckOutcome.UpToDate, result.Outcome, "A release behind the running one needs no files to say so.");
    }

    [TestMethod]
    public async Task ATagWithTenAsTheMinorIsNewerThanNineNotOlder()
    {
        using var feed = new FeedFixture("v1.10.0");
        using var temp = new TempFolder();

        UpdateCheckResult result = await feed.Service(new CapturingLog(), temp.File("staging"), new ReleaseVersion(1, 9, 0)).CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckOutcome.Available, result.Outcome, "1.10.0 is newer than 1.9.0. A text comparison would say older.");
    }

    [TestMethod]
    public async Task MalformedJsonFailsTheCheckAndSaysSo()
    {
        using var feed = new FeedFixture();
        feed.Server.MapJson(FeedFixture.FeedPath, "{ this is not json");
        using var temp = new TempFolder();
        var log = new CapturingLog();

        UpdateCheckResult result = await CheckAsync(feed, temp, log);

        Assert.AreEqual(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.AreEqual(UpdateFailureKind.BadResponse, result.Failure!.Kind);
        Assert.IsTrue(log.Has(LogLevel.Warn, "Update check failed (BadResponse)"), "The failure is logged with its cause.");
    }

    [TestMethod]
    [DataRow("[]", DisplayName = "an array")]
    [DataRow("{}", DisplayName = "no tag")]
    [DataRow("{\"tag_name\":42}", DisplayName = "a numeric tag")]
    [DataRow("{\"tag_name\":\"v1.2.0-beta\",\"assets\":[]}", DisplayName = "a suffixed tag")]
    [DataRow("{\"tag_name\":\"latest\",\"assets\":[]}", DisplayName = "a tag that is not a version")]
    [DataRow("{\"tag_name\":\"v1.2.0\",\"prerelease\":true,\"assets\":[]}", DisplayName = "a pre-release")]
    [DataRow("{\"tag_name\":\"v1.2.0\",\"draft\":true,\"assets\":[]}", DisplayName = "a draft")]
    public async Task AnAnswerThatIsNotAReleaseFailsTheCheck(string json)
    {
        using var feed = new FeedFixture();
        feed.Server.MapJson(FeedFixture.FeedPath, json);
        using var temp = new TempFolder();

        UpdateCheckResult result = await CheckAsync(feed, temp);

        Assert.AreEqual(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.AreEqual(UpdateFailureKind.BadResponse, result.Failure!.Kind);
    }

    [TestMethod]
    public async Task ANewerReleaseWithNoZipFailsTheCheck()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Publish(zipAsset: false);
        using var temp = new TempFolder();

        UpdateCheckResult result = await CheckAsync(feed, temp);

        Assert.AreEqual(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.AreEqual(UpdateFailureKind.NoZipAsset, result.Failure!.Kind);
        Assert.AreEqual("The latest release has no Windows download.", result.Failure.Reason);
    }

    [TestMethod]
    public async Task ANewerReleaseWithNoChecksumFailsTheCheck()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Publish(checksumAsset: false);
        using var temp = new TempFolder();

        UpdateCheckResult result = await CheckAsync(feed, temp);

        Assert.AreEqual(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.AreEqual(UpdateFailureKind.NoChecksumAsset, result.Failure!.Kind);
    }

    [TestMethod]
    public async Task ARelease404IsRecordedWithItsStatus()
    {
        using var feed = new FeedFixture();
        feed.Server.MapStatus(FeedFixture.FeedPath, HttpStatusCode.NotFound);
        using var temp = new TempFolder();
        var log = new CapturingLog();

        UpdateCheckResult result = await CheckAsync(feed, temp, log);

        Assert.AreEqual(UpdateFailureKind.HttpStatus, result.Failure!.Kind);
        StringAssert.Contains(result.Failure.Detail, "404");
        Assert.IsTrue(log.Has(LogLevel.Warn, "404"), "The raw status is in the log.");
    }

    [TestMethod]
    public async Task ARelease500IsRecordedWithItsStatus()
    {
        using var feed = new FeedFixture();
        feed.Server.MapStatus(FeedFixture.FeedPath, HttpStatusCode.InternalServerError);
        using var temp = new TempFolder();
        var log = new CapturingLog();

        UpdateCheckResult result = await CheckAsync(feed, temp, log);

        Assert.AreEqual(UpdateFailureKind.HttpStatus, result.Failure!.Kind);
        StringAssert.Contains(result.Failure.Detail, "500");
        Assert.IsTrue(log.Has(LogLevel.Warn, "500"));
    }

    [TestMethod]
    public async Task AFeedThatCannotBeReachedIsRecordedWithTheExceptionType()
    {
        Uri gone;
        using (var feed = new FeedFixture())
        {
            gone = feed.Feed;
        }

        using var temp = new TempFolder();
        var log = new CapturingLog();
        var service = new UpdateService(log, Running, temp.File("staging"), gone, allowLoopbackHttp: true, new UpdateTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));

        UpdateCheckResult result = await service.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.AreEqual(UpdateFailureKind.Network, result.Failure!.Kind);
        StringAssert.Contains(result.Failure.Detail, nameof(HttpRequestException));
        Assert.IsTrue(log.Has(LogLevel.Warn, nameof(HttpRequestException)));
    }

    [TestMethod]
    public async Task AFeedAnswerCutShortIsAConnectionProblemNotADownloadThatStoppedPartWay()
    {
        using var feed = new FeedFixture();
        byte[] answer = Encoding.UTF8.GetBytes("{\"tag_name\":\"v1.2.0\",\"assets\":[]}");
        feed.Server.MapTruncated(FeedFixture.FeedPath, answer, 10);
        using var temp = new TempFolder();

        UpdateCheckResult result = await CheckAsync(feed, temp);

        Assert.AreEqual(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.AreEqual(UpdateFailureKind.Network, result.Failure!.Kind, result.Failure.Detail);
        Assert.AreEqual("Couldn't reach GitHub. Check your connection.", result.Failure.Reason);
    }

    [TestMethod]
    public async Task ARequestThatOutlastsItsBudgetFailsAsTimedOut()
    {
        using var feed = new FeedFixture();
        using var hold = new ManualResetEventSlim(false);
        feed.Server.Map(FeedFixture.FeedPath, _ => hold.Wait(TimeSpan.FromSeconds(20)));
        using var temp = new TempFolder();
        var service = new UpdateService(new CapturingLog(), Running, temp.File("staging"), feed.Feed, allowLoopbackHttp: true,
            new UpdateTimeouts(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5)));

        UpdateCheckResult result = await service.CheckAsync(CancellationToken.None);
        hold.Set();

        Assert.AreEqual(UpdateFailureKind.TimedOut, result.Failure!.Kind);
    }

    [TestMethod]
    public async Task ACancelledCheckIsCancelledNotFailed()
    {
        using var feed = new FeedFixture();
        using var temp = new TempFolder();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        UpdateCheckResult result = await feed.Service(new CapturingLog(), temp.File("staging"), Running).CheckAsync(cancel.Token);

        Assert.AreEqual(UpdateFailureKind.Cancelled, result.Failure!.Kind);
    }

    [TestMethod]
    public async Task TheFeedRequestSendsTheUserAgentGitHubRequires()
    {
        using var feed = new FeedFixture();
        using var temp = new TempFolder();

        await CheckAsync(feed, temp);

        RecordedRequest request = feed.Server.Requests.Single(r => r.Path == FeedFixture.FeedPath);
        Assert.IsFalse(string.IsNullOrWhiteSpace(request.UserAgent), "GitHub's REST API refuses a request with no User-Agent.");
        StringAssert.StartsWith(request.UserAgent, "Earshot/1.1.0");
        StringAssert.Contains(request.Accept, "application/vnd.github+json");
        Assert.AreEqual("GET", request.Method);
    }

    // ----- the address rules -----

    [TestMethod]
    public async Task ANonLoopbackHttpDownloadAddressIsRefusedBeforeAnyRequestIsMade()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Publish(zipUrl: "http://downloads.example.test/Earshot-1.2.0-win-x64.zip");
        using var temp = new TempFolder();

        UpdateCheckResult result = await CheckAsync(feed, temp);

        Assert.AreEqual(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.AreEqual(UpdateFailureKind.NotHttps, result.Failure!.Kind);
        StringAssert.Contains(result.Failure.Detail, "downloads.example.test");
    }

    [TestMethod]
    public void ANonLoopbackHttpAddressIsRefusedEvenWhenLoopbackHttpIsAllowed()
    {
        var service = new UpdateService(new CapturingLog(), Running, "unused", new Uri("http://127.0.0.1:1/"), allowLoopbackHttp: true, UpdateTimeouts.Default);

        UpdateService.UpdateException refused = Assert.ThrowsExactly<UpdateService.UpdateException>(() => service.RequireAllowed(new Uri("http://github.com/x")));
        Assert.AreEqual(UpdateFailureKind.NotHttps, refused.Kind);
        Assert.ThrowsExactly<UpdateService.UpdateException>(() => service.RequireAllowed(new Uri("http://192.0.2.10/x")));
        Assert.ThrowsExactly<UpdateService.UpdateException>(() => service.RequireAllowed(new Uri("ftp://127.0.0.1/x")));
        service.RequireAllowed(new Uri("http://127.0.0.1:5000/x"));
        service.RequireAllowed(new Uri("http://localhost:5000/x"));
        service.RequireAllowed(new Uri("https://api.github.com/x"));
    }

    [TestMethod]
    public void TheProductionServiceAcceptsHttpsOnlyNotEvenLoopback()
    {
        var service = new UpdateService(new CapturingLog(), Running, "unused");

        Assert.ThrowsExactly<UpdateService.UpdateException>(() => service.RequireAllowed(new Uri("http://127.0.0.1:5000/x")));
        Assert.ThrowsExactly<UpdateService.UpdateException>(() => service.RequireAllowed(new Uri("http://localhost/x")));
        Assert.ThrowsExactly<UpdateService.UpdateException>(() => service.RequireAllowed(new Uri("https://user:pw@api.github.com/x")));
        service.RequireAllowed(new Uri("https://objects.githubusercontent.com/x"));
    }

    [TestMethod]
    public void TheProductionFeedIsTheOneCompileTimeAddressAndNothingUserWritableReachesIt()
    {
        var service = new UpdateService(new CapturingLog(), Running, "unused");

        Assert.AreEqual("https://api.github.com/repos/5Muawiyah/earshot/releases/latest", service.Feed.ToString());

        // The only constructor a caller outside this assembly, or the tray, can reach takes no address, handler or
        // setting; the one that does is internal, for tests.
        var open = typeof(UpdateService).GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        Assert.HasCount(1, open);
        CollectionAssert.AreEqual(new[] { typeof(ILog), typeof(ReleaseVersion), typeof(string) }, open[0].GetParameters().Select(p => p.ParameterType).ToArray());
    }

    [TestMethod]
    public void NothingInTheUpdateSourcesReadsTheEnvironmentTheRegistryOrTheSettings()
    {
        string folder = Path.Combine(TestRepository.Root, "src", "Earshot", "Update");
        foreach (string file in Directory.GetFiles(folder, "*.cs"))
        {
            string text = File.ReadAllText(file);
            foreach (string forbidden in new[] { "GetEnvironmentVariable", "Microsoft.Win32.Registry", "RegistryKey", "ISettingsStore", "EarshotSettings ", "AppSettings", "ConfigurationManager" })
            {
                // HandoverIdentity is built from the pinned device by the caller; only the check for it names settings.
                if (Path.GetFileName(file) == "UpdateHandover.cs" && forbidden == "EarshotSettings ")
                {
                    continue;
                }

                Assert.IsFalse(text.Contains(forbidden, StringComparison.Ordinal), Path.GetFileName(file) + " mentions " + forbidden + ".");
            }
        }
    }

    [TestMethod]
    public async Task AFeedRedirectToAnAllowedAddressIsFollowed()
    {
        using var feed = new FeedFixture("v1.2.0");
        string real = "/moved" + FeedFixture.FeedPath;
        feed.Server.MapRedirect(FeedFixture.FeedPath, real);
        feed.Server.MapJson(real, "{\"tag_name\":\"v1.1.0\",\"assets\":[]}");
        using var temp = new TempFolder();

        UpdateCheckResult result = await CheckAsync(feed, temp);

        Assert.AreEqual(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.AreEqual(1, feed.Server.Count(real));
    }

    [TestMethod]
    public async Task AFeedRedirectToANonHttpsAddressIsRefusedAndNotFollowed()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Server.MapRedirect(FeedFixture.FeedPath, "http://downloads.example.test/release.json");
        using var temp = new TempFolder();

        UpdateCheckResult result = await CheckAsync(feed, temp);

        Assert.AreEqual(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.AreEqual(UpdateFailureKind.NotHttps, result.Failure!.Kind);
    }

    [TestMethod]
    public async Task ARedirectLoopStops()
    {
        using var feed = new FeedFixture("v1.2.0");
        feed.Server.MapRedirect(FeedFixture.FeedPath, FeedFixture.FeedPath);
        using var temp = new TempFolder();

        UpdateCheckResult result = await CheckAsync(feed, temp);

        Assert.AreEqual(UpdateFailureKind.BadResponse, result.Failure!.Kind);
        Assert.AreEqual(UpdateService.MaxRedirects + 1, feed.Server.Count(FeedFixture.FeedPath));
    }
}

// The repository root, found from the test assembly's folder.
internal static class TestRepository
{
    public static string Root
    {
        get
        {
            string? folder = AppContext.BaseDirectory;
            while (folder is not null && !File.Exists(Path.Combine(folder, "Earshot.slnx")))
            {
                folder = Path.GetDirectoryName(folder);
            }

            return folder ?? throw new AssertFailedException("The repository root was not found above " + AppContext.BaseDirectory + ".");
        }
    }
}
