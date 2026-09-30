using Earshot.Contracts;
using Earshot.Tests.Phase4;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// The repair that fetches the installed version's release, as the update controller runs it: find the release of that
// version, download and verify it, hand the verified zip to the installed program's update verb. Against a fake source and
// a fake launcher, so nothing reaches GitHub and nothing elevates.
[TestClass]
public sealed class RepairControllerTests
{
    private static readonly ReleaseVersion Version = new(1, 2, 1);

    private sealed class Rig : IDisposable
    {
        private readonly TempFolder _temp = new();
        private bool _pinned = true;
        private bool _installed = true;

        public const string InstalledExe = @"C:\Program Files\Earshot\Earshot.exe";

        public const int TrayPid = 4321;

        public Rig(string? unavailable = null)
        {
            Controller = new UpdateController(
                Source, Launcher,
                () => _pinned ? new HandoverIdentity(TestUsers.Sid, "0A1B2C3D4E8C", new Guid("5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")) : null,
                () => _installed ? new HandoverTarget(InstalledExe, TrayPid) : null,
                () => unavailable, new ReleaseVersion(1, 2, 1), Log, () => _installed ? InstallState.Usable : InstallState.Nothing);
            Controller.Changed += (_, _) => Stages.Add(Controller.View);
            Source.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(Stage()));
        }

        public FakeUpdateSource Source { get; } = new();

        public FakeUpdateLauncher Launcher { get; } = new();

        public CapturingLog Log { get; } = new();

        public UpdateController Controller { get; }

        public List<UpdateViewModel> Stages { get; } = new();

        public int HandedOver { get; private set; }

        public StagedUpdate? LastStaged { get; private set; }

        public void WatchHandover() => Controller.HandedOver += (_, _) => HandedOver++;

        public void Unpin() => _pinned = false;

        public void RemoveInstall() => _installed = false;

        public StagedUpdate Stage()
        {
            string work = Path.Combine(_temp.File("staging"), "u" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, UpdateService.ZipFileName);
            File.WriteAllText(zip, "zip");
            LastStaged = new StagedUpdate(Version, work, zip, new string('A', 64), Log);
            return LastStaged;
        }

        public void Dispose() => _temp.Dispose();
    }

    [TestMethod]
    public async Task ARepairFindsTheInstalledVersionsReleaseDownloadsItAndHandsItToTheInstalledProgramWithThisProcessId()
    {
        using var rig = new Rig();
        rig.WatchHandover();

        await rig.Controller.RepairAsync(Version, CancellationToken.None);

        Assert.AreEqual(Version, rig.Source.FindCalls.Single(), "The release of the installed version, not the latest.");
        Assert.AreEqual(0, rig.Source.CheckCalls, "No check for the newest version is made.");
        Assert.AreEqual(1, rig.Source.DownloadCalls);
        (string exe, string[] arguments, string workingDirectory) = rig.Launcher.Launches.Single();
        Assert.AreEqual(Rig.InstalledExe, exe, "The installed program's own update verb, never a file in the staging folder.");
        Assert.AreEqual("update", arguments[0]);
        Assert.AreEqual(rig.LastStaged!.ZipPath, arguments[1]);
        Assert.AreEqual(rig.LastStaged.ZipSha256, arguments[2], "The hash the download matched is what goes to the elevated run.");
        Assert.AreEqual("4321", arguments[3]);
        Assert.AreEqual(Environment.SystemDirectory, workingDirectory);
        Assert.AreEqual(1, rig.HandedOver, "The tray is told once, so it can close.");
        Assert.AreEqual(UpdateStage.HandingOver, rig.Controller.Stage);
        Assert.AreEqual("Repair", rig.Controller.View.Title);
        Assert.AreEqual("Approve the Windows prompt", rig.Controller.View.Status);
        Assert.AreEqual("Repairing 1.2.1", rig.Controller.View.Sub);
        Assert.IsTrue(Directory.Exists(rig.LastStaged.WorkFolder), "The elevated run reads the zip from it.");
    }

    [TestMethod]
    public void NothingIsFoundOrDownloadedUntilRepairIsAskedFor()
    {
        using var rig = new Rig();

        Assert.AreEqual(0, rig.Source.FindCalls.Count);
        Assert.AreEqual(0, rig.Source.DownloadCalls);
        Assert.IsEmpty(rig.Launcher.Launches);
    }

    [TestMethod]
    public async Task AReleaseThatCannotBeFoundIsAFailureInTheRepairsWordsAndTryAgainLooksAgain()
    {
        using var rig = new Rig();
        rig.Source.OnFind = (_, _) => Task.FromResult(UpdateCheckResult.Failed(new UpdateFailure(UpdateFailureKind.HttpStatus, "GitHub has no such release or file (404).", "404")));

        await rig.Controller.RepairAsync(Version, CancellationToken.None);

        Assert.AreEqual(UpdateStage.DownloadFailed, rig.Controller.Stage);
        Assert.AreEqual("Couldn't download the repair", rig.Controller.View.Status);
        Assert.AreEqual("GitHub has no such release or file (404).", rig.Controller.View.Reason);
        Assert.AreEqual(0, rig.Source.DownloadCalls);
        Assert.IsEmpty(rig.Launcher.Launches);

        rig.Source.OnFind = (version, _) => Task.FromResult(UpdateCheckResult.Available(FakeUpdateSource.Release(version.ToString())));
        await rig.Controller.TryAgainAsync(CancellationToken.None);

        Assert.AreEqual(2, rig.Source.FindCalls.Count, "Try again finds the release again: a repair has no release kept from an earlier check.");
        Assert.AreEqual(UpdateStage.HandingOver, rig.Controller.Stage);
    }

    [TestMethod]
    public async Task ADownloadThatDoesNotVerifyIsNeverHandedOver()
    {
        using var rig = new Rig();
        rig.Source.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Failed(
            new UpdateFailure(UpdateFailureKind.ChecksumMismatch, "The download did not match its checksum, so Earshot removed it.", "mismatch")));

        await rig.Controller.RepairAsync(Version, CancellationToken.None);

        Assert.AreEqual(UpdateStage.DownloadFailed, rig.Controller.Stage);
        Assert.AreEqual("The download did not match its checksum, so Earshot removed it.", rig.Controller.View.Reason);
        Assert.IsEmpty(rig.Launcher.Launches, "Nothing unverified reaches the elevated run.");
    }

    [TestMethod]
    public async Task ADeclinedPromptIsASaidFailureOfTheRepairNotAnUpdateOfferedAndTheDownloadIsDeleted()
    {
        using var rig = new Rig();
        rig.Launcher.OnLaunch = _ => new LaunchResult(LaunchOutcome.Declined, 1223, "cancelled", null);

        await rig.Controller.RepairAsync(Version, CancellationToken.None);

        Assert.AreEqual(UpdateStage.HandoverFailed, rig.Controller.Stage);
        Assert.AreEqual("Couldn't start the repair", rig.Controller.View.Status);
        Assert.AreEqual("The Windows prompt was declined, so nothing was changed.", rig.Controller.View.Reason);
        Assert.IsFalse(rig.Controller.View.Buttons.Any(b => b.Role == UpdateButtonRole.Update), "A declined repair does not turn into an update on offer.");
        Assert.IsFalse(Directory.Exists(rig.LastStaged!.WorkFolder));
    }

    [TestMethod]
    public async Task ARepairIsRefusedBeforeAnythingIsFoundInSafeModeWithoutAPinnedDeviceAndWithNoInstall()
    {
        using var safe = new Rig(unavailable: "Safe mode: no device actions");
        await safe.Controller.RepairAsync(Version, CancellationToken.None);
        Assert.AreEqual("Safe mode: no device actions", safe.Controller.View.Reason);
        Assert.AreEqual(0, safe.Source.FindCalls.Count);

        using var unpinned = new Rig();
        unpinned.Unpin();
        await unpinned.Controller.RepairAsync(Version, CancellationToken.None);
        Assert.AreEqual("Choose your AirPods first, then repair.", unpinned.Controller.View.Reason);
        Assert.AreEqual(0, unpinned.Source.FindCalls.Count);

        using var none = new Rig();
        none.RemoveInstall();
        await none.Controller.RepairAsync(Version, CancellationToken.None);
        Assert.AreEqual("There is no installed Earshot to repair.", none.Controller.View.Reason);
        Assert.AreEqual(0, none.Source.FindCalls.Count);

        foreach (Rig rig in new[] { safe, unpinned, none })
        {
            Assert.AreEqual(UpdateStage.HandoverFailed, rig.Controller.Stage);
            Assert.AreEqual(0, rig.Source.DownloadCalls);
            Assert.IsEmpty(rig.Launcher.Launches);
        }
    }

    [TestMethod]
    public async Task AnInstallThatVanishesDuringTheDownloadIsNotHandedOverToAndTheRepairSaysSo()
    {
        using var rig = new Rig();
        rig.Source.OnDownload = (_, _, _) =>
        {
            rig.RemoveInstall();
            return Task.FromResult(UpdateDownloadResult.Success(rig.Stage()));
        };

        await rig.Controller.RepairAsync(Version, CancellationToken.None);

        Assert.AreEqual(UpdateStage.HandoverFailed, rig.Controller.Stage);
        Assert.AreEqual("There is no installed Earshot to repair.", rig.Controller.View.Reason);
        Assert.IsEmpty(rig.Launcher.Launches);
        Assert.IsFalse(Directory.Exists(rig.LastStaged!.WorkFolder), "The unused download is deleted.");
    }

    [TestMethod]
    public async Task ACancelWhileFindingOrDownloadingReturnsToIdleAndAnUpdateCheckAfterwardsIsAnUpdateAgain()
    {
        using var rig = new Rig();
        var started = new TaskCompletionSource();
        rig.Source.OnDownload = async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Not reached.");
        };

        Task running = rig.Controller.RepairAsync(Version, CancellationToken.None);
        await started.Task;
        rig.Controller.Cancel();
        await running;

        Assert.AreEqual(UpdateStage.Idle, rig.Controller.Stage, "There is no update on offer to fall back to.");
        Assert.IsEmpty(rig.Launcher.Launches);

        rig.Source.OnCheck = _ => Task.FromResult(UpdateCheckResult.Available(FakeUpdateSource.Release("1.3.0")));
        await rig.Controller.CheckAsync(CancellationToken.None);

        Assert.AreEqual("Updates", rig.Controller.View.Title, "A later check is an update again, in the update's words.");
        Assert.AreEqual("Version 1.3.0 is available", rig.Controller.View.Status);
    }

    [TestMethod]
    public async Task ARepairIsIgnoredWhileAnotherStepIsUnderWay()
    {
        using var rig = new Rig();
        var release = new TaskCompletionSource();
        rig.Source.OnCheck = async ct =>
        {
            await release.Task;
            return UpdateCheckResult.UpToDate(Version);
        };
        Task check = rig.Controller.CheckAsync(CancellationToken.None);
        SpinWait.SpinUntil(() => rig.Controller.Stage == UpdateStage.Checking, TimeSpan.FromSeconds(5));

        await rig.Controller.RepairAsync(Version, CancellationToken.None);

        Assert.AreEqual(0, rig.Source.FindCalls.Count);
        release.SetResult();
        await check;
    }
}
