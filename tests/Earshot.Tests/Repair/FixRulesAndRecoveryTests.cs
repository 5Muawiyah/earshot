using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

[TestClass]
public sealed class FixRulesAndRecoveryTests
{
    [TestMethod]
    public void AnAirPodsContainerThatIsNotThePinnedOneIsNotProtected()
    {
        FakeNodeApi table = RecordedNodes.Table();
        using var rig = new FixRig(new FakeNodeApi(table.Nodes.Concat(FixRig.SecondAirPodsNodes())));
        AirPodsDiscoveryResult found = AirPodsDiscovery.Find(rig.Nodes);

        IReadOnlyList<AirPodsCandidate> unprotected = FixRules.UnprotectedAirPods(found, RecordedNodes.AirPodsContainer);

        Assert.HasCount(1, unprotected);
        Assert.AreEqual(FixRig.SecondAirPodsAddress, unprotected[0].Address);
    }

    [TestMethod]
    public void WithoutAPinEveryAirPodsContainerIsUnprotected()
    {
        FakeNodeApi table = RecordedNodes.Table();
        using var rig = new FixRig(new FakeNodeApi(table.Nodes.Concat(FixRig.SecondAirPodsNodes())), pinned: false);

        IReadOnlyList<AirPodsCandidate> unprotected = FixRules.UnprotectedAirPods(AirPodsDiscovery.Find(rig.Nodes), Guid.Empty);

        Assert.HasCount(2, unprotected);
    }

    [TestMethod]
    public void ThePhoneAndOtherDevicesAreNeverListedAsUnprotectedAirPods()
    {
        using var rig = new FixRig(RecordedNodes.TableWithHeadphones());

        IReadOnlyList<AirPodsCandidate> unprotected = FixRules.UnprotectedAirPods(AirPodsDiscovery.Find(rig.Nodes), RecordedNodes.AirPodsContainer);

        Assert.IsEmpty(unprotected);
    }

    [TestMethod]
    public void NothingIsAdoptedByTheRuleItself()
    {
        // The rule is a pure list: it takes no gate and no store, so it cannot move a pin.
        System.Reflection.ParameterInfo[] parameters = typeof(FixRules).GetMethod(nameof(FixRules.UnprotectedAirPods))!.GetParameters();
        Assert.IsTrue(parameters.All(p => p.ParameterType == typeof(AirPodsDiscoveryResult) || p.ParameterType == typeof(Guid)));
    }

    [TestMethod]
    public void TheEndsWithBlockRuleNeedsAnInstallAndBlockAtBootOn()
    {
        using var rig = new FixRig();
        Assert.IsTrue(FixRules.ShouldEndWithBlock(rig.Records.ReadDevice(), rig.Records.ReadConfig()));

        Assert.IsTrue(rig.Gate.Store.WriteConfig(new GateConfig { BlockAtBoot = false }).Ok);
        Assert.IsFalse(FixRules.ShouldEndWithBlock(rig.Records.ReadDevice(), rig.Records.ReadConfig()));

        File.Delete(rig.Gate.Store.DeviceFile);
        Assert.IsFalse(FixRules.ShouldEndWithBlock(rig.Records.ReadDevice(), rig.Records.ReadConfig()));
    }

    // ---- the marker ----

    [TestMethod]
    public void TheMarkerOpensAWindowForAMomentAndThenGoesStale()
    {
        using var temp = new TempFolder();
        var time = new ManualTime();
        bool alive = true;
        var recovery = new FixRecovery(temp.File("fix-window.json"), time, _ => alive);
        Assert.AreEqual(FixRecoveryState.None, recovery.Read().State);

        Assert.IsTrue(recovery.Begin("repair").Ok);
        Assert.AreEqual(FixRecoveryState.WindowOpen, recovery.Read().State);

        alive = false;
        Assert.AreEqual(FixRecoveryState.Stale, recovery.Read().State, "The run that wrote it has gone.");

        alive = true;
        time.Advance(FixRecovery.WindowLength + TimeSpan.FromSeconds(1));
        Assert.AreEqual(FixRecoveryState.Stale, recovery.Read().State, "The window is not open for ever.");

        Assert.IsTrue(recovery.Clear().Ok);
        Assert.AreEqual(FixRecoveryState.None, recovery.Read().State);
    }

    [TestMethod]
    public async Task AStaleMarkerIsAnsweredWithTheBlockAndCleared()
    {
        using var rig = new FixRig();
        bool alive = true;
        var recovery = new FixRecovery(Path.Combine(Path.GetTempPath(), "earshot-tests", Guid.NewGuid().ToString("N") + ".json"), rig.Gate.Time, _ => alive);
        recovery.Begin("repair");
        alive = false;

        FixRecoveryOutcome outcome = await recovery.RecoverAsync(rig.Records, rig.GateFake, CancellationToken.None);

        Assert.AreEqual(FixRecoveryOutcome.Blocked, outcome);
        Expect.Same(rig.GateFake.Calls, "block");
        Assert.AreEqual(FixRecoveryState.None, recovery.Read().State);
    }

    [TestMethod]
    public async Task AnOpenWindowIsLeftAlone()
    {
        using var rig = new FixRig();
        rig.Recovery.Begin("repair");

        FixRecoveryOutcome outcome = await rig.Recovery.RecoverAsync(rig.Records, rig.GateFake, CancellationToken.None);

        Assert.AreEqual(FixRecoveryOutcome.WindowOpen, outcome);
        Assert.IsEmpty(rig.GateFake.Calls);
    }

    [TestMethod]
    public async Task AFailedRecoveryBlockKeepsTheMarker()
    {
        using var rig = new FixRig();
        bool alive = true;
        var recovery = new FixRecovery(Path.Combine(Path.GetTempPath(), "earshot-tests", Guid.NewGuid().ToString("N") + ".json"), rig.Gate.Time, _ => alive);
        recovery.Begin("repair");
        alive = false;
        rig.GateFake.BlockResult = () => new FixGateResult(false, "no", [], 3);

        FixRecoveryOutcome outcome = await recovery.RecoverAsync(rig.Records, rig.GateFake, CancellationToken.None);

        Assert.AreEqual(FixRecoveryOutcome.BlockFailed, outcome);
        Assert.AreEqual(FixRecoveryState.Stale, recovery.Read().State);
    }

    [TestMethod]
    public async Task AStaleMarkerWithEarshotUninstalledJustClears()
    {
        using var rig = new FixRig();
        File.Delete(rig.Gate.Store.DeviceFile);
        bool alive = true;
        var recovery = new FixRecovery(Path.Combine(Path.GetTempPath(), "earshot-tests", Guid.NewGuid().ToString("N") + ".json"), rig.Gate.Time, _ => alive);
        recovery.Begin("repair");
        alive = false;

        FixRecoveryOutcome outcome = await recovery.RecoverAsync(rig.Records, rig.GateFake, CancellationToken.None);

        Assert.AreEqual(FixRecoveryOutcome.NothingToBlock, outcome);
        Assert.IsEmpty(rig.GateFake.Calls);
        Assert.AreEqual(FixRecoveryState.None, recovery.Read().State);
    }

    [TestMethod]
    public void AGarbledMarkerReadsAsStaleNotAsNothing()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("fix-window.json"), "not json");
        var recovery = new FixRecovery(temp.File("fix-window.json"), new ManualTime(), _ => true);

        Assert.AreEqual(FixRecoveryState.Stale, recovery.Read().State);
    }
}
