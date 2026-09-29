using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase1;
using Earshot.Tray;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// The tray's side of updating, driving the real TrayContext with the update source and launcher faked: the menu
// item, the card that shows the result, the daily check, and the close that follows a hand-over. The data root is a
// temporary folder for each test, so the last-check file and the staging folder are never the owner's.
[TestClass]
public sealed class TrayUpdateTests
{
    private static string[] AvailableTexts(TrayMenu menu) =>
        menu.Items.Where(i => i.Available).Select(i => i is ToolStripSeparator ? "-" : i.Text ?? "").ToArray();

    private static readonly string[] BeforeExit = ["Check for updates", "Check automatically", "-", "Exit"];

    private static string UpdateFolder(string root) => Path.Combine(root, "Local", "Earshot", "update");

    private static void FoundNewer(FakeUpdateSource source, string version = "1.2.0") =>
        source.OnCheck = _ => Task.FromResult(UpdateCheckResult.Available(FakeUpdateSource.Release(version)));

    private static StagedUpdate Stage(string root, CapturingLog log)
    {
        string work = Path.Combine(UpdateFolder(root), "u" + Guid.NewGuid().ToString("N")[..8]);
        string app = Path.Combine(work, "app");
        Directory.CreateDirectory(app);
        string exe = Path.Combine(app, "Earshot.exe");
        File.WriteAllText(exe, "program");
        return new StagedUpdate(new ReleaseVersion(1, 2, 0), work, app, exe, [new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read)], log);
    }

    // ----- the menu -----

    [TestMethod]
    public void TheMenuHasCheckForUpdatesAndCheckAutomaticallyJustAboveExit()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();

            string[] texts = AvailableTexts(tray.Context.Menu);

            CollectionAssert.AreEqual(BeforeExit, texts[^4..]);
            Assert.AreEqual(CheckState.Unchecked, tray.MenuItem("Check automatically").CheckState, "Off by default.");
        });
    }

    [TestMethod]
    public void NothingIsContactedAndNoDailyCheckStartsByDefault()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();

            Assert.AreEqual(0, tray.Source.CheckCalls);
            Assert.AreEqual(0, tray.Source.DownloadCalls);
            Assert.IsFalse(tray.Context.AutoCheckRunning);
            Assert.AreEqual(0, tray.UpdateClock.LiveTimers, "Nothing is waiting to check.");
            Assert.IsFalse(tray.Settings.Current.CheckForUpdatesAutomatically);
        });
    }

    // ----- Check for updates -----

    [TestMethod]
    public void CheckForUpdatesShowsTheVersionThatIsAvailableOnTheMessageCard()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(source: s => FoundNewer(s));

            tray.ClickMenu("Check for updates");
            tray.PumpUntilIdle();

            Assert.AreEqual(1, tray.Source.CheckCalls);
            Assert.AreEqual(0, tray.Source.DownloadCalls, "A check never downloads.");
            Assert.IsEmpty(tray.Launcher.Launches);
            Assert.HasCount(2, tray.Cards.Shown);
            Assert.AreEqual("Checking for updates", tray.Cards.Shown[0].Content.Status);
            Assert.AreEqual("Version 1.2.0 is available", tray.Cards.Shown[1].Content.Title);
            Assert.AreEqual("Version 1.1.0 installed", tray.Cards.Shown[1].Content.Status);
            Assert.IsTrue(tray.Cards.Shown.All(c => c.Anchor == CardAnchor.NearCursor), "Shown where the click was, like the other menu results.");
        });
    }

    [TestMethod]
    public void CheckForUpdatesSaysSoWhenTheProgramIsUpToDate()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();

            tray.ClickMenu("Check for updates");
            tray.PumpUntilIdle();

            Assert.AreEqual("You're up to date", tray.Cards.Shown[^1].Content.Title);
            Assert.AreEqual("Version 1.1.0", tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void CheckForUpdatesShowsAFailureWithItsPlainReason()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(source: s => s.OnCheck = _ => Task.FromResult(
                UpdateCheckResult.Failed(new UpdateFailure(UpdateFailureKind.HttpStatus, "GitHub answered with an error (500).", "500"))));

            tray.ClickMenu("Check for updates");
            tray.PumpUntilIdle();

            Assert.AreEqual("Couldn't check for updates", tray.Cards.Shown[^1].Content.Title);
            Assert.AreEqual("GitHub answered with an error (500).", tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void CheckForUpdatesWorksInSafeModeBecauseItTouchesNoDevice()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(safeMode: true);

            tray.ClickMenu("Check for updates");
            tray.PumpUntilIdle();

            Assert.AreEqual(1, tray.Source.CheckCalls);
        });
    }

    // ----- Check automatically -----

    [TestMethod]
    public void TurningCheckAutomaticallyOnSavesItAndStartsTheDailyCheckWhichOnlyChecks()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(source: s => FoundNewer(s));

            tray.ClickMenu("Check automatically");
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Settings.Current.CheckForUpdatesAutomatically);
            tray.Context.Menu.Refresh();
            Assert.AreEqual(CheckState.Checked, tray.MenuItem("Check automatically").CheckState);
            UpdateTrayHarness.PumpUntil(() => tray.Context.AutoCheckRunning && tray.UpdateClock.ArmedTimers == 1, "The daily check never started waiting.");
            Assert.AreEqual(0, tray.Source.CheckCalls, "Nothing is checked in the first minute.");

            tray.UpdateClock.Advance(UpdateAutoCheck.StartupDelay);
            UpdateTrayHarness.PumpUntil(() => tray.Source.CheckCalls == 1 && tray.Cards.Shown.Count == 1, "The first automatic check did not happen.");

            Assert.AreEqual("Version 1.2.0 is available", tray.Cards.Shown[0].Content.Title);
            Assert.AreEqual(0, tray.Source.DownloadCalls, "An automatic check never downloads.");
            Assert.IsEmpty(tray.Launcher.Launches);
            Assert.IsTrue(File.Exists(Path.Combine(UpdateFolder(temp.Path), "last-check.txt")), "The check is recorded.");

            // A day later it checks again, and does not announce the same version a second time.
            UpdateTrayHarness.PumpUntil(() => tray.UpdateClock.ArmedTimers == 1, "The daily check did not go back to waiting.");
            tray.UpdateClock.Advance(TimeSpan.FromDays(1) + UpdateAutoCheck.PollInterval);
            UpdateTrayHarness.PumpUntil(() => tray.Source.CheckCalls == 2, "The second automatic check did not happen.");
            tray.PumpUntilIdle();
            Assert.HasCount(1, tray.Cards.Shown);
        });
    }

    [TestMethod]
    public void ASettingAlreadyOnAtStartupStartsTheDailyCheckAtOnce()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(settings: s => s.CheckForUpdatesAutomatically = true);

            Assert.IsTrue(tray.Context.AutoCheckRunning);
            UpdateTrayHarness.PumpUntil(() => tray.UpdateClock.ArmedTimers == 1, "The daily check never started waiting.");
        });
    }

    [TestMethod]
    public void ACheckMadeWithinTheDayIsNotRepeatedByARestart()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        Directory.CreateDirectory(UpdateFolder(temp.Path));
        DateTimeOffset now = new Phase3.ManualTimeProvider().GetUtcNow();
        File.WriteAllText(Path.Combine(UpdateFolder(temp.Path), "last-check.txt"), (now - TimeSpan.FromHours(3)).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(settings: s => s.CheckForUpdatesAutomatically = true);
            UpdateTrayHarness.PumpUntil(() => tray.UpdateClock.ArmedTimers == 1, "The daily check never started waiting.");

            tray.UpdateClock.Advance(UpdateAutoCheck.StartupDelay);
            Thread.Sleep(150);
            tray.PumpUntilIdle();

            Assert.AreEqual(0, tray.Source.CheckCalls, "Checked three hours ago, so not again now.");
        });
    }

    // ----- staging left behind -----

    [TestMethod]
    public void AFolderAnEarlierUpdateLeftBehindIsRemovedWhenTheTrayStartsButTheStampIsKept()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        string left = Path.Combine(UpdateFolder(temp.Path), "u1234abcd", "app");
        Directory.CreateDirectory(left);
        File.WriteAllText(Path.Combine(left, "Earshot.exe"), "old");
        File.WriteAllText(Path.Combine(UpdateFolder(temp.Path), "last-check.txt"), "kept");
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();

            Assert.IsFalse(Directory.Exists(Path.Combine(UpdateFolder(temp.Path), "u1234abcd")));
            Assert.IsTrue(File.Exists(Path.Combine(UpdateFolder(temp.Path), "last-check.txt")));
        });
    }

    [TestMethod]
    public void ATrayStartedWithNoUpdateFolderMakesNone()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();

            Assert.IsFalse(Directory.Exists(UpdateFolder(temp.Path)), "Starting the tray leaves the update folder alone until something needs it.");
        });
    }

    // ----- the hand-over -----

    [TestMethod]
    public void ClickingUpdateDownloadsHandsOverAndTheTrayClosesInsteadOfHoldingItsFolder()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            StagedUpdate? staged = null;
            using var tray = new UpdateTrayHarness(source: s =>
            {
                FoundNewer(s);
                s.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged!));
            });
            staged = Stage(temp.Path, tray.Log);
            try
            {
                tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                tray.Ui.Post(_ => tray.Context.StartUpdate(), null);
                var watch = System.Diagnostics.Stopwatch.StartNew();

                Application.Run(tray.Context);

                Assert.IsLessThan(TimeSpan.FromSeconds(10), watch.Elapsed);
                Assert.AreEqual(staged.ExecutablePath, tray.Launcher.Launches.Single().Executable);
                Assert.AreEqual("install", tray.Launcher.Launches.Single().Arguments[0]);
                Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Earshot is closing so the update can replace its files."), "The tray closed through Exit.");
                Assert.AreEqual(1, tray.Cards.Hides);
                Assert.AreEqual(1, tray.Source.DownloadCalls);
            }
            finally
            {
                staged.Dispose();
            }
        });
    }

    [TestMethod]
    public void ADeclinedPromptLeavesTheTrayRunningAndSaysNothingWasChanged()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            StagedUpdate? staged = null;
            using var tray = new UpdateTrayHarness(source: s =>
            {
                FoundNewer(s);
                s.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged!));
            });
            staged = Stage(temp.Path, tray.Log);
            tray.Launcher.OnLaunch = _ => new LaunchResult(LaunchOutcome.Declined, 1223, "cancelled", null);
            tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();

            tray.Context.StartUpdate();
            tray.PumpUntilIdle();

            Assert.AreEqual("Version 1.2.0 is available", tray.Cards.Shown[^1].Content.Title);
            Assert.AreEqual("The Windows prompt was declined, so nothing was changed.", tray.Cards.Shown[^1].Content.Status);
            Assert.IsFalse(tray.Log.Has(LogLevel.Info, "Earshot is closing so the update can replace its files."));
            Assert.IsFalse(Directory.Exists(staged.WorkFolder), "The unused download is deleted.");
        });
    }

    [TestMethod]
    public void AnUpdateInSafeModeOrOnTestDataIsRefusedBeforeAnyDownload()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(safeMode: true, source: s => FoundNewer(s));
            tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();

            tray.Context.StartUpdate();
            tray.PumpUntilIdle();

            Assert.AreEqual(0, tray.Source.DownloadCalls);
            Assert.IsEmpty(tray.Launcher.Launches);
            Assert.AreEqual("Version 1.2.0 is available", tray.Cards.Shown[^1].Content.Title);
            StringAssert.Contains(tray.Cards.Shown[^1].Content.Status, "Safe mode");
        });
    }

    [TestMethod]
    public void AnUpdateBeforeAnyDeviceIsChosenAsksForTheAirPodsFirst()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(pinned: false, source: s => FoundNewer(s));
            tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();

            tray.Context.StartUpdate();
            tray.PumpUntilIdle();

            Assert.AreEqual(0, tray.Source.DownloadCalls);
            Assert.AreEqual("Choose your AirPods first, then update.", tray.Cards.Shown[^1].Content.Status);
        });
    }
}
