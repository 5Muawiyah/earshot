using System.Globalization;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase1;
using Earshot.Tray;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Update;

// Repair Earshot in the real TrayContext, with the update source, the launcher, the file check and the block controller
// faked: which route each state of the install takes, that a download happens only when a file is bad, and what the
// person is told. Nothing elevates, downloads, reads Program Files or touches a device.
[TestClass]
public sealed class TrayRepairTests
{
    private const string Repair = "Repair Earshot...";

    private static InstalledFilesReport OneBad => new(false, false, ["Earshot.dll"], []);

    private static void ClickRepair(UpdateTrayHarness tray)
    {
        tray.ClickMenu(Repair);
        tray.PumpUntilIdle();
    }

    // ----- every file matches: the installed program repairs itself, nothing is downloaded -----

    [TestMethod]
    public void FilesThatAllMatchRunTheInstalledProgramsRepairAndDownloadNothing()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: false, otherCopy: true, installedVersion: RepairPlanner.RepairVerbSince.ToVersion());

            ClickRepair(tray);

            Assert.AreEqual("repair", tray.Block.Calls.Single(), "The repair verb, run by the installed program.");
            Assert.AreEqual(@"C:\Program Files\Earshot", tray.CheckedFolders.Single(), "Every installed file was checked, in the install folder.");
            Assert.AreEqual(0, tray.Source.FindCalls.Count);
            Assert.AreEqual(0, tray.Source.DownloadCalls, "Files that match need nothing downloaded.");
            Assert.IsEmpty(tray.Launcher.Launches);
            Assert.AreEqual("Done", tray.Cards.Shown[^1].Content.Status, "The result is shown, success included.");
        });
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void AnInstalledProgramFromBeforeTheRepairVerbIsAskedThroughInstall(int patch)
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            var older = new Version(1, 2, patch, 0);
            using var tray = new UpdateTrayHarness(installedVersion: older);

            ClickRepair(tray);

            Assert.AreEqual("repair-install", tray.Block.Calls.Single(), older + " has no repair verb that can be relied on.");
            Assert.AreEqual(0, tray.Source.DownloadCalls);
        });
    }

    // ----- a file is missing or does not match: the release of the installed version, verified, through update -----

    [TestMethod]
    public void AMismatchingFileDownloadsTheReleaseOfTheInstalledVersionAndHandsItToTheInstalledProgramsUpdate()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            StagedUpdate? staged = null;
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true, checkFiles: _ => OneBad, installedVersion: new Version(1, 2, 1, 0),
                source: s => s.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged!)));
            staged = Stage(temp.Path, tray.Log);
            tray.Ui.Post(_ => tray.Context.RepairFromCard(), null);
            bool timedOut = false;
            using var watchdog = new System.Threading.Timer(
                _ =>
                {
                    timedOut = true;
                    tray.Ui.Post(_ => tray.Context.ExitThread(), null);
                },
                null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);

            Application.Run(tray.Context);

            Assert.IsFalse(timedOut, "The tray did not close after the hand-over.");
            Assert.AreEqual(new ReleaseVersion(1, 2, 1), tray.Source.FindCalls.Single(), "The release of the installed version, not the latest.");
            Assert.AreEqual(0, tray.Source.CheckCalls);
            Assert.AreEqual(1, tray.Source.DownloadCalls);
            Assert.IsEmpty(tray.Block.Calls, "Nothing in the install folder is trusted to repair itself.");
            (string exe, string[] arguments, string _) = tray.Launcher.Launches.Single();
            Assert.AreEqual(UpdateTrayHarness.InstalledExe, exe);
            Assert.AreEqual("update", arguments[0], "The installed program's existing update path.");
            Assert.AreEqual(staged.ZipPath, arguments[1]);
            Assert.AreEqual(staged.ZipSha256, arguments[2], "The verified hash goes with the zip.");
            Assert.AreEqual(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), arguments[3]);
        });
    }

    [TestMethod]
    public void NothingIsDownloadedForARepairUntilThePersonClicksAndASafeModeRepairChecksNothing()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(safeMode: true, checkFiles: _ => OneBad);
            Assert.AreEqual(0, tray.Source.FindCalls.Count);
            Assert.AreEqual(0, tray.Source.DownloadCalls);
            Assert.IsEmpty(tray.CheckedFolders, "Starting the tray reads nothing of the install.");

            ClickRepair(tray);

            Assert.AreEqual("Safe mode: no device actions.", tray.Cards.Shown[^1].Content.Status);
            Assert.IsEmpty(tray.CheckedFolders, "Safe mode refuses before any file is hashed.");
            Assert.AreEqual(0, tray.Source.DownloadCalls);
            Assert.IsEmpty(tray.Block.Calls);
            Assert.IsEmpty(tray.Launcher.Launches);
        });
    }

    [TestMethod]
    public void ADeclinedPromptAfterTheDownloadLeavesTheTrayRunningAndSaysNothingWasChanged()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            StagedUpdate? staged = null;
            using var tray = new UpdateTrayHarness(
                checkFiles: _ => OneBad, installedVersion: new Version(1, 2, 1, 0),
                source: s => s.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged!)));
            staged = Stage(temp.Path, tray.Log);
            tray.Launcher.OnLaunch = _ => new LaunchResult(LaunchOutcome.Declined, 1223, "cancelled", null);

            ClickRepair(tray);

            Assert.AreEqual("Couldn't start the repair", tray.Cards.Shown[^1].Content.Title);
            Assert.AreEqual("The Windows prompt was declined, so nothing was changed.", tray.Cards.Shown[^1].Content.Status);
            Assert.IsFalse(Directory.Exists(staged.WorkFolder), "The unused download is deleted.");
            Assert.IsFalse(File.Exists(RepairNote(temp.Path)), "No note says an update was a repair when nothing was handed over.");
        });
    }

    // A repair keeps no release from an earlier check, so Try again on the card looks for it again.
    [TestMethod]
    public void TryAgainOnTheCardAfterARepairThatCouldNotFindItsReleaseLooksForTheReleaseAgain()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            StagedUpdate? staged = null;
            bool reachable = false;
            using var tray = new UpdateTrayHarness(
                checkFiles: _ => OneBad, installedVersion: new Version(1, 2, 1, 0),
                source: s =>
                {
                    s.OnFind = (version, _) => Task.FromResult(reachable
                        ? UpdateCheckResult.Available(FakeUpdateSource.Release(version.ToString()))
                        : UpdateCheckResult.Failed(new UpdateFailure(UpdateFailureKind.Network, "Couldn't reach GitHub. Check your connection.", "no route")));
                    s.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged!));
                });
            staged = Stage(temp.Path, tray.Log);
            tray.Launcher.OnLaunch = _ => new LaunchResult(LaunchOutcome.Declined, 1223, "cancelled", null);
            ClickRepair(tray);
            Assert.AreEqual(UpdateStage.DownloadFailed, tray.Context.Updates!.Stage);
            Assert.AreEqual("Couldn't reach GitHub. Check your connection.", tray.Context.Updates.View.Reason);

            reachable = true;
            tray.Context.WidgetCardHostForTest.TryUpdateAgain();
            tray.PumpUntilIdle();

            Assert.AreEqual(2, tray.Source.FindCalls.Count, "The release was looked for again.");
            Assert.AreEqual(1, tray.Launcher.Launches.Count, "And this time it got as far as the prompt, which was declined.");
            Assert.AreEqual(UpdateStage.HandoverFailed, tray.Context.Updates.Stage);
        });
    }

    // ----- no installed program to run, or this copy is newer -----

    [TestMethod]
    public void AnInstallWhoseProgramIsMissingIsRepairedFromThisCopysOwnSetupWithoutADownload()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: false, installProgramMissing: true, checkFiles: _ => OneBad);

            ClickRepair(tray);

            Assert.AreEqual("repair-setup", tray.Block.Calls.Single());
            Assert.IsEmpty(tray.CheckedFolders, "There is no installed program to check.");
            Assert.AreEqual(0, tray.Source.DownloadCalls);
        });
    }

    [TestMethod]
    public void AnInstallFolderAStandardUserCanWriteIsRepairedFromThisCopysSetupNeverFromTheInstalledProgram()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: false, otherCopy: true, installFolderSddl: FixedFolderSecurity.UsersCanWrite);

            ClickRepair(tray);

            Assert.AreEqual("repair-setup", tray.Block.Calls.Single(), "The elevated program is never one in a folder a user can write.");
            Assert.IsEmpty(tray.Launcher.Launches);
        });
    }

    // A copy newer than the install may be in a folder the signed-in user can write, so it is never the elevated program
    // while an install exists. Repair sends it to the verified update path: a check, then Update (download, SHA-256, the
    // installed program's update verb).
    [TestMethod]
    public void ACopyNewerThanTheInstalledOneIsNeverElevatedAndRepairOpensTheUpdatePathInstead()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true, block: b => b.Status = Block(BlockState.Allowed) with { RunningCopyIsNewer = true },
                source: s => s.OnCheck = _ => Task.FromResult(UpdateCheckResult.Available(FakeUpdateSource.Release("1.3.0"))));
            tray.PumpUntilIdle();

            ClickRepair(tray);

            Assert.IsEmpty(tray.Block.Calls, "No elevated run of any kind, this copy's setup least of all.");
            Assert.IsEmpty(tray.Launcher.Launches);
            Assert.IsEmpty(tray.CheckedFolders, "The installed files are not checked: the update replaces them from a verified release.");
            Assert.AreEqual(1, tray.Source.CheckCalls, "A check for the update path.");
            Assert.AreEqual(0, tray.Source.DownloadCalls, "Nothing downloads until the person presses Update.");
            Assert.AreEqual("Version 1.3.0 is available", tray.Cards.Shown[^1].Content.Title);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "UpdateInstead"), "The route is logged.");
        });
    }

    // The update replaces the installed copy, so it is measured against that copy: a newer copy unzipped in a download folder
    // is still offered the update that brings the install up to date, and the installed copy itself is measured against
    // its own version.
    [TestMethod]
    public void AnUpdateIsMeasuredAgainstTheInstalledProgramWhenThisCopyIsAnotherOne()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: false, otherCopy: true, installedVersion: new Version(1, 2, 0, 0));

            Assert.AreEqual(new ReleaseVersion(1, 2, 0), tray.Context.Updates!.Installed);
        });
    }

    [TestMethod]
    public void AnUpdateIsMeasuredAgainstThisCopyWhenItIsTheInstalledOneOrTheInstalledVersionCannotBeRead()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        ReleaseVersion running = ReleaseVersion.Running(typeof(TrayContext).Assembly)!.Value;
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: true, installedVersion: new Version(9, 9, 9, 0));

            Assert.AreEqual(running, tray.Context.Updates!.Installed, "The installed copy is this one: its own version.");
        });
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true,
                readInstalledFile: path => new Earshot.Boot.InstalledFile(true, null, new StepOutcome("read-file-version", false, unchecked((int)0x80070020), "SHARING", path)));

            Assert.AreEqual(running, tray.Context.Updates!.Installed, "A version that cannot be read is not invented.");
        });
    }

    // ----- a file another program holds open is not a file that is missing -----

    // A standard user can hold the installed program open. Through the real check and reader on a real tree with a real
    // FileShare.None handle, and the real tray: nothing is elevated, nothing downloads, the raw code is logged, and the
    // person is told to try again. On the route it replaced, this ran this copy's own setup elevated.
    [TestMethod]
    public void AnInstalledProgramAnotherProgramHoldsOpenIsNotRepairedFromThisCopyAndThePersonIsToldToTryAgain()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        using var installTree = new TempInstallTree(temp);
        using var held = new FileStream(installTree.File("Earshot.exe"), FileMode.Open, FileAccess.Read, FileShare.None);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true,
                checkFiles: _ => InstalledFileCheck.Check(installTree.Folder),
                readInstalledFile: _ => new Earshot.Boot.InstalledFileReader().Read(installTree.File("Earshot.exe")));

            ClickRepair(tray);

            Assert.IsEmpty(tray.Block.Calls, "Nothing was elevated: " + string.Join(", ", tray.Block.Calls));
            Assert.IsEmpty(tray.Launcher.Launches);
            Assert.AreEqual(0, tray.Source.DownloadCalls);
            Assert.AreEqual(0, tray.Source.FindCalls.Count);
            Assert.AreEqual(UpdateCopy.RepairCouldNotReadStatus, tray.Cards.Shown[^1].Content.Title);
            Assert.AreEqual(UpdateCopy.RepairCouldNotReadText, tray.Cards.Shown[^1].Content.Status);
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "verify-installed:Earshot.exe"), "The raw code of the failed read is logged.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "CouldNotRead"));
        });
    }

    // Once the other program lets go, the same tray repairs the install itself: the route is decided from what is read each time.
    [TestMethod]
    public void OnceTheHandleIsReleasedTheSameInstallIsRepairedByItsOwnProgram()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        using var installTree = new TempInstallTree(temp);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true,
                checkFiles: _ => InstalledFileCheck.Check(installTree.Folder),
                readInstalledFile: _ => new Earshot.Boot.InstalledFile(true, RepairPlanner.RepairVerbSince.ToVersion(), new StepOutcome("read-file-version", true, 0, "S_OK", "")));

            ClickRepair(tray);

            Assert.AreEqual("repair", tray.Block.Calls.Single(), "Every file matches: the installed program repairs itself.");
        });
    }

    // ----- the menu -----

    [TestMethod]
    public void RepairIsOnTheMenuForAnInstallInAnyStateAndSetUpOnlyForNone()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(block: b => b.Status = Block(BlockState.NotSetUp));
            tray.PumpUntilIdle();
            tray.Context.Menu.Refresh();
            Assert.IsTrue(tray.MenuItem("Set up Earshot...").Available);
            Assert.IsFalse(tray.MenuItem(Repair).Available, "Nothing is installed: Set up, not Repair.");

            tray.Block.Status = Block(BlockState.Allowed) with { NeedsRepair = true };
            _ = tray.Coordinator.RefreshStatusAsync();
            tray.PumpUntilIdle();
            tray.Context.Menu.Refresh();
            Assert.IsFalse(tray.MenuItem("Set up Earshot...").Available);
            Assert.IsTrue(tray.MenuItem(Repair).Available, "An install exists, damaged or not: Repair.");
        });
    }

    // ----- how it ended, said at the next start -----

    private static string UpdateFolder(string root) => Path.Combine(root, "Local", "Earshot", "update");

    private static string RepairNote(string root) => Path.Combine(UpdateFolder(root), "repair-note.txt");

    private static StagedUpdate Stage(string root, CapturingLog log)
    {
        string work = Path.Combine(UpdateFolder(root), "u" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, UpdateService.ZipFileName);
        File.WriteAllText(zip, "zip");
        return new StagedUpdate(new ReleaseVersion(1, 2, 1), work, zip, new string('B', 64), log);
    }

    private const string OutcomeId = "0123456789abcdef0123456789abcdef";

    private static (UpdateOutcomeSource Source, GateStore Store) Machine(string root)
    {
        string machine = Path.Combine(root, "ProgramData", "Earshot");
        Directory.CreateDirectory(machine);
        return (new UpdateOutcomeSource(machine, Path.Combine(UpdateFolder(root), "outcome-shown.txt")), new GateStore(machine));
    }

    // The update path records Installed. A note written before the hand-over says the update was a repair, so the tray says
    // "repaired", once.
    [TestMethod]
    public void AnUpdateThatWasARepairIsSaidToHaveBeenARepairOnce()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        (UpdateOutcomeSource source, GateStore store) = Machine(temp.Path);
        var now = new Earshot.Tests.Phase3.ManualTimeProvider().GetUtcNow();
        Directory.CreateDirectory(UpdateFolder(temp.Path));
        File.WriteAllText(RepairNote(temp.Path), "1.2.1\n" + now.AddMinutes(-2).ToString("O", CultureInfo.InvariantCulture));
        Assert.IsTrue(store.WriteUpdateOutcome(new UpdateOutcome(OutcomeId, now, UpdateOutcomeKind.Installed, "1.2.1", "", "")).Ok);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(outcomeSource: source);
            tray.Settle();

            Assert.HasCount(1, tray.Cards.Shown);
            Assert.AreEqual("Earshot was repaired.", tray.Cards.Shown[0].Content.Status);
            Assert.IsFalse(File.Exists(RepairNote(temp.Path)), "The note is used once.");
            Assert.AreEqual(OutcomeId, File.ReadAllText(source.ShownFile), "And the outcome is noted as shown.");
        });
    }

    [TestMethod]
    public void AnUpdateWithNoNoteOrWithAStaleNoteIsStillAnUpdate()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        (UpdateOutcomeSource source, GateStore store) = Machine(temp.Path);
        var now = new Earshot.Tests.Phase3.ManualTimeProvider().GetUtcNow();
        Directory.CreateDirectory(UpdateFolder(temp.Path));
        File.WriteAllText(RepairNote(temp.Path), "1.2.1\n" + now.AddHours(-5).ToString("O", CultureInfo.InvariantCulture));
        Assert.IsTrue(store.WriteUpdateOutcome(new UpdateOutcome(OutcomeId, now, UpdateOutcomeKind.Installed, "1.3.0", "", "")).Ok);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(outcomeSource: source);
            tray.Settle();

            Assert.AreEqual("Earshot was updated to 1.3.0.", tray.Cards.Shown[0].Content.Status, "A repair note from hours before is not this update's.");
        });
    }

    [TestMethod]
    public void ARepairRunFromTheInstalledCopyRecordsItsOutcomeAndTheTrayThatWaitedForItNotesItShownSoItIsNotSaidTwice()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        (UpdateOutcomeSource source, GateStore store) = Machine(temp.Path);
        StaThread.Run(() =>
        {
            var clock = new Earshot.Tests.Phase3.ManualTimeProvider();
            using var tray = new UpdateTrayHarness(
                outcomeSource: source, time: clock, installedVersion: RepairPlanner.RepairVerbSince.ToVersion(),
                block: b => b.OnRepaired = () =>
                    Assert.IsTrue(store.WriteUpdateOutcome(new UpdateOutcome(OutcomeId, clock.GetUtcNow().AddSeconds(5), UpdateOutcomeKind.Repaired, "1.2.2", "", "")).Ok));

            ClickRepair(tray);

            Assert.AreEqual("repair", tray.Block.Calls.Single());
            Assert.AreEqual(OutcomeId, File.ReadAllText(source.ShownFile), "The outcome the elevated run recorded is noted as shown: the next start does not say it again.");
        });
    }

    // ----- the card -----

    [TestMethod]
    public void TheCardsSettingsPageOffersRepairOnlyWhenAnInstallExists()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(block: b => b.Status = Block(BlockState.NotSetUp));
            tray.PumpUntilIdle();
            Assert.IsFalse(tray.Context.WidgetCardHostForTest.ReadSettings().InstallExists);

            tray.Block.Status = Block(BlockState.Blocked);
            _ = tray.Coordinator.RefreshStatusAsync();
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Context.WidgetCardHostForTest.ReadSettings().InstallExists);
        });
    }

    [TestMethod]
    public void TheCardsRepairSetUpAndSwitchButtonsReachTheTray()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: false, otherCopy: true, installedVersion: RepairPlanner.RepairVerbSince.ToVersion());
            tray.Settle();

            tray.Context.WidgetCardHostForTest.RepairEarshot();
            tray.PumpUntilIdle();
            Assert.AreEqual("repair", tray.Block.Calls.Single());

            tray.Context.WidgetCardHostForTest.SetUpEarshot();
            tray.PumpUntilIdle();
            Assert.AreEqual("setup", tray.Block.Calls[^1]);
        });
    }
}
