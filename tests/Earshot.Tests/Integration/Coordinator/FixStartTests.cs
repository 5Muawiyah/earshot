using Earshot.App;
using Earshot.Contracts;
using Earshot.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Integration.Coordinator;

// What the coordinator does at start about a Fix connection run that is not its own: answer one that stopped half way, leave
// one that is pairing alone, and say when paired AirPods are not the ones Earshot protects.
[TestClass]
public sealed class FixStartTests
{
    private static readonly string[] BlockOnly = ["block"];

    private static CoordinatorHarness Idle()
    {
        var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(1));
        return h;
    }

    // ---- a Fix that stopped half way ----

    [TestMethod]
    public void TheCoordinatorAnswersAFixThatStoppedHalfWayOnceWhenItStarts()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));

        h.Start();
        h.Coordinator.Start();
        h.Pump();

        Assert.AreEqual(1, h.Recovery.Runs);
    }

    [TestMethod]
    public void ARecoveryThatFailsIsLoggedAndTheCoordinatorStillStartsAndChecks()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.Recovery.Throw = new IOException("no", unchecked((int)0x80070002));
        h.ProtectionScan.Reading = new ProtectionReading(true, true, 1);

        h.Start();

        Assert.IsTrue(h.Log.Has(LogLevel.Error, "IOException"));
        Assert.AreEqual(1, h.ProtectionScan.Reads, "The check for unprotected AirPods still runs.");
    }

    [TestMethod]
    public void ARecoveryThatChangedTheNodesMakesTheCoordinatorReadThemAgainAndOneThatChangedNothingDoesNot()
    {
        using var quiet = new CoordinatorHarness();
        quiet.Block.Status = Statuses.Blocked();
        quiet.Monitor.Set(Devices.Idle(1));
        quiet.Start();
        int quietReads = quiet.Block.StatusReads;

        using var changed = new CoordinatorHarness();
        changed.Block.Status = Statuses.Blocked();
        changed.Monitor.Set(Devices.Idle(1));
        changed.Recovery.Changed = true;
        changed.Start();

        Assert.AreEqual(quietReads + 1, changed.Block.StatusReads);
    }

    // ---- the window of a run that is pairing again ----

    [TestMethod]
    public void NothingIsBlockedWhileAReplairWindowIsOpenAndTheIdleRuleStartsAgainWhenItCloses()
    {
        using CoordinatorHarness h = Idle();
        h.RePair.Open = true;
        h.Start();

        h.Advance(BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);

        Assert.IsEmpty(h.Block.Calls, "The new link of a re-pair was cut.");
        Assert.IsFalse(h.Coordinator.IdleWaitRunning);
        Assert.IsTrue(h.Coordinator.RecheckRunning, "Nothing reads the state again once the window closes.");

        h.RePair.Open = false;
        h.Advance(BlockCoordinator.IdleRetryLimit + BlockCoordinator.IdleGrace + BlockCoordinator.IdleGrace);

        CollectionAssert.AreEqual(BlockOnly, h.Block.Calls);
    }

    [TestMethod]
    public void AWindowThatOpensDuringTheIdleCountdownStopsTheBlock()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Active(1));
        h.Start();
        h.Publish(Devices.Idle(2));
        Assert.IsTrue(h.Coordinator.IdleWaitRunning);

        h.RePair.Open = true;
        h.Advance(BlockCoordinator.IdleGrace);

        Assert.IsEmpty(h.Block.Calls);
    }

    [TestMethod]
    public void TheStartUpBlockIsHeldBackByAnOpenWindowToo()
    {
        using var h = new CoordinatorHarness(startedAtLogon: true);
        h.Block.Status = Statuses.Allowed();
        h.Monitor.Set(Devices.Idle(1));
        h.RePair.Open = true;

        h.Start();

        Assert.IsEmpty(h.Block.Calls);
    }

    [TestMethod]
    public void AWindowDoesNotHoldBackTheBlockBeforeShutDownOrSleep()
    {
        // Hand-back at shut down and sleep is not an automatic block of the idle rule: it still goes ahead.
        using CoordinatorHarness h = Idle();
        h.Block.Status = Statuses.Allowed();
        h.Start();
        h.RePair.Open = true;

        h.Coordinator.BeginShutdown();
        h.Pump();

        Assert.IsTrue(h.Block.Calls.Contains("block"), "The block before closing did not go ahead: " + string.Join(",", h.Block.Calls));
    }

    // ---- paired AirPods that are not the ones Earshot protects ----

    [TestMethod]
    public void UnprotectedAirPodsAreSaidOnTheCardWithOneButtonAndNothingIsAdopted()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.ProtectionScan.Reading = new ProtectionReading(true, true, 1);
        Guid pinnedBefore = h.Settings.Current.PinnedContainerId;

        h.Start();

        CardShown card = h.Cards.Shown.Single(c => c.Content.Status == BlockCoordinator.NotProtectedMessage);
        Assert.AreEqual(CardAnchor.NearTray, card.Anchor);
        Assert.AreEqual(FixOffer.ButtonLabel, card.Content.Action?.Label);
        Assert.AreEqual(0, h.FixLauncher.Started, "Nothing starts until the person presses the button.");
        card.Content.Action!.OnClick();
        Assert.AreEqual(1, h.FixLauncher.Started);
        Assert.IsEmpty(h.Block.Calls, "Saying so changes no node.");
        Assert.AreEqual(pinnedBefore, h.Settings.Current.PinnedContainerId, "Nothing is adopted.");
    }

    [TestMethod]
    public void ThePressOnTheCardWhileARunIsOpenSaysSoAndStartsNothing()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.ProtectionScan.Reading = new ProtectionReading(true, true, 1);
        h.Start();
        CardShown card = h.Cards.Shown.Single(c => c.Content.Status == BlockCoordinator.NotProtectedMessage);
        h.FixLauncher.Running = true;

        card.Content.Action!.OnClick();

        Assert.AreEqual(0, h.FixLauncher.Started);
        Assert.AreEqual(FixOffer.AlreadyRunningMessage, h.Cards.Shown[^1].Content.Status);
    }

    [TestMethod]
    public void TheCardIsShownOncePerRun()
    {
        using var h = new CoordinatorHarness();
        h.Block.Status = Statuses.Blocked();
        h.Monitor.Set(Devices.Idle(1));
        h.ProtectionScan.Reading = new ProtectionReading(true, true, 2);

        h.Start();
        h.Coordinator.Start();
        h.Publish(Devices.Idle(2));

        Assert.AreEqual(1, h.Cards.Shown.Count(c => c.Content.Status == BlockCoordinator.NotProtectedMessage));
    }

    [TestMethod]
    public void NoCardWhenNothingIsUnprotectedWhenThereIsNoPinOrWhenTheListCouldNotBeRead()
    {
        foreach (ProtectionReading reading in new[]
                 {
                     new ProtectionReading(true, true, 0),
                     new ProtectionReading(true, false, 3),
                     new ProtectionReading(false, true, 3),
                     ProtectionReading.Nothing,
                 })
        {
            using var h = new CoordinatorHarness();
            h.Block.Status = Statuses.Blocked();
            h.Monitor.Set(Devices.Idle(1));
            h.ProtectionScan.Reading = reading;

            h.Start();

            Assert.IsFalse(h.Cards.Shown.Any(c => c.Content.Status == BlockCoordinator.NotProtectedMessage), reading.ToString());
        }
    }
}
