using Earshot.App;
using Earshot.Contracts;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// The hand-back setting on its way to config.json for the shut-down service, against the same fakes and clock as the
// rest of this folder. The harness's own coordinator sees a block controller that keeps no record of this verb, so each
// test builds a coordinator of its own over a block that does, and shares every other fake with the harness.
[TestClass]
public sealed class HandBackMirrorTests
{
    // A block controller that passes everything to the harness's fake and records the hand-back setting it is asked to
    // send, applying it to the status it reports as the gate would.
    private sealed class MirrorBlock(FakeBlock inner) : IBlockController
    {
        public List<string> HandBackCalls { get; } = new();

        // False: the gate accepts the request but config.json does not change.
        public bool Applies { get; set; } = true;

        // True: the request is refused or fails, and config.json does not change.
        public bool Fails { get; set; }

        public Task<ControllerResult> SetHandBackAtShutdownAsync(bool handBack, CancellationToken ct = default)
        {
            HandBackCalls.Add(handBack ? "on" : "off");
            if (Fails)
            {
                return Task.FromResult(ControllerResult.Fail("Could not save Hand back", [StepOutcomes.NotAttempted("set-hand-back", "refused")]));
            }

            if (Applies)
            {
                inner.Status = inner.Status with { HandBackAtShutdownMirror = handBack };
            }

            return Task.FromResult(ControllerResult.Ok("Hand back is saved"));
        }

        public bool IsSetUp => inner.IsSetUp;

        public Task<BootBlockStatus> GetStatusAsync(CancellationToken ct = default) => inner.GetStatusAsync(ct);

        public Task<ControllerResult> BlockAsync(CancellationToken ct = default) => inner.BlockAsync(ct);

        public Task<ControllerResult> AllowAsync(CancellationToken ct = default) => inner.AllowAsync(ct);

        public Task<ControllerResult> SetBlockAtBootAsync(bool blockAtBoot, CancellationToken ct = default) => inner.SetBlockAtBootAsync(blockAtBoot, ct);

        public Task<ControllerResult> SetDeviceAsync(string address12, CancellationToken ct = default) => inner.SetDeviceAsync(address12, ct);

        public Task<ControllerResult> RunSetupAsync(CancellationToken ct = default) => inner.RunSetupAsync(ct);

        public Task<ControllerResult> UninstallAsync(CancellationToken ct = default) => inner.UninstallAsync(ct);
    }

    private sealed class Rig : IDisposable
    {
        public Rig(bool setting, bool? mirror, bool tasksInstalled = true, bool safeMode = false)
        {
            Harness = new CoordinatorHarness { CheckInvariantOnPump = false };
            Harness.Settings.Update(s => s.HandBackOnShutdownAndSleep = setting);
            Harness.Block.Status = (tasksInstalled ? Statuses.Allowed() : Statuses.NotSetUp()) with { HandBackAtShutdownMirror = mirror };
            Harness.Monitor.Set(Devices.Active(0));
            Block = new MirrorBlock(Harness.Block);
            Coordinator = new BlockCoordinator(
                Harness.Monitor, Harness.Connection, Block, Harness.Protection, Harness.Settings, Harness.Cards, Harness.Log, Harness.Time,
                new CoordinatorOptions(safeMode, StartedAtLogon: false));
        }

        public CoordinatorHarness Harness { get; }

        public MirrorBlock Block { get; }

        public BlockCoordinator Coordinator { get; }

        public void Start()
        {
            Coordinator.Start();
            Pump(() => true);
        }

        // Runs what is posted to the UI thread until the condition holds, allowing the short real hops the exclusive
        // slot's own completion takes.
        public void Pump(Func<bool> done, int limitMilliseconds = 3000)
        {
            DateTime end = DateTime.UtcNow + TimeSpan.FromMilliseconds(limitMilliseconds);
            do
            {
                Harness.Ui.RunAll();
                if (done())
                {
                    Harness.Ui.RunAll();
                    return;
                }

                Thread.Sleep(2);
            }
            while (DateTime.UtcNow < end);
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            Harness.Dispose();
        }
    }

    private static int Mirrors(Rig rig) =>
        rig.Harness.Log.Entries.Count(e => e.Message.StartsWith("Hand-back mirror: config.json reads", StringComparison.Ordinal));

    [TestMethod]
    public void AFreshReadThatDisagreesWithTheSettingSendsOneOperationAndLogsWhy()
    {
        using var rig = new Rig(setting: true, mirror: false);

        rig.Start();
        rig.Pump(() => rig.Block.HandBackCalls.Count > 0);

        Assert.AreEqual("on", string.Join(",", rig.Block.HandBackCalls));
        Assert.IsTrue(rig.Harness.Log.Has(LogLevel.Info, "Hand-back mirror: config.json reads off, the setting is on; sending sethandback-on."));
    }

    [TestMethod]
    public void TheSettingOffIsSentAsSethandbackOff()
    {
        using var rig = new Rig(setting: false, mirror: true);

        rig.Start();
        rig.Pump(() => rig.Block.HandBackCalls.Count > 0);

        Assert.AreEqual("off", string.Join(",", rig.Block.HandBackCalls));
        Assert.IsTrue(rig.Harness.Log.Has(LogLevel.Info, "config.json reads on, the setting is off; sending sethandback-off."));
    }

    [TestMethod]
    public void AMirrorThatAgreesSendsNothing()
    {
        using var rig = new Rig(setting: true, mirror: true);

        rig.Start();
        rig.Pump(() => false, 300);

        Assert.IsEmpty(rig.Block.HandBackCalls);
        Assert.AreEqual(0, Mirrors(rig));
    }

    [TestMethod]
    public void AConfigThatWasNotReadSendsNothing()
    {
        using var rig = new Rig(setting: true, mirror: null);

        rig.Start();
        rig.Pump(() => false, 300);

        Assert.IsEmpty(rig.Block.HandBackCalls, "A file that was not read is no answer to send from.");
    }

    [TestMethod]
    public void BeforeSetupNothingIsSent()
    {
        using var rig = new Rig(setting: true, mirror: false, tasksInstalled: false);

        rig.Start();
        rig.Pump(() => false, 300);

        Assert.IsEmpty(rig.Block.HandBackCalls, "The task that carries the request does not exist yet.");
    }

    [TestMethod]
    public void SafeModeSendsNothing()
    {
        using var rig = new Rig(setting: true, mirror: false, safeMode: true);

        rig.Start();
        rig.Pump(() => false, 300);

        Assert.IsEmpty(rig.Block.HandBackCalls);
    }

    // Once per tray run: a request the gate carried out no better than last time is not repeated at every read.
    [TestMethod]
    public void TheMirrorIsSentOncePerRunEvenWhenConfigJsonStillDisagrees()
    {
        using var rig = new Rig(setting: true, mirror: false);
        rig.Block.Applies = false;
        rig.Start();
        rig.Pump(() => rig.Block.HandBackCalls.Count > 0);

        for (int i = 0; i < 3; i++)
        {
            rig.Coordinator.RefreshStatusAsync();
            rig.Pump(() => false, 300);
        }

        Assert.HasCount(1, rig.Block.HandBackCalls);
        Assert.AreEqual(1, Mirrors(rig));
    }

    // A request the gate refused or that failed leaves config.json as it was, so the next status read after the delay sends
    // it again; the read that follows the failed operation itself, and any read before the delay is over, do not.
    [TestMethod]
    public void AFailedMirrorIsSentAgainByAStatusReadAfterTheDelayAndNotBefore()
    {
        using var rig = new Rig(setting: true, mirror: false);
        rig.Block.Fails = true;
        rig.Start();
        rig.Pump(() => rig.Block.HandBackCalls.Count > 0);
        rig.Pump(() => false, 300);
        Assert.HasCount(1, rig.Block.HandBackCalls, "The read that follows the failed operation does not send it again.");

        rig.Harness.Time.Advance(BlockCoordinator.HandBackMirrorRetryDelay - TimeSpan.FromSeconds(1));
        rig.Coordinator.RefreshStatusAsync();
        rig.Pump(() => false, 300);
        Assert.HasCount(1, rig.Block.HandBackCalls, "Not before the delay is over.");

        rig.Block.Fails = false;
        rig.Harness.Time.Advance(TimeSpan.FromSeconds(2));
        rig.Coordinator.RefreshStatusAsync();
        rig.Pump(() => rig.Block.HandBackCalls.Count > 1);
        rig.Pump(() => false, 300);

        Assert.AreEqual("on,on", string.Join(",", rig.Block.HandBackCalls));
        Assert.AreEqual(2, Mirrors(rig), "Each send is logged with its reason.");

        rig.Harness.Time.Advance(TimeSpan.FromMinutes(5));
        rig.Coordinator.RefreshStatusAsync();
        rig.Pump(() => false, 300);
        Assert.HasCount(2, rig.Block.HandBackCalls, "Once it went through, it is not sent again.");
    }

    [TestMethod]
    public void AMenuTickThatFailedIsSentAgainByALaterStatusRead()
    {
        using var rig = new Rig(setting: true, mirror: true);
        rig.Block.Fails = true;
        rig.Start();

        Task<ControllerResult> tick = rig.Coordinator.SetHandBackAtShutdownAsync(false);
        rig.Pump(() => tick.IsCompleted);
        Assert.IsFalse(tick.Result.IsSuccess);
        rig.Harness.Settings.Update(s => s.HandBackOnShutdownAndSleep = false);
        rig.Block.Fails = false;
        rig.Harness.Time.Advance(BlockCoordinator.HandBackMirrorRetryDelay + TimeSpan.FromSeconds(1));
        rig.Coordinator.RefreshStatusAsync();
        rig.Pump(() => rig.Block.HandBackCalls.Count > 1);

        Assert.AreEqual("off,off", string.Join(",", rig.Block.HandBackCalls));
    }

    [TestMethod]
    public void NothingIsSentByItselfWhileTheSessionEnds()
    {
        using var rig = new Rig(setting: true, mirror: true);
        rig.Start();
        rig.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));

        rig.Harness.Block.Status = rig.Harness.Block.Status with { HandBackAtShutdownMirror = false };
        rig.Coordinator.RefreshStatusAsync();
        rig.Pump(() => false, 300);

        Assert.IsEmpty(rig.Block.HandBackCalls);
        Assert.AreEqual(0, Mirrors(rig), "Not even queued: the exclusive slot would refuse it, but it must not be tried.");
    }

    // The menu tick: one operation through the exclusive slot, refused with the session-ending card while the session
    // ends, exactly as every gate write is.
    [TestMethod]
    public void TheMenuTickSendsOneOperationThroughTheExclusiveSlot()
    {
        using var rig = new Rig(setting: true, mirror: true);
        rig.Start();

        Task<ControllerResult> tick = rig.Coordinator.SetHandBackAtShutdownAsync(false);
        rig.Pump(() => tick.IsCompleted);

        Assert.IsTrue(tick.IsCompleted);
        Assert.IsTrue(tick.Result.IsSuccess);
        Assert.AreEqual("off", string.Join(",", rig.Block.HandBackCalls));
        Assert.IsFalse(rig.Coordinator.IsBusy, "The operation ended.");
    }

    [TestMethod]
    public void TheMenuTickIsRefusedWhileTheSessionEndsAndSendsNothing()
    {
        using var rig = new Rig(setting: true, mirror: true);
        rig.Start();
        rig.Coordinator.OnSessionEnding(new SessionEndingEventArgs(isQuery: true, ending: true, flags: 0));

        Task<ControllerResult> tick = rig.Coordinator.SetHandBackAtShutdownAsync(false);
        rig.Pump(() => tick.IsCompleted);

        Assert.IsTrue(tick.IsCompleted);
        Assert.AreEqual(OpStatus.NotAttempted, tick.Result.Status);
        Assert.AreEqual(BlockCoordinator.SessionEndingMessage, tick.Result.UserMessage);
        Assert.IsEmpty(rig.Block.HandBackCalls);
    }

    // A change that the tick already made is not made again by the coordinator's own read that follows it.
    [TestMethod]
    public void AFailedTickIsNotRepeatedByTheReadThatFollowsIt()
    {
        using var rig = new Rig(setting: true, mirror: true);
        rig.Block.Applies = false;
        rig.Start();

        Task<ControllerResult> tick = rig.Coordinator.SetHandBackAtShutdownAsync(false);
        rig.Pump(() => tick.IsCompleted);
        rig.Harness.Settings.Update(s => s.HandBackOnShutdownAndSleep = false);
        rig.Coordinator.RefreshStatusAsync();
        rig.Pump(() => false, 300);

        Assert.HasCount(1, rig.Block.HandBackCalls);
    }

    // ---- the real menu tick, on the real TrayContext ----

    // The tick saves the setting and, once Earshot is set up, sends it on as an operation of its own. The tray's fake
    // block controller keeps no record of this verb, so the operation's own log line is what shows it was sent.
    [TestMethod]
    public void TheMenuTickSendsTheSettingOnceEarshotIsSetUp()
    {
        Earshot.Tests.Phase1.StaThread.Run(() =>
        {
            using var tray = new Earshot.Tests.Phase1.TrayHarness(settings: s => s.HandBackOnShutdownAndSleep = true);

            tray.ClickMenu(Earshot.Tray.MenuModel.HandBackOnShutdownAndSleep);
            tray.PumpUntilIdle();
            tray.ClickMenu(Earshot.Tray.MenuModel.HandBackOnShutdownAndSleep);
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Registry.Settings.Current.HandBackOnShutdownAndSleep);
            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.Contains("sethandback-off", StringComparison.Ordinal)), "The first tick sent the off request.");
            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.Contains("sethandback-on", StringComparison.Ordinal)), "The second tick sent the on request.");
            Assert.IsFalse(tray.Log.Entries.Any(e => e.Message.StartsWith("Hand-back mirror:", StringComparison.Ordinal)), "Only the ticks sent anything.");
        });
    }

    [TestMethod]
    public void TheMenuTickSendsNothingBeforeSetup()
    {
        Earshot.Tests.Phase1.StaThread.Run(() =>
        {
            using var tray = new Earshot.Tests.Phase1.TrayHarness(
                settings: s => s.HandBackOnShutdownAndSleep = true,
                arrange: h => h.Block.Status = Earshot.Tests.Phase1.Phase1Fixtures.Block(BlockState.NotSetUp));

            tray.ClickMenu(Earshot.Tray.MenuModel.HandBackOnShutdownAndSleep);
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Registry.Settings.Current.HandBackOnShutdownAndSleep, "The setting itself is still saved.");
            Assert.IsFalse(tray.Log.Entries.Any(e => e.Message.Contains("sethandback", StringComparison.Ordinal)));
        });
    }
}
