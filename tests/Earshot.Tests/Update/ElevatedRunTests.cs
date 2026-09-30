using System.Globalization;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase1;
using Earshot.Tray;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Update;

// The tray's side of the elevated operations (setup, repair, update), through the real TrayContext and the real block
// coordinator with only the controllers, the update source and the launcher faked: the order of the closing device work
// and the elevated program, one elevated operation at a time, and what Exit does while one runs. Nothing elevates,
// downloads or touches a device.
[TestClass]
public sealed class ElevatedRunTests
{
    private static string UpdateFolder(string root) => Path.Combine(root, "Local", "Earshot", "update");

    private static void FoundNewer(FakeUpdateSource source) =>
        source.OnCheck = _ => Task.FromResult(UpdateCheckResult.Available(FakeUpdateSource.Release("1.3.0")));

    private static StagedUpdate Stage(string root, CapturingLog log)
    {
        string work = Path.Combine(UpdateFolder(root), "u" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, UpdateService.ZipFileName);
        File.WriteAllText(zip, "zip");
        return new StagedUpdate(new ReleaseVersion(1, 3, 0), work, zip, new string('B', 64), log);
    }

    private static readonly string[] DisconnectBlockLaunch = ["disconnect", "block:start", "block:end", "launch"];
    private static readonly string[] BlockLaunch = ["block:start", "block:end", "launch"];
    private static readonly string[] BlockThenLaunch = ["block", "launch"];
    private static readonly string[] RepairLabels = ["Repair Earshot...", "Check for updates"];

    // Runs the tray's message loop until it ends, with a watchdog so a hand-over that never closes the tray fails the test.
    private static bool RunUntilClosed(UpdateTrayHarness tray, int seconds = 20)
    {
        bool timedOut = false;
        using var watchdog = new System.Threading.Timer(
            _ =>
            {
                timedOut = true;
                tray.Ui.Post(_ => tray.Context.ExitThread(), null);
            },
            null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
        Application.Run(tray.Context);
        return !timedOut;
    }

    // ----- the hand-over waits for the closing device work -----

    // The installed 1.2.0 and the first 1.2.1 do not wait for a copy run from another folder to end, so an install started
    // while this tray is still handing the AirPods back and blocking them can replace files under it. The tray finishes that
    // work first, then starts the elevated program, then ends without another device call.
    [TestMethod]
    public void AnUpdateHandOverHandsTheAirPodsBackAndBlocksThemBeforeTheElevatedProgramStartsAndMakesNoDeviceCallAfter()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            StagedUpdate? staged = null;
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true, snapshot: Devices.Active(1), settings: s => s.HandBackOnShutdownAndSleep = true,
                block: b => b.Status = Block(BlockState.Allowed),
                source: s =>
                {
                    FoundNewer(s);
                    s.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged!));
                });
            staged = Stage(temp.Path, tray.Log);
            var trace = new List<string>();
            tray.Connection.OnDisconnect = async _ =>
            {
                trace.Add("disconnect");
                await Task.Delay(30, CancellationToken.None);
                return new ConnectResult(ConnectOutcome.Confirmed, "Disconnected", []);
            };
            tray.Block.OnBlock = async _ =>
            {
                trace.Add("block:start");
                await Task.Delay(30, CancellationToken.None);
                trace.Add("block:end");
                return ControllerResult.Ok("Blocked at boot");
            };
            tray.Launcher.DuringLaunch = _ => trace.Add("launch");
            tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            tray.PumpUntilIdle();
            trace.Clear();
            tray.Ui.Post(_ => tray.Context.StartUpdate(), null);

            Assert.IsTrue(RunUntilClosed(tray), "The tray did not close after the hand-over.");

            CollectionAssert.AreEqual(DisconnectBlockLaunch, trace, "The hand-back and the block finish before the elevated program starts, and nothing follows it.");
            Assert.AreEqual(1, tray.Launcher.Launches.Count);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Update: the elevated program started, so Earshot ends."));
        });
    }

    [TestMethod]
    public void AnUpdateHandOverWithHandBackOffStillBlocksBeforeTheElevatedProgramStarts()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            StagedUpdate? staged = null;
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true, snapshot: Devices.Idle(1), block: b => b.Status = Block(BlockState.Allowed),
                source: s =>
                {
                    FoundNewer(s);
                    s.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged!));
                });
            staged = Stage(temp.Path, tray.Log);
            var trace = new List<string>();
            tray.Block.OnBlock = async _ =>
            {
                trace.Add("block:start");
                await Task.Delay(30, CancellationToken.None);
                trace.Add("block:end");
                return ControllerResult.Ok("Blocked at boot");
            };
            tray.Launcher.DuringLaunch = _ => trace.Add("launch");
            tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            tray.PumpUntilIdle();
            trace.Clear();
            tray.Ui.Post(_ => tray.Context.StartUpdate(), null);

            Assert.IsTrue(RunUntilClosed(tray));

            CollectionAssert.AreEqual(BlockLaunch, trace);
            Assert.IsEmpty(tray.Connection.Calls, "Hand back is off: nothing is disconnected.");
        });
    }

    // A repair that fetches the installed version's release goes through the same hand-over, so it has the same order.
    [TestMethod]
    public void ARepairByDownloadFinishesTheClosingBlockBeforeTheElevatedProgramStartsToo()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            StagedUpdate? staged = null;
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true, snapshot: Devices.Idle(1), block: b => b.Status = Block(BlockState.Allowed),
                checkFiles: _ => new Earshot.Boot.Gate.InstalledFilesReport(false, false, ["Earshot.dll"], []), installedVersion: new Version(1, 2, 1, 0),
                source: s => s.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged!)));
            staged = Stage(temp.Path, tray.Log);
            var trace = new List<string>();
            tray.Block.OnBlock = async _ =>
            {
                trace.Add("block:start");
                await Task.Delay(30, CancellationToken.None);
                trace.Add("block:end");
                return ControllerResult.Ok("Blocked at boot");
            };
            tray.Launcher.DuringLaunch = _ => trace.Add("launch");
            tray.PumpUntilIdle();
            trace.Clear();
            tray.Ui.Post(_ => tray.Context.RepairFromCard(), null);

            Assert.IsTrue(RunUntilClosed(tray));

            CollectionAssert.AreEqual(BlockLaunch, trace);
            Assert.AreEqual("update", tray.Launcher.Launches.Single().Arguments[0], "The installed program's update verb.");
        });
    }

    // The prompt is refused after the closing work has been done: the tray cannot go back to what it was, so it says so,
    // ends and starts again, and sends no device call after the launch that was refused.
    [TestMethod]
    public void ADeclinedPromptAfterTheClosingWorkMakesNoFurtherDeviceCallAndTheTrayStartsAgain()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            StagedUpdate? staged = null;
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true, snapshot: Devices.Idle(1), block: b => b.Status = Block(BlockState.Allowed),
                source: s =>
                {
                    FoundNewer(s);
                    s.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged!));
                });
            staged = Stage(temp.Path, tray.Log);
            var trace = new List<string>();
            tray.Block.OnBlock = _ =>
            {
                trace.Add("block");
                return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
            };
            tray.Launcher.DuringLaunch = _ => trace.Add("launch");
            tray.Launcher.OnLaunch = _ => new LaunchResult(LaunchOutcome.Declined, 1223, "cancelled", null);
            tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            tray.PumpUntilIdle();
            trace.Clear();
            tray.Ui.Post(_ => tray.Context.StartUpdate(), null);

            Assert.IsTrue(RunUntilClosed(tray));

            CollectionAssert.AreEqual(BlockThenLaunch, trace);
            Assert.AreEqual(UpdateTrayHarness.OtherExe, tray.Context.StartAfterExit, "It starts again from the copy that was running.");
            Assert.AreEqual("The Windows prompt was declined, so nothing was changed. Earshot is starting again.", tray.Cards.Shown[^1].Content.Status);
        });
    }

    // Exit pressed while the update is on its way: the hand-over does not start a second closing.
    [TestMethod]
    public void AnUpdateThatFindsTheTrayAlreadyClosingIsNotStartedAndNothingIsElevated()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            StagedUpdate? staged = null;
            var downloading = new TaskCompletionSource<UpdateDownloadResult>();
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true,
                source: s =>
                {
                    FoundNewer(s);
                    s.OnDownload = (_, _, _) => downloading.Task;
                });
            staged = Stage(temp.Path, tray.Log);
            tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            tray.Context.StartUpdate();
            UpdateTrayHarness.PumpUntil(() => tray.Source.DownloadCalls == 1, "The download never started.");
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);
            UpdateTrayHarness.PumpUntil(() => tray.Log.Has(LogLevel.Info, "Exit chosen from the tray menu."), "Exit was not taken.");

            downloading.SetResult(UpdateDownloadResult.Success(staged));
            Assert.IsTrue(RunUntilClosed(tray));

            Assert.IsEmpty(tray.Launcher.Launches, "A tray that is closing does not start the elevated program.");
        });
    }

    // ----- one elevated operation at a time -----

    [TestMethod]
    public void WhileARepairRunsAnotherRepairAnUpdateAndASetupAreRefusedWithTheReasonAndTheMenuAndCardSaySo()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            var release = new TaskCompletionSource<ControllerResult>();
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true, block: b => b.OnRunRepair = (_, ct) => release.Task.WaitAsync(ct),
                source: FoundNewer);
            tray.Context.RepairFromCard();
            UpdateTrayHarness.PumpUntil(() => tray.Block.Calls.Contains("repair"), "The repair never started.");

            Assert.AreEqual("repair", tray.Context.ElevatedRunInProgress);
            tray.Context.Menu.Refresh();
            foreach (string label in RepairLabels)
            {
                System.Windows.Forms.ToolStripMenuItem item = tray.Context.Menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(i => i.Text!.StartsWith(label, StringComparison.Ordinal));
                Assert.IsFalse(item.Enabled, label + " is disabled while the repair runs.");
                Assert.AreEqual(label + " (finishing the repair first)", item.Text, "And says why in its own words.");
            }

            Assert.AreEqual("Finishing the repair first.", tray.Context.WidgetCardHostForTest.ReadSettings().ElevatedRunNote, "The card's settings rows say so too.");

            int cards = tray.Cards.Shown.Count;
            tray.Context.RepairFromCard();
            tray.Context.StartUpdate();
            tray.Context.SetUpFromCard();
            tray.Context.TryUpdateAgainFromCard();
            Application.DoEvents();

            Assert.AreEqual(1, tray.Block.Calls.Count(c => c == "repair"), "No second repair started.");
            Assert.DoesNotContain("setup", tray.Block.Calls, "No setup started.");
            Assert.AreEqual(0, tray.Source.DownloadCalls, "No update started.");
            Assert.IsGreaterThan(cards, tray.Cards.Shown.Count);
            Assert.Contains("Finishing the repair first.", tray.Cards.Statuses);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "not started, the repair is still running"));

            release.SetResult(ControllerResult.Ok("Done"));
            tray.PumpUntilIdle();

            Assert.IsNull(tray.Context.ElevatedRunInProgress);
            Assert.AreEqual(1, tray.Block.Calls.Count(c => c == "repair"), "Nothing refused was queued behind the first: no second repair ran when it ended.");
            tray.Context.Menu.Refresh();
            Assert.IsTrue(tray.MenuItem("Repair Earshot...").Enabled, "Enabled again once it has ended.");
            Assert.IsNull(tray.Context.WidgetCardHostForTest.ReadSettings().ElevatedRunNote);
        });
    }

    [TestMethod]
    public void WhileASetupRunsARepairIsRefusedAndTheReasonNamesTheSetup()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            var release = new TaskCompletionSource<ControllerResult>();
            using var tray = new UpdateTrayHarness(installedCopy: false, otherCopy: true, block: b => b.OnRunSetup = ct => release.Task.WaitAsync(ct));
            tray.Context.SetUpFromCard();
            UpdateTrayHarness.PumpUntil(() => tray.Block.Calls.Contains("setup"), "The setup never started.");

            tray.Context.RepairFromCard();
            Application.DoEvents();

            Assert.DoesNotContain("repair", tray.Block.Calls);
            Assert.Contains("Finishing the setup first.", tray.Cards.Statuses);

            release.SetResult(ControllerResult.Ok("Done"));
            tray.PumpUntilIdle();
        });
    }

    [TestMethod]
    public void WhileAnUpdateDownloadsARepairIsRefusedAndTheReasonNamesTheUpdate()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            var downloading = new TaskCompletionSource<UpdateDownloadResult>();
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true,
                source: s =>
                {
                    FoundNewer(s);
                    s.OnDownload = (_, _, ct) => downloading.Task.WaitAsync(ct);
                });
            tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            tray.Context.StartUpdate();
            UpdateTrayHarness.PumpUntil(() => tray.Source.DownloadCalls == 1, "The download never started.");

            tray.Context.RepairFromCard();
            Application.DoEvents();

            Assert.IsEmpty(tray.Block.Calls);
            Assert.Contains("Finishing the update first.", tray.Cards.Statuses);

            tray.Context.Updates.Cancel();
            tray.PumpUntilIdle();
            Assert.IsNull(tray.Context.ElevatedRunInProgress, "A cancelled download ends the claim.");
        });
    }

    // ----- Exit waits for a repair that is running -----

    // Exit's hand-back and block must not meet a scheduled task the repair is registering again. The fake repair behaves as
    // the real launcher does: when its token is cancelled it stops waiting and leaves the "program" running, which is what
    // let Exit go on to block while the repair was still working.
    [TestMethod]
    public void ExitWaitsForARunningRepairBeforeItBlocks()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            var programEnds = new TaskCompletionSource();
            var trace = new List<string>();
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true, snapshot: Devices.Idle(1),
                block: b =>
                {
                    b.Status = Block(BlockState.Allowed);
                    b.OnRunRepair = async (_, ct) =>
                    {
                        trace.Add("repair:start");
                        try
                        {
                            await programEnds.Task.WaitAsync(ct);
                        }
                        catch (OperationCanceledException)
                        {
                            trace.Add("repair:gave-up-waiting");
                            throw;
                        }

                        trace.Add("repair:end");
                        return ControllerResult.Ok("Done");
                    };
                    b.OnBlock = _ =>
                    {
                        trace.Add("block");
                        return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
                    };
                });
            tray.PumpUntilIdle();
            trace.Clear();
            tray.Context.RepairFromCard();
            UpdateTrayHarness.PumpUntil(() => trace.Contains("repair:start"), "The repair never started.");
            using var release = new System.Threading.Timer(_ => programEnds.TrySetResult(), null, TimeSpan.FromMilliseconds(400), Timeout.InfiniteTimeSpan);
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);

            Assert.IsTrue(RunUntilClosed(tray));

            Assert.IsGreaterThan(-1, trace.IndexOf("block"), "Exit still blocked: " + string.Join(", ", trace));
            Assert.DoesNotContain("repair:gave-up-waiting", trace, "Exit cancelled the repair instead of waiting for it.");
            Assert.IsLessThan(trace.IndexOf("block"), trace.IndexOf("repair:end"), "The block came after the repair ended: " + string.Join(", ", trace));
            Assert.Contains("Finishing the repair first.", tray.Cards.Statuses, "The card says why Exit waits.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Exit: the repair ended, so the hand-back and the block go on."));
        });
    }

    [TestMethod]
    public void ExitThatCannotWaitForeverSaysSoAndLogsItAndStillBlocks()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            var programEnds = new TaskCompletionSource();
            var trace = new List<string>();
            using var tray = new UpdateTrayHarness(
                installedCopy: false, otherCopy: true, snapshot: Devices.Idle(1), elevatedExitWait: TimeSpan.FromMilliseconds(250),
                block: b =>
                {
                    b.Status = Block(BlockState.Allowed);
                    b.OnRunRepair = async (_, ct) =>
                    {
                        trace.Add("repair:start");
                        try
                        {
                            await programEnds.Task.WaitAsync(ct);
                        }
                        catch (OperationCanceledException)
                        {
                            trace.Add("repair:gave-up-waiting");
                            throw;
                        }

                        return ControllerResult.Ok("Done");
                    };
                    b.OnBlock = _ =>
                    {
                        trace.Add("block");
                        return Task.FromResult(ControllerResult.Ok("Blocked at boot"));
                    };
                });
            tray.PumpUntilIdle();
            trace.Clear();
            tray.Context.RepairFromCard();
            UpdateTrayHarness.PumpUntil(() => trace.Contains("repair:start"), "The repair never started.");
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);

            Assert.IsTrue(RunUntilClosed(tray));

            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "Exit: the repair was still running after 0.25 s"), "The wait that ran out is logged.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "is not known here"), "The log says what is not known about the BootBlock task, and does not promise it.");
            Assert.IsFalse(tray.Log.Has(LogLevel.Warn, "BootBlock task blocks the AirPods at the next start"), "No certainty about the next start.");
            Assert.Contains(TrayContext.ClosedBeforeElevatedEndedMessage("repair"), tray.Cards.Statuses, "And said on a card.");
            Assert.Contains("block", trace, "Exit still blocked after the wait ran out.");
            Assert.IsGreaterThan(trace.IndexOf("repair:start"), trace.IndexOf("repair:gave-up-waiting"));
            programEnds.TrySetResult();
        });
    }

    [TestMethod]
    public void ExitWithNoElevatedRunInFlightDoesNotWaitOrSayAnything()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(snapshot: Devices.Idle(1), block: b => b.Status = Block(BlockState.Allowed));
            tray.PumpUntilIdle();
            tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);

            Assert.IsTrue(RunUntilClosed(tray));

            Assert.DoesNotContain("Finishing the repair first.", tray.Cards.Statuses);
            Assert.IsFalse(tray.Log.Has(LogLevel.Info, "is still running, so the hand-back and the block wait"));
        });
    }
}
