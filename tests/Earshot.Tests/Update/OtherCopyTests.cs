using System.Windows.Forms;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase1;
using Earshot.Tray;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// The tray when it is not the installed copy: the owner ran a copy from a download folder while Earshot was installed in
// Program Files. What it offers, what it says, and what it never does (point Open on startup or the Start menu shortcut
// at itself). The install is fake, so no test looks at the owner's Program Files.
[TestClass]
public sealed class OtherCopyTests
{
    private static void FoundNewer(FakeUpdateSource source) =>
        source.OnCheck = _ => Task.FromResult(UpdateCheckResult.Available(FakeUpdateSource.Release("1.2.0")));

    // Stands in for the real shortcut writer: nothing is ever written to the owner's Start menu.
    private sealed class RecordingShellLinks : Earshot.Widget.Alert.IShellLinkWriter
    {
        public List<(string ShortcutPath, string TargetPath, string AppUserModelId)> Calls { get; } = new();

        public StepOutcome WriteShortcut(string shortcutPath, string targetPath, string appUserModelId)
        {
            Calls.Add((shortcutPath, targetPath, appUserModelId));
            return StepOutcomes.FromHResult("fake-shortcut", 0);
        }

        public Earshot.Widget.Alert.ShortcutRead ReadShortcut(string shortcutPath) =>
            new(Earshot.Widget.Alert.ShortcutReadStatus.NotFound, null, null);
    }

    // ----- what the update card says and offers -----

    [TestMethod]
    public void WithNothingInstalledACheckThatFindsANewerVersionOffersSetUpOnTheCard()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: false, source: FoundNewer);

            tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();

            UpdateViewModel view = tray.Context.Updates.View;
            Assert.AreEqual("Set up Earshot first, then update.", view.Notice);
            Assert.AreEqual(UpdateButtonRole.SetUp, view.Buttons.Single().Role, "Set up is offered on the same card, not a dead end.");
            Assert.AreEqual(0, tray.Source.DownloadCalls);
            Assert.IsEmpty(tray.Launcher.Launches);
        });
    }

    [TestMethod]
    public void AnInstallWhoseProgramIsMissingOffersRepairAndHandsNothingOver()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: false, installProgramMissing: true, source: FoundNewer);
            tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();

            tray.Context.StartUpdate();
            tray.PumpUntilIdle();

            UpdateViewModel view = tray.Context.Updates.View;
            Assert.AreEqual("Repair Earshot first, then update.", view.Notice);
            Assert.AreEqual(UpdateButtonRole.Repair, view.Buttons.Single().Role);
            Assert.AreEqual(0, tray.Source.DownloadCalls);
            Assert.IsEmpty(tray.Launcher.Launches);
        });
    }

    // The elevated program is never one in a folder a standard user can change, so an install whose folder is not
    // administrators-only is not handed to, from this copy or any other.
    [TestMethod]
    public void AnInstallFolderThatAStandardUserCanWriteIsNotHandedOverToFromAnyCopy()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        foreach (bool otherCopy in new[] { true, false })
        {
            StaThread.Run(() =>
            {
                using var tray = new UpdateTrayHarness(
                    installedCopy: !otherCopy, otherCopy: otherCopy, installFolderSddl: FixedFolderSecurity.UsersCanWrite, source: FoundNewer);
                tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();

                tray.Context.StartUpdate();
                tray.PumpUntilIdle();

                Assert.AreEqual(UpdateButtonRole.Repair, tray.Context.Updates.View.Buttons.Single().Role, "otherCopy " + otherCopy);
                Assert.AreEqual(0, tray.Source.DownloadCalls);
                Assert.IsEmpty(tray.Launcher.Launches, "otherCopy " + otherCopy);
            });
        }
    }

    // ----- the switch -----

    [TestMethod]
    public void ACopyThatIsNotTheInstalledOneSaysSoOnceAndOffersTheSwitch()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: false, otherCopy: true);
            tray.Settle();

            Assert.AreEqual(UpdateStage.SwitchOffered, tray.Context.Updates!.Stage);
            Assert.AreEqual("Earshot is already installed", tray.Context.Updates.View.Status);
            Assert.AreEqual(UpdateButtonRole.Switch, tray.Context.Updates.View.Buttons.Single().Role);
            Assert.HasCount(1, tray.Cards.Shown, "With no widget card to hold the button, one short message card says it.");
            Assert.AreEqual("Earshot", tray.Cards.Shown[0].Content.Title);
            Assert.AreEqual("Earshot is already installed in Program Files. Start that copy from the Start menu.", tray.Cards.Shown[0].Content.Status);
        });
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public void TheInstalledCopyItselfARunWithNothingInstalledAndAnInstallWithNoProgramOfferNoSwitch(bool runsFromInstall, bool programMissing)
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: runsFromInstall, installProgramMissing: programMissing);
            tray.Settle();

            Assert.AreEqual(UpdateStage.Idle, tray.Context.Updates!.Stage, "A copy cannot switch to a program that is not there, and the installed copy has nowhere to switch.");
            Assert.IsEmpty(tray.Cards.Shown);
        });
    }

    [TestMethod]
    public void SafeModeNeverOffersASwitchThatWouldExitAndStartTheOwnersInstall()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(safeMode: true, installedCopy: false, otherCopy: true);
            tray.Settle();

            Assert.AreEqual(UpdateStage.Idle, tray.Context.Updates!.Stage);
            Assert.IsEmpty(tray.Cards.Shown);
        });
    }

    [TestMethod]
    public void TheSwitchButtonClosesThisCopyAndNamesTheInstalledProgramToStartAfterwards()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: false, otherCopy: true);
            tray.Settle();
            tray.Ui.Post(_ => tray.Context.SwitchToInstalledCopy(), null);
            bool timedOut = false;
            using var watchdog = new System.Threading.Timer(
                _ =>
                {
                    timedOut = true;
                    tray.Ui.Post(_ => tray.Context.ExitThread(), null);
                },
                null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);

            Application.Run(tray.Context);

            Assert.IsFalse(timedOut, "This copy did not close.");
            Assert.AreEqual(UpdateTrayHarness.InstalledExe, tray.Context.StartAfterExit, "The installed program, not this copy.");
            Assert.IsEmpty(tray.Launcher.Launches, "Nothing is started elevated: the start is a plain one, after the lock is released.");
        });
    }

    // The switch starts the installed copy with this process's own token. From a tray started with Run as administrator that
    // would be an elevated tray, so no switch is offered and the button refuses; the person starts it from the Start menu.
    [TestMethod]
    public void AnElevatedTrayIsOfferedNoSwitchAndTheSwitchRefusesToStartAnElevatedCopy()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: false, otherCopy: true, elevated: true);
            tray.Settle();

            Assert.AreEqual(UpdateStage.Idle, tray.Context.Updates!.Stage, "No switch is offered on the update page.");
            Assert.AreEqual(UpdateCopy.SwitchMessage, tray.Cards.Shown[0].Content.Status, "The person is told to start the installed copy themselves.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "runs elevated, so no switch to the installed copy is offered"));

            tray.Context.SwitchToInstalledCopy();
            tray.PumpUntilIdle();

            Assert.IsNull(tray.Context.StartAfterExit, "Nothing is started: it would start elevated.");
            Assert.IsEmpty(tray.Launcher.Launches);
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "this copy runs elevated"));
        });
    }

    [TestMethod]
    public void AnElevatedTrayThatFindsTheInstalledCopyUsableStillDoesNotCloseForTheSwitchButton()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: false, otherCopy: true, elevated: true);
            tray.Settle();

            tray.Context.SwitchToInstalledCopy();
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Log.Has(LogLevel.Info, "Switch: this copy is closing"), "It did not start closing.");
        });
    }

    [TestMethod]
    public void TheSwitchRefusesWhenThereIsNothingUsableToSwitchTo()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(installedCopy: false, installProgramMissing: true);
            tray.Settle();

            tray.Context.SwitchToInstalledCopy();
            tray.PumpUntilIdle();

            Assert.IsNull(tray.Context.StartAfterExit);
            Assert.AreEqual("Repair Earshot first, then update.", tray.Cards.Shown[^1].Content.Status);
        });
    }

    // ----- Open on startup and the Start menu shortcut are never pointed at a copy that is not installed -----

    private const string Installed = UpdateTrayHarness.InstalledExe;
    private const string Other = UpdateTrayHarness.OtherExe;

    // Two spellings of one path are the same file, so a copy running from the install folder under a different spelling is not
    // "another copy" and an install is not taken as damaged because of it.
    [TestMethod]
    public void TheInstalledCopyRunningUnderAnotherSpellingOfItsPathIsNotAnotherCopy()
    {
        var registry = new FakeStartupRegistry();
        var respelled = @"C:\Program Files\Earshot\..\Earshot\EARSHOT.EXE";
        var startup = new StartupRegistration(
            registry, new CapturingLog(), safeMode: false, respelled, redirected: false, Installed,
            fileExists: _ => false, folderExists: path => path == @"C:\Program Files\Earshot");

        Assert.IsFalse(startup.InstallDamaged, "The running copy is the installed one: its program is there, this is not a damaged install.");
        Assert.AreEqual(respelled, startup.TargetExePath, "And with no other copy there is nothing to refuse to point at.");
    }

    [TestMethod]
    public void ACopyThatIsReallyAnotherOneStillFindsTheInstallDamagedWhenItsProgramIsMissing()
    {
        var startup = new StartupRegistration(
            new FakeStartupRegistry(), new CapturingLog(), safeMode: false, Other, redirected: false, Installed,
            fileExists: _ => false, folderExists: path => path == @"C:\Program Files\Earshot");

        Assert.IsTrue(startup.InstallDamaged);
        Assert.IsNull(startup.TargetExePath);
    }

    private static StartupRegistration Startup(FakeStartupRegistry registry, CapturingLog log, bool installedProgram, bool installedFolder) =>
        new(registry, log, safeMode: false, Other, redirected: false, Installed,
            fileExists: path => installedProgram && path == Installed, folderExists: path => installedFolder && path == @"C:\Program Files\Earshot");

    [TestMethod]
    public void ACopyThatIsNotInstalledPointsOpenOnStartupAtTheInstalledCopyNeverAtItself()
    {
        var registry = new FakeStartupRegistry();
        registry.Run[StartupRegistration.ValueName] = StartupRegistration.CommandFor(Other);
        var log = new CapturingLog();
        StartupRegistration startup = Startup(registry, log, installedProgram: true, installedFolder: true);

        Assert.AreEqual(Installed, startup.TargetExePath);
        Assert.IsTrue(startup.RunValueNeedsRepair(), "A value that starts the download folder's copy is stale.");
        startup.Apply(true);

        Assert.AreEqual(StartupRegistration.CommandFor(Installed), registry.Run[StartupRegistration.ValueName]);
    }

    // A damaged install: the folder is there and its program is not. The copy in a download folder used to write itself
    // into the Run value then.
    [TestMethod]
    public void ACopyThatIsNotInstalledNeverWritesItselfIntoTheRunValueWhenTheInstalledProgramIsMissing()
    {
        var registry = new FakeStartupRegistry();
        var log = new CapturingLog();
        StartupRegistration startup = Startup(registry, log, installedProgram: false, installedFolder: true);

        Assert.IsNull(startup.TargetExePath);
        Assert.IsTrue(startup.InstallDamaged);
        Assert.IsFalse(startup.RunValueNeedsRepair(), "Nothing is repaired towards a copy that is not installed.");
        ControllerResult result = startup.Apply(true);

        Assert.AreEqual(OpStatus.NotAttempted, result.Status);
        Assert.AreEqual("Earshot is installed but its program is missing. Choose Repair Earshot first.", result.UserMessage);
        Assert.IsEmpty(registry.Run, "The Run value was not written.");
    }

    [TestMethod]
    public void WithNothingInstalledTheRunningCopyIsStillTheTarget()
    {
        var registry = new FakeStartupRegistry();
        var log = new CapturingLog();
        StartupRegistration startup = Startup(registry, log, installedProgram: false, installedFolder: false);

        Assert.AreEqual(Other, startup.TargetExePath);
        Assert.IsFalse(startup.InstallDamaged);
        Assert.AreEqual(OpStatus.Success, startup.Apply(true).Status);
        Assert.AreEqual(StartupRegistration.CommandFor(Other), registry.Run[StartupRegistration.ValueName], "Before setup the Run value starts this copy, as it always did.");
    }

    [TestMethod]
    public void TheStartMenuShortcutIsNeverWrittenForACopyThatIsNotInstalled()
    {
        var writer = new RecordingShellLinks();
        var log = new CapturingLog();

        var installedPresent = new Earshot.Widget.Alert.NotificationRegistration(
            writer, log, safeMode: false, redirected: false, @"C:\folder", Other, Installed,
            fileExists: path => path == Installed, folderExists: _ => true);
        Assert.AreEqual(Installed, installedPresent.TargetExePath);
        installedPresent.Register();
        Assert.AreEqual(Installed, writer.Calls.Single().TargetPath, "The shortcut starts the installed copy.");

        writer.Calls.Clear();
        var programMissing = new Earshot.Widget.Alert.NotificationRegistration(
            writer, log, safeMode: false, redirected: false, @"C:\folder", Other, Installed,
            fileExists: _ => false, folderExists: _ => true);
        Assert.IsNull(programMissing.TargetExePath);
        StepOutcome step = programMissing.Register();

        Assert.IsFalse(step.Ok);
        Assert.IsEmpty(writer.Calls, "No shortcut is written that starts a download folder's copy.");
    }

    // The tray itself, end to end: started from a download folder with Open on startup on and a Run value that starts
    // that folder. It is repaired to the installed copy, never left pointing at this one, and nothing is written for
    // this copy's own path.
    [TestMethod]
    public void TheTrayRunFromADownloadFolderRepairsAStaleRunValueTowardsTheInstalledCopy()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true, settings: s => s.OpenOnStartup = true,
                startup: r => r.Run[StartupRegistration.ValueName] = StartupRegistration.CommandFor(Other));
            tray.Settle();

            Assert.AreEqual(StartupRegistration.CommandFor(Installed), tray.Startup.Run[StartupRegistration.ValueName]);
            Assert.IsFalse(tray.Startup.Run.Values.Any(v => v.Contains(Other, StringComparison.OrdinalIgnoreCase)));
        });
    }
}
