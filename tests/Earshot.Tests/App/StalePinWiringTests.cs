using System.Windows.Forms;
using Earshot.App;
using Earshot.Bluetooth;
using Earshot.Contracts;
using Earshot.Repair;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase1;
using Earshot.Tests.Phase4;
using Earshot.Tests.Repair;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PairedDevice = Earshot.Repair.PairedDevice;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.App;

// What the tray does with a pinned pair that Windows no longer holds, and with a PC that has no AirPods paired at all: at start,
// before set-up, and when asked. It never adopts another pair by itself and never unpairs anything; it says what it found and
// offers Pair AirPods or the device picker.
[TestClass]
public sealed class StalePinWiringTests
{
    private static PairAirPodsServices Services(FixRig rig, TimeSpan? stalePinBudget = null) => new()
    {
        StalePinBudget = stalePinBudget ?? StalePin.DefaultBudget,
        Api = rig.Pairing,
        Nodes = rig.Nodes,
        Prompter = rig.Prompter,
        UiFactory = _ => rig.Ui,
        Radio = new FakeRadioCheck(Pairing.PairingSessionTests.Radio(RadioCheckState.Ok)),
        Options = rig.Host().Options,
        LockName = @"Local\Earshot.Tests.Stale." + Guid.NewGuid().ToString("N"),
    };

    private static void PinTheRigsPair(EarshotSettings s)
    {
        s.PinnedContainerId = RecordedNodes.AirPodsContainer;
        s.PinnedAddress = RecordedNodes.AirPodsAddress;
    }

    private static void Unpin(EarshotSettings s)
    {
        s.PinnedContainerId = Guid.Empty;
        s.PinnedAddress = "";
    }

    // The owner's pair removed in Windows: its nodes are left behind but not present, and the paired list does not hold it.
    private static void RemoveThePinnedPair(FixRig rig)
    {
        foreach (string id in RecordedNodes.AirPodsTargets)
        {
            rig.Gate.Nodes[id].Present = false;
        }
    }

    private static void AddASecondPairedPair(FixRig rig)
    {
        rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Concat(FixRig.SecondAirPodsNodes()));
        rig.Pairing.Paired.Add(new PairedDevice("paired-second", "AirPods Pro", FixRig.SecondAirPodsAddress, FixRig.SecondAirPodsContainer));
    }

    private static FixRig NothingPairedRig() => new(new FakeNodeApi(Array.Empty<FakeNode>()), pinned: false);

    private static CardContent Last(TrayHarness tray) => tray.Cards.Shown[^1].Content;

    [TestMethod]
    public void WithNoAirPodsPairedTheCardOffersPairAirPodsOnceAndItsButtonStartsPairing()
    {
        StaThread.Run(() =>
        {
            using FixRig rig = NothingPairedRig();
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: Services(rig), settings: Unpin);

            CardContent card = Last(tray);
            Assert.AreEqual("No AirPods are paired with this PC.", card.Status);
            Assert.AreEqual("Pair AirPods", card.Action?.Label);
            Assert.IsTrue(card.StaysUp, "The way forward after an install, often shown at sign-in, stays until it is used.");
            int shown = tray.Cards.Shown.Count;

            tray.Context.StartPinnedPairCheck("again");
            tray.PumpUntilIdle();

            Assert.HasCount(shown, tray.Cards.Shown, "Once per run.");

            rig.Prompter.SayCancel();
            card.Action!.OnClick();
            tray.PumpUntilIdle();

            Assert.HasCount(1, rig.Prompter.Asked, "The button started Pair AirPods, which asked its first question.");
        });
    }

    [TestMethod]
    public void APinnedPairThatIsStillThereNeedsNoCard()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: Services(rig), settings: PinTheRigsPair);

            Assert.IsEmpty(tray.Cards.Shown);
        });
    }

    [TestMethod]
    public void NothingPinnedWithAPairInTheTreeNeedsNoCard()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: Services(rig), settings: Unpin);

            Assert.IsEmpty(tray.Cards.Shown, "A pair is there to be pinned on its first sighting.");
        });
    }

    [TestMethod]
    public void AStalePinWithNoOtherPairForgetsTheReadingsAndOffersPairAirPods()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            RemoveThePinnedPair(rig);
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: Services(rig), settings: PinTheRigsPair);

            Assert.AreEqual("No AirPods are paired with this PC.", Last(tray).Status);
            Assert.AreEqual("Pair AirPods", Last(tray).Action?.Label);
            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.Contains("pinned AirPods: Stale", StringComparison.Ordinal)));
            Assert.AreEqual(RecordedNodes.AirPodsContainer, tray.Settings.Current.PinnedContainerId, "Nothing is adopted or dropped behind the person's back.");
        });
    }

    // A Pair AirPods pressed while the stale-pin check read the paired list: the check says nothing once it has read it, so the
    // device picker card does not show over the pairing's own, and nothing chosen on it races the pairing's adoption.
    [TestMethod]
    public void AStalePinCheckThatFindsAPairingStartedSaysNothing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            RemoveThePinnedPair(rig);
            AddASecondPairedPair(rig);
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: Services(rig), settings: PinTheRigsPair);
            tray.Cards.Shown.Clear();
            rig.Prompter.SayOk();
            bool armed = true;
            rig.Pairing.OnListPaired = () =>
            {
                if (armed)
                {
                    armed = false;
                    tray.Context.Menu.Refresh();
                    tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PairAirPods).PerformClick();
                }
            };

            tray.Context.StartPinnedPairCheck("new device");
            tray.PumpUntilIdle();

            Assert.IsFalse(armed, "The pairing started while the paired list was read.");
            Assert.IsFalse(tray.Cards.Shown.Any(c => c.Content.Status == PairAirPodsText.StaleChosen), string.Join(" | ", tray.Cards.Statuses));
            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.Contains("a pairing started while it looked", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public void AStalePinWithAnotherPairAsksWhichOneAndAdoptsNothing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            RemoveThePinnedPair(rig);
            AddASecondPairedPair(rig);
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: Services(rig), settings: PinTheRigsPair);

            CardContent card = Last(tray);
            Assert.AreEqual("Your AirPods were removed in Windows. Choose which pair Earshot should use.", card.Status);
            Assert.AreEqual("Choose AirPods", card.Action?.Label);
            Assert.IsTrue(card.StaysUp, "Often shown at sign-in; until a pair is chosen none is protected.");
            Assert.AreEqual(RecordedNodes.AirPodsContainer, tray.Settings.Current.PinnedContainerId, "Always ask: the other pair is not adopted by itself.");
            Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("unpair:", StringComparison.Ordinal)));
        });
    }

    // At start the tray's own card about a removed pin answers for the pairs, so the coordinator's card about unprotected
    // AirPods does not replace it. Before, both raced and whichever came last stayed.
    [TestMethod]
    public void AtStartTheStalePinCardIsNotReplacedByTheNotProtectedCard()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            RemoveThePinnedPair(rig);
            AddASecondPairedPair(rig);
            var protection = new FakeProtectionCheck { Reading = new ProtectionReading(true, true, 1) };
            using var tray = new TrayHarness(
                snapshot: NoDevice(), pairAirPods: Services(rig), settings: PinTheRigsPair,
                coordinatorServices: new CoordinatorServices { Protection = protection });
            tray.PumpUntilIdle();

            Assert.AreEqual(PairAirPodsText.StaleChosen, Last(tray).Status);
            Assert.IsFalse(tray.Cards.Shown.Any(c => c.Content.Status == BlockCoordinator.NotProtectedMessage), string.Join(" | ", tray.Cards.Shown.Select(c => c.Content.Status)));
        });
    }

    // With the pin still paired, the start look shows nothing and the coordinator's check runs as before.
    [TestMethod]
    public void AtStartTheNotProtectedCardStillShowsWhenTheLookSaidNothing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            var protection = new FakeProtectionCheck { Reading = new ProtectionReading(true, true, 1) };
            using var tray = new TrayHarness(
                snapshot: NoDevice(), pairAirPods: Services(rig), settings: PinTheRigsPair,
                coordinatorServices: new CoordinatorServices { Protection = protection });
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Cards.Shown.Any(c => c.Content.Status == BlockCoordinator.NotProtectedMessage), string.Join(" | ", tray.Cards.Shown.Select(c => c.Content.Status)));
        });
    }

    // A paired list that does not answer ends the look within its budget, saying nothing, so neither the start checks nor a
    // failed connect's card wait on it.
    [TestMethod]
    public void APairedListThatDoesNotAnswerEndsTheLookWithinItsBudget()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Pairing.HangOnList = true;
            PairAirPodsServices services = Services(rig, TimeSpan.FromMilliseconds(100));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: services, settings: PinTheRigsPair);
            while (!tray.Log.Has(LogLevel.Info, "Pairing check (start)") && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                tray.PumpUntilIdle();
                Thread.Sleep(10);
            }

            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Pairing check (start)"), string.Join(" | ", tray.Log.Entries.Select(e => e.Message)));
            Assert.IsEmpty(tray.Cards.Shown);
        });
    }

    [TestMethod]
    public void AReadThatFailedIsNeverStaleSoNothingIsShown()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            RemoveThePinnedPair(rig);
            rig.Pairing.ListSteps.Add(new StepOutcome("pair-list", false, unchecked((int)0x80004005), "E_FAIL", null));
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: Services(rig), settings: PinTheRigsPair);

            Assert.IsEmpty(tray.Cards.Shown);
        });
    }

    [TestMethod]
    public void SetUpAfterAPairingInWindowsDoesNotSendTheStalePinToTheGate()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            RemoveThePinnedPair(rig);
            AddASecondPairedPair(rig);
            using var tray = new TrayHarness(
                snapshot: NoDevice(), pairAirPods: Services(rig), settings: PinTheRigsPair, arrange: h => h.Block.Status = Block(BlockState.NotSetUp));
            tray.Cards.Shown.Clear();

            tray.ClickMenu(MenuModel.SetUpEarshot);
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Block.Calls.Contains("setup"), "No administrator prompt for a pair that is gone.");
            Assert.AreEqual("Choose AirPods", Last(tray).Action?.Label);
        });
    }

    [TestMethod]
    public void AFailedConnectToAPairThatIsNoLongerPairedOffersPairAirPodsNotFixOrConnectInWindows()
    {
        StaThread.Run(() =>
        {
            // The harness pins the pair its snapshot shows; the rig's Windows holds a different one, so the pin is stale.
            using var rig = new FixRig();
            RemoveThePinnedPair(rig);
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));
            tray.Cards.Shown.Clear();
            tray.Connection.OnConnect = _ => Task.FromResult(new ConnectResult(
                ConnectOutcome.AttemptedTimedOut, "Still connecting. Check your AirPods.", [StepOutcomes.FromHResult("ks-reconnect:src", 0)]));

            tray.Context.OnIconMouseClick(null, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            tray.PumpUntilIdle();

            CardContent card = Last(tray);
            Assert.AreEqual("No AirPods are paired with this PC.", card.Status);
            Assert.AreEqual("Pair AirPods", card.Action?.Label);
            Assert.IsFalse(tray.Cards.Shown.Any(c => c.Content.Action?.Label is "Fix connection" or "Connect in Windows"));
        });
    }

    [TestMethod]
    public void SetUpWithAPairThatIsStillThereGoesAhead()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            using var tray = new TrayHarness(
                snapshot: NoDevice(), pairAirPods: Services(rig), settings: PinTheRigsPair, arrange: h => h.Block.Status = Block(BlockState.NotSetUp));

            tray.ClickMenu(MenuModel.SetUpEarshot);
            tray.PumpUntilIdle();

            CollectionAssert.Contains(tray.Block.Calls, "setup");
        });
    }
}
