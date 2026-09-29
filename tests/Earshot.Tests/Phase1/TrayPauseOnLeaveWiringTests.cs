using System.Windows.Forms;
using Earshot.App;
using Earshot.Audio;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tray;
using Earshot.Widget.EarPause;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// The tray builds pause on leave from what the registry holds and hands it to the coordinator, and lets go of it when it
// closes. The audio worker here is the real class with nothing to read: no thread starts until something asks it to
// run, and the harness clock never moves, so the sampler never reads.
[TestClass]
public sealed class TrayPauseOnLeaveWiringTests
{
    [TestMethod]
    public void WithAnAudioWorkerTheTrayGivesTheCoordinatorPauseOnLeaveAndTakesItBackWhenItCloses()
    {
        StaThread.Run(() =>
        {
            var worker = new AudioWorker(new CapturingLog());
            try
            {
                using var tray = new TrayHarness(
                    snapshot: Devices.Idle(1),
                    safeMode: true,
                    arrange: t => t.Registry.Worker = worker);

                Assert.IsNotNull(tray.Coordinator.LeavePause, "The tray built no pause on leave.");
                Assert.IsNotNull(tray.Registry.MediaSessions, "The tray built no media session source.");

                tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);
                Application.Run(tray.Context);

                Assert.IsNull(tray.Coordinator.LeavePause, "Closing left pause on leave with the coordinator.");
            }
            finally
            {
                worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    // Safe mode: the media source the tray builds refuses a pause, like every device action there.
    [TestMethod]
    public void InSafeModeTheMediaSourceRefusesAPause()
    {
        StaThread.Run(() =>
        {
            var worker = new AudioWorker(new CapturingLog());
            try
            {
                using var tray = new TrayHarness(snapshot: Devices.Idle(1), safeMode: true, arrange: t => t.Registry.Worker = worker);

                bool paused = tray.Registry.MediaSessions!.TryPauseAsync("player.exe", CancellationToken.None).GetAwaiter().GetResult();

                Assert.IsFalse(paused);
                Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "Safe mode: no device actions. Refused: pause."));
            }
            finally
            {
                worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    // No audio worker (a test, or discovery unavailable): nothing is built, and it is said once.
    [TestMethod]
    public void WithNoAudioWorkerNothingIsBuiltAndItIsSaid()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Devices.Idle(1));

            Assert.IsNull(tray.Coordinator.LeavePause);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Pause when AirPods leave this PC: not available, because there is no audio worker"));
        });
    }
}
