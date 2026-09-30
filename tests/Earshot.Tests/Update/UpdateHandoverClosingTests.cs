using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Phase1;
using Earshot.Tray;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// What the tray does after its closing work for an update's hand-over has run, while the administrator prompt is up or the
// launch has not yet answered: the real TrayContext with the update source and launcher faked, as TrayUpdateTests builds it.
[TestClass]
public sealed class UpdateHandoverClosingTests
{
    private static StagedUpdate Stage(string root, CapturingLog log)
    {
        string work = Path.Combine(root, "Local", "Earshot", "update", "u" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        string zip = Path.Combine(work, UpdateService.ZipFileName);
        File.WriteAllText(zip, "zip");
        return new StagedUpdate(new ReleaseVersion(1, 2, 0), work, zip, new string('B', 64), log);
    }

    // Once the closing work has run, the AirPods are handed back and the boot block has been asked for. The prompt can stay up
    // for as long as the person likes, and a sign-out, shut down or sleep in that time must not send a device call beside
    // the elevated install that follows: not a disconnect, not a block, not the resume check's block.
    [TestMethod]
    public void ASessionEndOrSleepWhileTheAdministratorPromptIsUpMakesNoDeviceCall()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            StagedUpdate? staged = null;
            using var tray = new UpdateTrayHarness(
                snapshot: Phase1Fixtures.TargetRenderActive(),
                settings: s => s.HandBackOnShutdownAndSleep = true,
                time: TimeProvider.System,
                source: s =>
                {
                    s.OnCheck = _ => Task.FromResult(UpdateCheckResult.Available(FakeUpdateSource.Release("1.2.0")));
                    s.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged!));
                });
            staged = Stage(temp.Path, tray.Log);
            tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();

            int disconnectsAtPrompt = -1;
            int blocksAtPrompt = -1;
            tray.Launcher.OnLaunch = _ => new LaunchResult(LaunchOutcome.Declined, 1223, "cancelled", null);
            tray.Launcher.DuringLaunch = _ =>
            {
                disconnectsAtPrompt = tray.Connection.Calls.Count;
                blocksAtPrompt = tray.Block.Calls.Count;

                // The prompt is up: each event reaches the tray's thread as the window procedure would deliver it.
                tray.Ui.Send(_ => tray.Context.OnSessionEnding(null, new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0)), null);
                tray.Ui.Send(_ => tray.Context.OnSessionEnding(null, new SessionEndingEventArgs(isQuery: false, ending: true, flags: 0)), null);
                tray.Ui.Send(_ => tray.Context.OnPowerChanged(null, new PowerEventArgs(PowerEventKind.Suspend)), null);
                tray.Ui.Send(_ => tray.Context.OnPowerChanged(null, new PowerEventArgs(PowerEventKind.ResumeAutomatic)), null);
            };

            tray.Context.StartUpdate();
            tray.PumpUntilIdle();
            tray.Settle();

            Assert.IsGreaterThan(0, disconnectsAtPrompt, "The closing work handed the AirPods back before the prompt: the test reached what it is about.");
            Assert.AreEqual(disconnectsAtPrompt, tray.Connection.Calls.Count, "A disconnect was sent after the closing work, beside the install.");
            Assert.AreEqual(blocksAtPrompt, tray.Block.Calls.Count, "A block was sent after the closing work, beside the install.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "the closing work for the update is done"), "Each refusal says why.");
        });
    }

    private static readonly TimeSpan NoticeTime = TimeSpan.FromMilliseconds(400);

    // What the cards said, and how long after the click the launch came, when the administrator prompt is asked for.
    private sealed record AtLaunch(List<string> Statuses, TimeSpan Elapsed);

    // Clicks Update on a tray that holds a download, and looks at the cards at the moment the launch is asked for: that is the
    // moment the Windows prompt would cover them.
    private static AtLaunch UpdateAndReadTheCardsAtTheLaunch(DeviceSnapshot snapshot, bool handBack, Action<UpdateTrayHarness>? arrange = null)
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        AtLaunch? result = null;
        StaThread.Run(() =>
        {
            StagedUpdate? staged = null;
            using var tray = new UpdateTrayHarness(
                snapshot: snapshot,
                settings: s => s.HandBackOnShutdownAndSleep = handBack,
                time: TimeProvider.System,
                exitNoticeTime: NoticeTime,
                source: s =>
                {
                    s.OnCheck = _ => Task.FromResult(UpdateCheckResult.Available(FakeUpdateSource.Release("1.2.0")));
                    s.OnDownload = (_, _, _) => Task.FromResult(UpdateDownloadResult.Success(staged!));
                });
            staged = Stage(temp.Path, tray.Log);
            tray.Context.Updates!.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            arrange?.Invoke(tray);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            tray.Launcher.OnLaunch = _ => new LaunchResult(LaunchOutcome.Declined, 1223, "cancelled", null);
            tray.Launcher.DuringLaunch = _ => result = new AtLaunch(tray.Cards.Statuses, clock.Elapsed);

            tray.Context.StartUpdate();
            tray.PumpUntilIdle();
            tray.Settle();
        });

        Assert.IsNotNull(result, "The launch was never asked for.");
        return result;
    }

    // The AirPods in use and Hand back off: the update ends the tray, which cannot block them, and the install that follows
    // does not. The card says so plainly before the closing work, and is still up for its whole time when the prompt comes.
    [TestMethod]
    public void WithTheAirPodsInUseAndHandBackOffTheCardSaysTheyStayConnectedAndStaysUpBeforeThePrompt()
    {
        AtLaunch atLaunch = UpdateAndReadTheCardsAtTheLaunch(Phase1Fixtures.TargetRenderActive(), handBack: false);

        Assert.Contains(UpdateCopy.AirPodsStayConnectedNotice, atLaunch.Statuses, "The notice was not shown before the launch.");
        Assert.IsGreaterThanOrEqualTo(NoticeTime, atLaunch.Elapsed, "The prompt came before the notice had been up for its time.");
        StringAssert.Contains(UpdateCopy.AirPodsStayConnectedNotice, "stay connected");
        StringAssert.Contains(UpdateCopy.AirPodsStayConnectedNotice, "blocked again at the next start");
        StringAssert.Contains(UpdateCopy.AirPodsStayConnectedNotice, "turn on Hand back or disconnect the AirPods");
        Assert.DoesNotContain(BlockCoordinator.ClosedWhileInUseMessage, atLaunch.Statuses, "The same thing is not said twice.");
    }

    [TestMethod]
    public void WithHandBackOnNoSuchNoticeIsShown()
    {
        AtLaunch atLaunch = UpdateAndReadTheCardsAtTheLaunch(Phase1Fixtures.TargetRenderActive(), handBack: true);

        Assert.DoesNotContain(UpdateCopy.AirPodsStayConnectedNotice, atLaunch.Statuses);
    }

    [TestMethod]
    public void WithTheAirPodsNotInUseNoSuchNoticeIsShown()
    {
        AtLaunch atLaunch = UpdateAndReadTheCardsAtTheLaunch(Phase1Fixtures.Target(ConnectionState.Disconnected), handBack: false);

        Assert.DoesNotContain(UpdateCopy.AirPodsStayConnectedNotice, atLaunch.Statuses);
    }

    // A notice from the closing work itself is kept up for its time before the launch, as Exit keeps its own: here the
    // disconnect did not confirm, which is said at the end of the hand-back.
    [TestMethod]
    public void ANoticeFromTheClosingWorkIsKeptUpForItsTimeBeforeTheLaunch()
    {
        AtLaunch atLaunch = UpdateAndReadTheCardsAtTheLaunch(
            Phase1Fixtures.TargetRenderActive(), handBack: true,
            arrange: tray => tray.Connection.OnDisconnect = _ => Task.FromResult(new ConnectResult(ConnectOutcome.AttemptedTimedOut, "No change", [])));

        Assert.Contains(BlockCoordinator.ExitNotDisconnectedBlockedMessage, atLaunch.Statuses, "The notice was not shown before the launch.");
        Assert.IsGreaterThanOrEqualTo(NoticeTime, atLaunch.Elapsed, "The prompt came before the notice had been up for its time.");
    }
}
