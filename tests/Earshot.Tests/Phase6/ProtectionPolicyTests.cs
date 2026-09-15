using Earshot.AudioProtection;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase6;

// The ordering between the node block and the Handsfree service state, run as a small state machine over a
// simulated device.
[TestClass]
public sealed class ProtectionPolicyTests
{
    // A simulated device. The services read lags behind a change until ReadServices runs, as the real read
    // does. Allow can bring Handsfree back, as reconnects have been reported to.
    private sealed class World
    {
        public NodePhase Nodes { get; set; } = NodePhase.Allowed;

        // What the device really has now.
        public AudioProtectionState RealServices { get; set; } = AudioProtectionState.NotProtected;

        // What the caller last read.
        public AudioProtectionState ReadServices { get; set; } = AudioProtectionState.Unknown;

        public bool Protect { get; set; } = true;

        public bool IntentPending { get; set; }

        public AudioProtectionState ServicesAfterAllow { get; set; } = AudioProtectionState.NotProtected;

        public bool ProtectTakes { get; set; } = true;

        public bool AllowTakes { get; set; } = true;

        public NodePhase NodesDuringServiceChange { get; private set; } = NodePhase.Allowed;

        public List<ProtectionAction> Run(ProtectionGoal goal)
        {
            var actions = new List<ProtectionAction>();
            var progress = default(ProtectionProgress);
            for (int i = 0; i < 20; i++)
            {
                ProtectionAction next = ProtectionPolicy.Next(goal, new ProtectionFacts(Nodes, ReadServices, Protect, IntentPending), progress);
                if (next == ProtectionAction.Done)
                {
                    return actions;
                }

                actions.Add(next);
                Apply(next);
                progress = progress.After(next);
            }

            throw new AssertFailedException("The sequence did not finish: " + string.Join(", ", actions));
        }

        private void Apply(ProtectionAction action)
        {
            switch (action)
            {
                case ProtectionAction.ReadServices:
                    ReadServices = RealServices;
                    break;
                case ProtectionAction.ProtectOn:
                case ProtectionAction.ProtectOff:
                    if (Nodes != NodePhase.Allowed)
                    {
                        NodesDuringServiceChange = Nodes;
                    }

                    if (ProtectTakes)
                    {
                        RealServices = action == ProtectionAction.ProtectOn ? AudioProtectionState.Protected : AudioProtectionState.NotProtected;
                    }

                    break;
                case ProtectionAction.BlockNodes:
                    Nodes = NodePhase.Blocked;
                    break;
                case ProtectionAction.AllowNodes:
                    if (AllowTakes)
                    {
                        Nodes = NodePhase.Allowed;
                        RealServices = ServicesAfterAllow;
                    }

                    break;
                case ProtectionAction.StoreIntent:
                    IntentPending = true;
                    break;
            }
        }
    }

    private static ProtectionAction[] Seq(params ProtectionAction[] actions) => actions;

    [TestMethod]
    public void BlockWithProtectionOnTurnsHandsfreeOffFirstThenReadsThenBlocks()
    {
        var world = new World();

        List<ProtectionAction> actions = world.Run(ProtectionGoal.Block);

        CollectionAssert.AreEqual(
            Seq(ProtectionAction.ReadServices, ProtectionAction.ProtectOn, ProtectionAction.ReadServices, ProtectionAction.BlockNodes),
            actions);
        Assert.AreEqual(NodePhase.Blocked, world.Nodes);
        Assert.AreEqual(AudioProtectionState.Protected, world.RealServices);
    }

    [TestMethod]
    public void BlockWhenAlreadyProtectedOnlyBlocks()
    {
        var world = new World { RealServices = AudioProtectionState.Protected };

        CollectionAssert.AreEqual(Seq(ProtectionAction.ReadServices, ProtectionAction.BlockNodes), world.Run(ProtectionGoal.Block));
    }

    [TestMethod]
    public void BlockWithProtectionOffNeverTouchesTheServices()
    {
        var world = new World { Protect = false };

        CollectionAssert.AreEqual(Seq(ProtectionAction.BlockNodes), world.Run(ProtectionGoal.Block));
        Assert.AreEqual(AudioProtectionState.NotProtected, world.RealServices);
    }

    [TestMethod]
    public void AProtectionThatDoesNotTakeNeverHoldsUpTheBlock()
    {
        var world = new World { ProtectTakes = false };

        List<ProtectionAction> actions = world.Run(ProtectionGoal.Block);

        Assert.AreEqual(ProtectionAction.BlockNodes, actions[^1]);
        Assert.AreEqual(1, actions.Count(a => a == ProtectionAction.ProtectOn), "Tried once, not looped.");
    }

    [TestMethod]
    public void BlockWithAnUnknownServiceReadBlocksWithoutGuessing()
    {
        var world = new World { RealServices = AudioProtectionState.Unknown };

        CollectionAssert.AreEqual(Seq(ProtectionAction.ReadServices, ProtectionAction.BlockNodes), world.Run(ProtectionGoal.Block));
    }

    [TestMethod]
    [DataRow(nameof(NodePhase.Blocked))]
    [DataRow(nameof(NodePhase.Mixed))]
    [DataRow(nameof(NodePhase.Unknown))]
    public void BlockFromANodeStateThatIsNotAllowedSkipsTheServices(string phase)
    {
        NodePhase nodes = Enum.Parse<NodePhase>(phase);
        var world = new World { Nodes = nodes };

        List<ProtectionAction> actions = world.Run(ProtectionGoal.Block);

        Assert.DoesNotContain(ProtectionAction.ProtectOn, actions);
        Assert.DoesNotContain(ProtectionAction.ReadServices, actions);
        CollectionAssert.AreEqual(nodes == NodePhase.Blocked ? Seq() : Seq(ProtectionAction.BlockNodes), actions);
    }

    [TestMethod]
    public void AllowEnablesTheNodesFirstThenPutsHandsfreeBackOff()
    {
        var world = new World { Nodes = NodePhase.Blocked, RealServices = AudioProtectionState.Protected, ServicesAfterAllow = AudioProtectionState.NotProtected };

        List<ProtectionAction> actions = world.Run(ProtectionGoal.Allow);

        CollectionAssert.AreEqual(
            Seq(ProtectionAction.AllowNodes, ProtectionAction.ReadServices, ProtectionAction.ProtectOn, ProtectionAction.ReadServices),
            actions);
        Assert.AreEqual(NodePhase.Allowed, world.NodesDuringServiceChange);
        Assert.AreEqual(AudioProtectionState.Protected, world.RealServices);
    }

    [TestMethod]
    public void AllowWhenHandsfreeStayedOffChangesNoService()
    {
        var world = new World { Nodes = NodePhase.Blocked, ServicesAfterAllow = AudioProtectionState.Protected };

        CollectionAssert.AreEqual(Seq(ProtectionAction.AllowNodes, ProtectionAction.ReadServices), world.Run(ProtectionGoal.Allow));
    }

    [TestMethod]
    public void AnAllowThatDoesNotTakeChangesNoService()
    {
        var world = new World { Nodes = NodePhase.Blocked, AllowTakes = false };

        CollectionAssert.AreEqual(Seq(ProtectionAction.AllowNodes), world.Run(ProtectionGoal.Allow));
    }

    [TestMethod]
    public void AllowWithProtectionOffAndNothingStoredLeavesTheServices()
    {
        var world = new World { Nodes = NodePhase.Blocked, Protect = false, ServicesAfterAllow = AudioProtectionState.Protected };

        CollectionAssert.AreEqual(Seq(ProtectionAction.AllowNodes), world.Run(ProtectionGoal.Allow));
    }

    [TestMethod]
    public void TurningProtectionOnWhileBlockedStoresItAndTheNextAllowAppliesIt()
    {
        var world = new World { Nodes = NodePhase.Blocked, RealServices = AudioProtectionState.NotProtected, Protect = true };

        CollectionAssert.AreEqual(Seq(ProtectionAction.StoreIntent), world.Run(ProtectionGoal.SetProtection));
        Assert.IsTrue(world.IntentPending);
        Assert.AreEqual(AudioProtectionState.NotProtected, world.RealServices, "Nothing changes while blocked.");

        List<ProtectionAction> allow = world.Run(ProtectionGoal.Allow);

        CollectionAssert.AreEqual(
            Seq(ProtectionAction.AllowNodes, ProtectionAction.ReadServices, ProtectionAction.ProtectOn, ProtectionAction.ReadServices),
            allow);
        Assert.AreEqual(AudioProtectionState.Protected, world.RealServices);
    }

    [TestMethod]
    public void TurningProtectionOffWhileBlockedIsAppliedOnTheNextAllow()
    {
        var world = new World { Nodes = NodePhase.Blocked, RealServices = AudioProtectionState.Protected, Protect = false };

        CollectionAssert.AreEqual(Seq(ProtectionAction.StoreIntent), world.Run(ProtectionGoal.SetProtection));

        world.ServicesAfterAllow = AudioProtectionState.Protected;
        List<ProtectionAction> allow = world.Run(ProtectionGoal.Allow);

        CollectionAssert.AreEqual(
            Seq(ProtectionAction.AllowNodes, ProtectionAction.ReadServices, ProtectionAction.ProtectOff, ProtectionAction.ReadServices),
            allow);
        Assert.AreEqual(AudioProtectionState.NotProtected, world.RealServices);
    }

    [TestMethod]
    public void TurningProtectionOnWhileAllowedAppliesItAtOnce()
    {
        var world = new World();

        CollectionAssert.AreEqual(
            Seq(ProtectionAction.ReadServices, ProtectionAction.ProtectOn, ProtectionAction.ReadServices),
            world.Run(ProtectionGoal.SetProtection));
    }

    [TestMethod]
    public void TurningProtectionOnWhenAlreadyProtectedOnlyReads()
    {
        var world = new World { RealServices = AudioProtectionState.Protected };

        CollectionAssert.AreEqual(Seq(ProtectionAction.ReadServices), world.Run(ProtectionGoal.SetProtection));
    }

    [TestMethod]
    public void ReverifyPutsProtectionBackOnlyWhenItReverted()
    {
        var reverted = new World { RealServices = AudioProtectionState.NotProtected };
        var intact = new World { RealServices = AudioProtectionState.Protected };
        var unknown = new World { RealServices = AudioProtectionState.Unknown };
        var off = new World { Protect = false };

        CollectionAssert.AreEqual(
            Seq(ProtectionAction.ReadServices, ProtectionAction.ProtectOn, ProtectionAction.ReadServices),
            reverted.Run(ProtectionGoal.Reverify));
        CollectionAssert.AreEqual(Seq(ProtectionAction.ReadServices), intact.Run(ProtectionGoal.Reverify));
        CollectionAssert.AreEqual(Seq(ProtectionAction.ReadServices), unknown.Run(ProtectionGoal.Reverify));
        CollectionAssert.AreEqual(Seq(), off.Run(ProtectionGoal.Reverify));
    }

    [TestMethod]
    public void PartialProtectionIsCompleted()
    {
        var world = new World { RealServices = AudioProtectionState.Partial };

        CollectionAssert.AreEqual(
            Seq(ProtectionAction.ReadServices, ProtectionAction.ProtectOn, ProtectionAction.ReadServices),
            world.Run(ProtectionGoal.Reverify));
    }

    // Every goal from every starting point: no service change while a node is disabled or unknown, every
    // sequence ends, each device change runs at most once, and a block always ends with the nodes blocked.
    [TestMethod]
    public void NoServiceChangeEverRunsUnlessTheNodesAreAllowed()
    {
        int sequences = 0;
        foreach (ProtectionGoal goal in Enum.GetValues<ProtectionGoal>())
        foreach (NodePhase nodes in Enum.GetValues<NodePhase>())
        foreach (AudioProtectionState services in Enum.GetValues<AudioProtectionState>())
        foreach (AudioProtectionState afterAllow in Enum.GetValues<AudioProtectionState>())
        foreach (bool protect in new[] { true, false })
        foreach (bool pending in new[] { true, false })
        foreach (bool takes in new[] { true, false })
        foreach (bool allowTakes in new[] { true, false })
        {
            var world = new World
            {
                Nodes = nodes,
                RealServices = services,
                Protect = protect,
                IntentPending = pending,
                ServicesAfterAllow = afterAllow,
                ProtectTakes = takes,
                AllowTakes = allowTakes,
            };
            string label = goal + " " + nodes + " " + services + " protect " + protect + " pending " + pending;

            List<ProtectionAction> actions = world.Run(goal);
            sequences++;

            Assert.AreEqual(NodePhase.Allowed, world.NodesDuringServiceChange, label);
            Assert.IsLessThanOrEqualTo(1, actions.Count(a => a is ProtectionAction.ProtectOn or ProtectionAction.ProtectOff), label);
            Assert.IsLessThanOrEqualTo(1, actions.Count(a => a is ProtectionAction.BlockNodes or ProtectionAction.AllowNodes), label);
            if (goal == ProtectionGoal.Block)
            {
                Assert.AreEqual(NodePhase.Blocked, world.Nodes, label);
                Assert.DoesNotContain(ProtectionAction.ProtectOff, actions, label);
            }

            if (goal is ProtectionGoal.Block or ProtectionGoal.Reverify)
            {
                Assert.DoesNotContain(ProtectionAction.AllowNodes, actions, label);
            }
        }

        Assert.IsGreaterThan(1000, sequences);
    }

    [TestMethod]
    public void TheNodeMappingTreatsAnyDisabledNodeAsNoServiceChange()
    {
        Assert.AreEqual(NodePhase.Allowed, ProtectionPolicy.PhaseOf(BlockState.Allowed));
        Assert.AreEqual(NodePhase.Blocked, ProtectionPolicy.PhaseOf(BlockState.Blocked));
        Assert.AreEqual(NodePhase.Mixed, ProtectionPolicy.PhaseOf(BlockState.Mixed));
        foreach (BlockState state in new[] { BlockState.Unknown, BlockState.NotFound, BlockState.NotSetUp })
        {
            Assert.AreEqual(NodePhase.Unknown, ProtectionPolicy.PhaseOf(state));
        }

        Assert.IsTrue(ProtectionPolicy.MayChangeServices(NodePhase.Allowed));
        Assert.IsFalse(ProtectionPolicy.MayChangeServices(NodePhase.Mixed));
        Assert.IsTrue(ProtectionPolicy.IsDeviceChange(ProtectionAction.ProtectOn));
        Assert.IsFalse(ProtectionPolicy.IsDeviceChange(ProtectionAction.ReadServices));
        Assert.IsFalse(ProtectionPolicy.IsDeviceChange(ProtectionAction.StoreIntent));
    }
}
