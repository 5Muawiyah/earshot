using Earshot.Contracts;
using Earshot.Tests.Phase4;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// The update flow's states and what each click is allowed to do, against a fake source and a fake launcher. No
// network, no elevation: the launcher only records what it was asked to start.
[TestClass]
public sealed class UpdateControllerTests
{
    private static readonly ReleaseVersion Installed = new(1, 1, 0);

    private sealed class Rig : IDisposable
    {
        private readonly TempFolder _temp = new();

        public const string InstalledExe = @"C:\Program Files\Earshot\Earshot.exe";

        public const int TrayPid = 4321;

        public Rig(bool pinned = true, string? unavailable = null, bool installedCopy = true)
        {
            Controller = new UpdateController(
                Source, Launcher,
                () => pinned ? new HandoverIdentity(TestUsers.Sid, "0A1B2C3D4E8C", new Guid("5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")) : null,
                () => installedCopy ? new HandoverTarget(InstalledExe, TrayPid) : null,
                () => unavailable, Installed, Log);
            Controller.Changed += (_, _) => Stages.Add(Controller.View);
        }

        public FakeUpdateSource Source { get; } = new();

        public FakeUpdateLauncher Launcher { get; } = new();

        public CapturingLog Log { get; } = new();

        public UpdateController Controller { get; }

        public List<UpdateViewModel> Stages { get; } = new();

        public int HandedOverRaised { get; private set; }

        public string StagingRoot => _temp.File("staging");

        public void WatchHandover() => Controller.HandedOver += (_, _) => HandedOverRaised++;

        public UpdateStage[] Seen => Stages.Select(s => s.Stage).ToArray();

        // A staging folder holding a zip, the way a real download leaves it.
        public StagedUpdate Stage(string version = "1.2.0")
        {
            string work = Path.Combine(StagingRoot, "u" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, UpdateService.ZipFileName);
            File.WriteAllText(zip, "zip");
            return new StagedUpdate(ReleaseVersion.TryParse(version, out ReleaseVersion v) ? v : default, work, zip, new string('A', 64), Log);
        }

        public void Dispose() => _temp.Dispose();
    }

    private static void FoundNewer(Rig rig, string version = "1.2.0") =>
        rig.Source.OnCheck = _ => Task.FromResult(UpdateCheckResult.Available(FakeUpdateSource.Release(version)));

    // ----- checking -----

    [TestMethod]
    public void BeforeAnyCheckTheControllerIsIdleAndOffersCheck()
    {
        using var rig = new Rig();

        Assert.AreEqual(UpdateStage.Idle, rig.Controller.Stage);
        Assert.AreEqual(UpdateButtonRole.Check, rig.Controller.View.Buttons.Single().Role);
        Assert.AreEqual(0, rig.Source.CheckCalls);
        Assert.IsFalse(rig.Controller.IsBusy);
    }

    [TestMethod]
    public async Task ACheckThatFindsNothingNewMovesFromCheckingToUpToDate()
    {
        using var rig = new Rig();

        await rig.Controller.CheckAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { UpdateStage.Checking, UpdateStage.UpToDate }, rig.Seen);
        Assert.AreEqual("You're up to date", rig.Controller.View.Status);
        Assert.AreEqual("Version 1.1.0", rig.Controller.View.Sub);
    }

    [TestMethod]
    public async Task ACheckThatFindsANewerVersionOffersItAndDownloadsNothing()
    {
        using var rig = new Rig();
        FoundNewer(rig);

        await rig.Controller.CheckAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { UpdateStage.Checking, UpdateStage.Available }, rig.Seen);
        Assert.AreEqual("Version 1.2.0 is available", rig.Controller.View.Status);
        Assert.AreEqual("Version 1.1.0 installed", rig.Controller.View.Sub);
        Assert.AreEqual(UpdateButtonRole.Update, rig.Controller.View.Buttons.Single().Role);
        Assert.AreEqual(0, rig.Source.DownloadCalls, "Nothing is downloaded until the owner clicks Update.");
        Assert.IsEmpty(rig.Launcher.Launches);
    }

    [TestMethod]
    public async Task AFailedCheckShowsItsReasonAndTryAgainChecksAgain()
    {
        using var rig = new Rig();
        rig.Source.OnCheck = _ => Task.FromResult(UpdateCheckResult.Failed(new UpdateFailure(UpdateFailureKind.Network, "Couldn't reach GitHub. Check your connection.", "HttpRequestException")));

        await rig.Controller.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateStage.CheckFailed, rig.Controller.Stage);
        Assert.AreEqual("Couldn't check for updates", rig.Controller.View.Status);
        Assert.AreEqual("Couldn't reach GitHub. Check your connection.", rig.Controller.View.Reason);

        rig.Source.OnCheck = _ => Task.FromResult(UpdateCheckResult.UpToDate(Installed));
        await rig.Controller.TryAgainAsync(CancellationToken.None);

        Assert.AreEqual(UpdateStage.UpToDate, rig.Controller.Stage);
        Assert.AreEqual(2, rig.Source.CheckCalls);
    }

    [TestMethod]
    public async Task AnExceptionTheSourceDidNotHandleEndsCheckingAsAFailureAndIsLogged()
    {
        using var rig = new Rig();
        rig.Source.OnCheck = _ => throw new InvalidOperationException("boom");

        await rig.Controller.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateStage.CheckFailed, rig.Controller.Stage, "The view does not stay on Checking for something that has ended.");
        Assert.IsTrue(rig.Log.Has(LogLevel.Error, "InvalidOperationException"));
    }

    [TestMethod]
    public async Task ACancelledCheckReturnsToIdleWithoutAFailure()
    {
        using var rig = new Rig();
        using var cancel = new CancellationTokenSource();
        rig.Source.OnCheck = async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return UpdateCheckResult.UpToDate(Installed);
        };

        Task check = rig.Controller.CheckAsync(cancel.Token);
        await cancel.CancelAsync();
        await check;

        Assert.AreEqual(UpdateStage.Idle, rig.Controller.Stage);
    }

    [TestMethod]
    public async Task ASecondCheckWhileOneIsRunningIsIgnored()
    {
        using var rig = new Rig();
        var release = new TaskCompletionSource<UpdateCheckResult>();
        rig.Source.OnCheck = _ => release.Task;

        Task first = rig.Controller.CheckAsync(CancellationToken.None);
        await rig.Controller.CheckAsync(CancellationToken.None);
        release.SetResult(UpdateCheckResult.UpToDate(Installed));
        await first;

        Assert.AreEqual(1, rig.Source.CheckCalls);
    }

    // ----- updating -----

    [TestMethod]
    public async Task UpdateDoesNothingBeforeACheckHasFoundAnUpdate()
    {
        using var rig = new Rig();

        await rig.Controller.UpdateAsync(CancellationToken.None);

        Assert.AreEqual(0, rig.Source.DownloadCalls);
        Assert.IsEmpty(rig.Launcher.Launches);
        Assert.AreEqual(UpdateStage.Idle, rig.Controller.Stage);
    }

    [TestMethod]
    public async Task UpdateDownloadsThenHandsTheVerifiedZipToTheInstalledProgramWithTheUpdateVerb()
    {
        using var rig = new Rig();
        rig.WatchHandover();
        FoundNewer(rig);
        StagedUpdate staged = rig.Stage();
        rig.Source.OnDownload = (release, progress, _) =>
        {
            Assert.AreEqual(new ReleaseVersion(1, 2, 0), release.Version);
            progress!.Report(new UpdateProgress(50, 100));
            progress.Report(new UpdateProgress(100, 100));
            return Task.FromResult(UpdateDownloadResult.Success(staged));
        };
        await rig.Controller.CheckAsync(CancellationToken.None);

        await rig.Controller.UpdateAsync(CancellationToken.None);

        (string exe, string[] arguments, string workingDirectory) = rig.Launcher.Launches.Single();
        Assert.AreEqual(Rig.InstalledExe, exe, "The installed, administrator-owned Earshot.exe is what starts, never a file in the staging folder.");
        CollectionAssert.AreEqual(
            new[] { "update", staged.ZipPath, staged.ZipSha256, "4321", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13" }, arguments);
        Assert.AreEqual(Environment.SystemDirectory, workingDirectory, "Not a folder the install replaces.");
        Assert.AreEqual(1, rig.HandedOverRaised, "The tray is told once, so it can close.");
        CollectionAssert.AreEqual(
            new[] { UpdateStage.Checking, UpdateStage.Available, UpdateStage.Downloading, UpdateStage.HandingOver },
            rig.Seen.Where((stage, i) => i == 0 || rig.Seen[i - 1] != stage).ToArray());
        Assert.AreEqual(50, rig.Stages.First(s => s.ProgressPercent == 50).ProgressPercent);
        Assert.AreEqual("Approve the Windows prompt", rig.Controller.View.Status);
        Assert.AreEqual("Installing 1.2.0", rig.Controller.View.Sub);
        Assert.IsTrue(Directory.Exists(staged.WorkFolder), "A handed-over update is not deleted: the elevated program reads the zip from it.");
    }

    [TestMethod]
    public async Task AProgramThatIsNotTheInstalledCopyDoesNotOfferUpdateAndSaysToSetUpFirst()
    {
        using var rig = new Rig(installedCopy: false);
        FoundNewer(rig);

        await rig.Controller.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateStage.Available, rig.Controller.Stage);
        Assert.IsEmpty(rig.Controller.View.Buttons, "No Update button is offered.");
        Assert.AreEqual("Set up Earshot first, then update.", rig.Controller.View.Notice);
        Assert.AreEqual("Set up Earshot first, then update.", rig.Controller.View.CardText);

        await rig.Controller.UpdateAsync(CancellationToken.None);

        Assert.AreEqual(0, rig.Source.DownloadCalls, "Nothing is downloaded for an update that cannot be handed over.");
        Assert.IsEmpty(rig.Launcher.Launches);
        Assert.AreEqual("Set up Earshot first, then update.", rig.Controller.View.Notice);
    }

    [TestMethod]
    public async Task ADownloadInProgressShowsItsProgressAndCanBeCancelledBackToAvailable()
    {
        using var rig = new Rig();
        rig.WatchHandover();
        FoundNewer(rig);
        var started = new TaskCompletionSource();
        CancellationToken seen = default;
        rig.Source.OnDownload = async (_, progress, ct) =>
        {
            seen = ct;
            progress!.Report(new UpdateProgress(30, 100));
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                return UpdateDownloadResult.Failed(new UpdateFailure(UpdateFailureKind.Cancelled, "Cancelled.", "The download was cancelled."));
            }

            throw new AssertFailedException("unreachable");
        };
        await rig.Controller.CheckAsync(CancellationToken.None);

        Task update = rig.Controller.UpdateAsync(CancellationToken.None);
        await started.Task;
        Assert.AreEqual(UpdateStage.Downloading, rig.Controller.Stage);
        Assert.AreEqual(30, rig.Controller.View.ProgressPercent);
        Assert.IsTrue(rig.Controller.IsBusy);
        rig.Controller.Cancel();
        await update;

        Assert.IsTrue(seen.IsCancellationRequested);
        Assert.AreEqual(UpdateStage.Available, rig.Controller.Stage, "Cancel goes back to the update on offer.");
        Assert.AreEqual(0, rig.HandedOverRaised);
        Assert.IsEmpty(rig.Launcher.Launches);
        Assert.IsNull(rig.Controller.View.Reason);
    }

    [TestMethod]
    public async Task AFailedDownloadShowsItsReasonAndTryAgainDownloadsAgain()
    {
        using var rig = new Rig();
        FoundNewer(rig);
        rig.Source.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Failed(
            new UpdateFailure(UpdateFailureKind.ChecksumMismatch, "The download did not match its checksum, so Earshot removed it.", "SHA-256 differs")));
        await rig.Controller.CheckAsync(CancellationToken.None);

        await rig.Controller.UpdateAsync(CancellationToken.None);

        Assert.AreEqual(UpdateStage.DownloadFailed, rig.Controller.Stage);
        Assert.AreEqual("Couldn't download the update", rig.Controller.View.Status);
        Assert.AreEqual("Version 1.2.0", rig.Controller.View.Sub);
        Assert.AreEqual("The download did not match its checksum, so Earshot removed it.", rig.Controller.View.Reason);
        Assert.IsEmpty(rig.Launcher.Launches, "A failed download is never handed over.");

        StagedUpdate staged = rig.Stage();
        rig.Source.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged));
        await rig.Controller.TryAgainAsync(CancellationToken.None);

        Assert.AreEqual(2, rig.Source.DownloadCalls);
        Assert.AreEqual(1, rig.Launcher.Launches.Count);
    }

    [TestMethod]
    public async Task ADeclinedWindowsPromptChangesNothingAndDeletesTheStagedFiles()
    {
        using var rig = new Rig();
        rig.WatchHandover();
        FoundNewer(rig);
        StagedUpdate staged = rig.Stage();
        rig.Source.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged));
        rig.Launcher.OnLaunch = _ => new LaunchResult(LaunchOutcome.Declined, 1223, "Win32 error 1223: cancelled", null);
        await rig.Controller.CheckAsync(CancellationToken.None);

        await rig.Controller.UpdateAsync(CancellationToken.None);

        Assert.AreEqual(UpdateStage.Available, rig.Controller.Stage);
        Assert.AreEqual(UpdateCopy.PromptDeclinedNotice, rig.Controller.View.Notice);
        Assert.AreEqual(0, rig.HandedOverRaised, "Earshot stays open when the prompt was declined.");
        Assert.IsFalse(Directory.Exists(staged.WorkFolder), "The unused download is deleted.");
    }

    [TestMethod]
    public async Task AProgramWindowsWouldNotStartIsReportedWithItsCodeAndTheStagedFilesAreDeleted()
    {
        using var rig = new Rig();
        rig.WatchHandover();
        FoundNewer(rig);
        StagedUpdate staged = rig.Stage();
        rig.Source.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged));
        rig.Launcher.OnLaunch = _ => new LaunchResult(LaunchOutcome.Failed, 5, "Win32 error 5: Access is denied", null);
        await rig.Controller.CheckAsync(CancellationToken.None);

        await rig.Controller.UpdateAsync(CancellationToken.None);

        Assert.AreEqual(UpdateStage.HandoverFailed, rig.Controller.Stage);
        Assert.AreEqual("Couldn't start the update", rig.Controller.View.Status);
        StringAssert.Contains(rig.Controller.View.Reason, "error 5");
        Assert.AreEqual(0, rig.HandedOverRaised);
        Assert.IsFalse(Directory.Exists(staged.WorkFolder));
        Assert.IsTrue(rig.Log.Has(LogLevel.Warn, "Access is denied"), "The raw cause is logged.");
    }

    [TestMethod]
    public async Task ALauncherThatThrowsEndsAsAFailedHandoverNotAViewStuckOnThePrompt()
    {
        using var rig = new Rig();
        rig.WatchHandover();
        FoundNewer(rig);
        StagedUpdate staged = rig.Stage();
        rig.Source.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged));
        rig.Launcher.OnLaunch = _ => throw new InvalidCastException("boom");
        await rig.Controller.CheckAsync(CancellationToken.None);

        await rig.Controller.UpdateAsync(CancellationToken.None);

        Assert.AreEqual(UpdateStage.HandoverFailed, rig.Controller.Stage);
        Assert.AreEqual(0, rig.HandedOverRaised);
        Assert.IsFalse(Directory.Exists(staged.WorkFolder), "The unused download is deleted.");
        Assert.IsTrue(rig.Log.Has(LogLevel.Error, "InvalidCastException"), "The exception type is in the log.");
    }

    [TestMethod]
    public async Task CancelWhenNothingIsDownloadingIsHarmless()
    {
        using var rig = new Rig();
        FoundNewer(rig);
        rig.Source.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Failed(new UpdateFailure(UpdateFailureKind.Network, "x", "x")));
        rig.Controller.Cancel();
        await rig.Controller.CheckAsync(CancellationToken.None);
        await rig.Controller.UpdateAsync(CancellationToken.None);

        rig.Controller.Cancel();

        Assert.AreEqual(UpdateStage.DownloadFailed, rig.Controller.Stage, "A late Cancel changes nothing and does not throw.");
    }

    [TestMethod]
    public async Task AnUpdateThatCannotRunInThisProcessIsRefusedBeforeAnyDownload()
    {
        using var rig = new Rig(unavailable: "Safe mode: no device actions");
        FoundNewer(rig);
        await rig.Controller.CheckAsync(CancellationToken.None);

        await rig.Controller.UpdateAsync(CancellationToken.None);

        Assert.AreEqual(0, rig.Source.DownloadCalls);
        Assert.AreEqual(UpdateStage.Available, rig.Controller.Stage);
        Assert.AreEqual("Safe mode: no device actions", rig.Controller.View.Notice);
        Assert.IsEmpty(rig.Launcher.Launches);
    }

    [TestMethod]
    public async Task AnUpdateWithNoPinnedDeviceIsRefusedBeforeAnyDownloadBecauseSetupNeedsOne()
    {
        using var rig = new Rig(pinned: false);
        FoundNewer(rig);
        await rig.Controller.CheckAsync(CancellationToken.None);

        await rig.Controller.UpdateAsync(CancellationToken.None);

        Assert.AreEqual(0, rig.Source.DownloadCalls);
        Assert.AreEqual(UpdateCopy.NotPinnedNotice, rig.Controller.View.Notice);
        Assert.AreEqual("Choose your AirPods first, then update.", rig.Controller.View.Notice);
    }

    [TestMethod]
    public async Task ASecondUpdateClickWhileDownloadingStartsNoSecondDownload()
    {
        using var rig = new Rig();
        FoundNewer(rig);
        var gate = new TaskCompletionSource<UpdateDownloadResult>();
        rig.Source.OnDownload = (_, _, _) => gate.Task;
        await rig.Controller.CheckAsync(CancellationToken.None);

        Task first = rig.Controller.UpdateAsync(CancellationToken.None);
        await rig.Controller.UpdateAsync(CancellationToken.None);
        gate.SetResult(UpdateDownloadResult.Failed(new UpdateFailure(UpdateFailureKind.Network, "x", "x")));
        await first;

        Assert.AreEqual(1, rig.Source.DownloadCalls);
    }

    [TestMethod]
    public async Task ACheckWhileDownloadingIsIgnored()
    {
        using var rig = new Rig();
        FoundNewer(rig);
        var gate = new TaskCompletionSource<UpdateDownloadResult>();
        rig.Source.OnDownload = (_, _, _) => gate.Task;
        await rig.Controller.CheckAsync(CancellationToken.None);
        Task update = rig.Controller.UpdateAsync(CancellationToken.None);

        await rig.Controller.CheckAsync(CancellationToken.None);

        Assert.AreEqual(UpdateStage.Downloading, rig.Controller.Stage);
        Assert.AreEqual(1, rig.Source.CheckCalls);
        gate.SetResult(UpdateDownloadResult.Failed(new UpdateFailure(UpdateFailureKind.Network, "x", "x")));
        await update;
    }

    [TestMethod]
    public async Task TheDownloadIsCancelledWhenTheProgramCloses()
    {
        using var rig = new Rig();
        FoundNewer(rig);
        using var closing = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        rig.Source.OnDownload = async (_, _, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                return UpdateDownloadResult.Failed(new UpdateFailure(UpdateFailureKind.Cancelled, "Cancelled.", "cancelled"));
            }

            throw new AssertFailedException("unreachable");
        };
        await rig.Controller.CheckAsync(CancellationToken.None);

        Task update = rig.Controller.UpdateAsync(closing.Token);
        await started.Task;
        await closing.CancelAsync();
        await update;

        Assert.AreEqual(UpdateStage.Available, rig.Controller.Stage);
    }
}
