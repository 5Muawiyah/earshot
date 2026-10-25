using System.Windows.Forms;
using Earshot.App;
using Earshot.Bluetooth;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Earshot.Infra;
using Earshot.Pairing;
using Earshot.Repair;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Pairing;
using Earshot.Tests.Phase1;
using Earshot.Tests.Phase4;
using Earshot.Tests.Repair;
using Earshot.Tray;
using PairedDevice = Earshot.Repair.PairedDevice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.App;

// Pair AirPods from the tray: the radio is read first, the person presses Look for AirPods, the shared pairing session runs, and
// a pair that passes the model check is pinned and then set up. Everything at a boundary is a fake: no pairing, no node change,
// no elevation, no window.
[TestClass]
public sealed class PairAirPodsTests
{
    private const string UnpairedId = "unpaired-airpods";

    private static void ArrangePairs(FixRig rig, bool airPods = true)
    {
        rig.Pairing.OnPaired = _ =>
        {
            FakeNode[] arrived = airPods
                ? FixRig.NewAirPodsNodes()
                :
                [
                    new FakeNode(@"BTHENUM\DEV_" + FixRig.NewAirPodsAddress + @"\b&1a2b3c4d&0&BLUETOOTHDEVICE_" + FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer, "Speaker"),
                ];
            rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Concat(arrived));
            rig.Pairing.Paired.Add(new PairedDevice("paired-new", "AirPods Pro", FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer));
        };
        rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice(UnpairedId, "AirPods Pro", FixRig.NewAirPodsAddress, true, 6, null)));
    }

    private static PairAirPodsServices Services(FixRig rig, RadioCheckState radio = RadioCheckState.Ok, string? lockName = null) => new()
    {
        Api = rig.Pairing,
        Nodes = rig.Nodes,
        Prompter = rig.Prompter,
        UiFactory = _ => rig.Ui,
        Radio = new FakeRadioCheck(PairingSessionTests.Radio(radio)),
        Options = rig.Host().Options,
        LockName = lockName ?? @"Local\Earshot.Tests.Pair." + Guid.NewGuid().ToString("N"),
    };

    private static void Click(TrayHarness tray)
    {
        tray.Context.Menu.Refresh();
        tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PairAirPods).PerformClick();
        tray.PumpUntilIdle();
    }

    [TestMethod]
    public void APairingThatPassesTheModelCheckPinsThenRunsSetUp()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            string? pinnedAtSetUp = null;
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected),
                time: TimeProvider.System,
                pairAirPods: Services(rig),
                coordinatorServices: new CoordinatorServices { History = history },
                arrange: h =>
                {
                    h.Block.Status = Block(BlockState.NotSetUp);
                    h.Block.OnRunSetup = _ =>
                    {
                        pinnedAtSetUp = h.Settings.Current.PinnedAddress + "|" + h.Settings.Current.PinnedContainerId;
                        return Task.FromResult(ControllerResult.Ok("Done"));
                    };
                });

            Click(tray);

            Assert.AreEqual(FixRig.NewAirPodsAddress + "|" + FixRig.NewAirPodsContainer, pinnedAtSetUp, "Set-up ran after the new pair was pinned.");
            Assert.AreEqual(1, tray.Block.Calls.Count(c => c == "setup"), "One set-up, so one administrator prompt.");
            Assert.HasCount(1, rig.Prompter.Asked, "One question: Look for AirPods. The pick is the other.");
            StringAssert.Contains(rig.Prompter.Asked[0].Body, PairingText.TurnOffIPhoneBluetooth);
            StringAssert.Contains(rig.Prompter.Asked[0].Body, PairingText.CaseFront);
            StringAssert.Contains(rig.Prompter.Asked[0].Body, PairingText.CaseBack);
            Assert.AreEqual("Look for AirPods", rig.Prompter.Asked[0].OkText);
            ConnectHistoryEntry entry = history.Read().Single();
            Assert.AreEqual(ConnectHistoryActions.Pair, entry.Action);
            Assert.AreEqual(ConnectHistoryOutcomes.Paired, entry.Outcome);
            var stepNames = entry.Steps.Select(s => s.Name).ToList();
            CollectionAssert.Contains(stepNames, "adopt");
            CollectionAssert.Contains(stepNames, "setup");
            Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("unpair:", StringComparison.Ordinal)), "No automatic unpair.");
            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.Contains("previous pair's last readings and link are dropped", StringComparison.Ordinal)), "A different pair took the place of the pinned one.");
        });
    }

    [TestMethod]
    public void WhenSetUpAlreadyThePairIsGivenToTheGateAndNoSetUpRuns()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));

            Click(tray);

            CollectionAssert.Contains(tray.Block.Calls, "set-device:" + FixRig.NewAirPodsAddress);
            Assert.IsFalse(tray.Block.Calls.Contains("setup"));
            Assert.AreEqual(FixRig.NewAirPodsContainer, tray.Settings.Current.PinnedContainerId);
        });
    }

    [TestMethod]
    public void ADeviceThatFailsTheModelCheckIsNotAdoptedNorUnpaired()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig, airPods: false);
            rig.Prompter.SayOk();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig), arrange: h => h.Block.Status = Block(BlockState.NotSetUp));

            Click(tray);

            Assert.AreEqual(AirPodsContainer, tray.Settings.Current.PinnedContainerId, "The pin did not move.");
            Assert.IsEmpty(tray.Block.Calls.Where(c => c == "setup" || c.StartsWith("set-device", StringComparison.Ordinal)).ToList());
            Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("unpair:", StringComparison.Ordinal)), "Earshot leaves the pairing as it is.");
            Assert.AreEqual(PairingText.NotAirPods, tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void ARadioThatIsOffShowsItsLineAndButtonAndAsksNothing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, RadioCheckState.Off));

            Click(tray);

            CardContent card = tray.Cards.Shown[^1].Content;
            Assert.AreEqual(RadioLines.Off, card.Status);
            Assert.AreEqual(RadioLines.BluetoothSettingsButton, card.Action?.Label);
            Assert.IsEmpty(rig.Prompter.Asked, "The radio is read before anything is asked.");
            Assert.IsFalse(rig.Pairing.Calls.Contains("watch"), "No scan.");
        });
    }

    [TestMethod]
    public void CancellingTheFirstQuestionStartsNothing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayCancel();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));

            Click(tray);

            Assert.IsFalse(rig.Pairing.Calls.Contains("watch"));
            Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("pair:", StringComparison.Ordinal)));
            Assert.AreEqual(AirPodsContainer, tray.Settings.Current.PinnedContainerId);
        });
    }

    [TestMethod]
    public void AFixOrPairingAlreadyRunningSaysSoAndStartsNothing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            string name = @"Local\Earshot.Tests.Pair.Held." + Guid.NewGuid().ToString("N");
            // A mutex belongs to the thread that took it, and one whose thread ends is abandoned and taken over, so the other
            // run's thread stays alive while the tray tries.
            using var acquired = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            bool holding = false;
            var other = new Thread(() =>
            {
                using FixRunLock? held = FixRunLock.TryAcquire(name, new CapturingLog());
                holding = held is not null;
                acquired.Set();
                release.Wait();
            }) { IsBackground = true };
            other.Start();
            acquired.Wait();
            Assert.IsTrue(holding, "The other run holds the lock.");
            try
            {
                using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, lockName: name));

                Click(tray);

                Assert.AreEqual(FixRunLock.PairAlreadyRunningMessage, tray.Cards.Shown[^1].Content.Status);
                Assert.IsEmpty(rig.Prompter.Asked);
            }
            finally
            {
                release.Set();
                other.Join();
            }
        });
    }

    [TestMethod]
    public void SafeModeRefusesPairingAndTouchesNothing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            using var tray = new TrayHarness(safeMode: true, snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));

            Click(tray);

            Assert.IsEmpty(rig.Prompter.Asked);
            Assert.IsEmpty(rig.Pairing.Calls);
            Assert.AreEqual(MenuModel.SafeMode, tray.Cards.Shown[^1].Content.Status);
        });
    }
}
