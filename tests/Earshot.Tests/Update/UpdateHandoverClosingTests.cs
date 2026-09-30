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
}
