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

    private static PairAirPodsServices Services(FixRig rig, RadioCheckState radio = RadioCheckState.Ok, string? lockName = null, IPairRecoveryMarker? marker = null) => new()
    {
        Marker = marker ?? NoPairRecoveryMarker.Instance,
        Api = rig.Pairing,
        Nodes = rig.Nodes,
        Prompter = rig.Prompter,
        UiFactory = _ => rig.Ui,
        Radio = new FakeRadioCheck(PairingSessionTests.Radio(radio)),
        Options = rig.Host().Options,
        LockName = lockName ?? @"Local\Earshot.Tests.Pair." + Guid.NewGuid().ToString("N"),
    };

    private static MouseEventArgs Press(MouseButtons button) => new(button, clicks: 1, x: 0, y: 0, delta: 0);

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
    public void ConnectInWindowsBeforeSetUpWithNothingPairedStartsPairAirPods()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            var paired = new FakePairedAirPodsCheck { Reading = new PairedAirPodsReading(true, 0) };
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected),
                pairAirPods: Services(rig),
                coordinatorServices: new CoordinatorServices { PairedAirPods = paired },
                arrange: h => h.Block.Status = Block(BlockState.NotSetUp));
            tray.Connection.OnConnect = _ => Task.FromResult(new ConnectResult(
                ConnectOutcome.AttemptedTimedOut, "Still connecting. Check your AirPods.", [StepOutcomes.FromHResult("ks-reconnect:src", 0)]));
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            tray.Cards.Shown[^1].Content.Action!.OnClick();
            tray.PumpUntilIdle();

            Assert.AreEqual(1, paired.Reads);
            Assert.HasCount(1, rig.Prompter.Asked, "Pair AirPods asked its first question.");
            Assert.AreEqual(FixRig.NewAirPodsContainer, tray.Settings.Current.PinnedContainerId);
        });
    }

    [TestMethod]
    public void AClickWithNothingPairedSaysSoWithAPairAirPodsButtonThatStartsPairing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig(new FakeNodeApi(Array.Empty<FakeNode>()), pinned: false);
            rig.Prompter.SayCancel();
            using var tray = new TrayHarness(
                pairAirPods: Services(rig),
                settings: s =>
                {
                    s.PinnedContainerId = Guid.Empty;
                    s.PinnedAddress = "";
                });
            tray.Cards.Shown.Clear();

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            CardContent card = tray.Cards.Shown[^1].Content;
            Assert.AreEqual("No AirPods are paired with this PC.", card.Status);
            Assert.AreEqual("Pair AirPods", card.Action?.Label);
            card.Action!.OnClick();
            tray.PumpUntilIdle();
            Assert.HasCount(1, rig.Prompter.Asked);
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

    // The time between the pairing and the adoption is covered by a marker written before anything is paired.
    [TestMethod]
    public void TheRecoveryMarkerIsThereWhileThePairIsPairedAndGoneOnceItIsAdopted()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            var marker = new FakePairRecoveryMarker();
            bool markedWhenPaired = false;
            Action<string> paired = rig.Pairing.OnPaired!;
            rig.Pairing.OnPaired = id =>
            {
                markedWhenPaired = marker.Exists();
                paired(id);
            };
            rig.Prompter.SayOk();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, marker: marker));

            Click(tray);

            Assert.IsTrue(markedWhenPaired, "The marker was written before the pairing.");
            Assert.AreEqual(FixRig.NewAirPodsContainer, tray.Settings.Current.PinnedContainerId);
            Assert.IsFalse(marker.Exists(), "Cleared once the pair was adopted: " + string.Join(", ", marker.Calls));
        });
    }

    [TestMethod]
    public void APairingThatFailedClearsTheMarker()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice(UnpairedId, "AirPods Pro", FixRig.NewAirPodsAddress, true, 6, null)));
            rig.Pairing.PairResult = new PairingCallResult("Failed", PairReasons.Failed, false, null);
            rig.Prompter.SayOk();
            var marker = new FakePairRecoveryMarker();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, marker: marker));

            Click(tray);

            CollectionAssert.Contains(marker.Calls, "write");
            Assert.IsFalse(marker.Exists());
        });
    }

    // The gate refused the new pair, so it is paired and not adopted: the marker stays for the next start to offer it again.
    [TestMethod]
    public void AnAdoptionThatDidNotHappenKeepsTheMarker()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            var marker = new FakePairRecoveryMarker();
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, marker: marker),
                arrange: h => h.Block.OnSetDevice = _ => Task.FromResult(ControllerResult.Fail("refused", [])));

            Click(tray);

            Assert.AreNotEqual(FixRig.NewAirPodsContainer, tray.Settings.Current.PinnedContainerId);
            Assert.IsTrue(marker.Exists());
        });
    }

    // A tray that stopped between the pairing and the adoption: at the next start the new pair is paired and not pinned, so the
    // device picker is offered for it. Nothing is adopted by itself.
    [TestMethod]
    public void AMarkerLeftAtStartWithAnUnpinnedPairOffersTheDevicePicker()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Concat(FixRig.NewAirPodsNodes()));
            rig.Pairing.Paired.Add(new PairedDevice("paired-new", "AirPods Pro", FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer));
            var marker = new FakePairRecoveryMarker { Present = true };
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: Services(rig, marker: marker), settings: s =>
            {
                s.PinnedContainerId = RecordedNodes.AirPodsContainer;
                s.PinnedAddress = RecordedNodes.AirPodsAddress;
            });

            CardContent card = tray.Cards.Shown[^1].Content;
            Assert.AreEqual(PairAirPodsText.RecoverChoose, card.Status);
            Assert.AreEqual(PairAirPodsText.ChooseButton, card.Action?.Label);
            Assert.AreEqual(RecordedNodes.AirPodsContainer, tray.Settings.Current.PinnedContainerId, "Nothing was adopted by itself.");
            Assert.IsTrue(marker.Exists(), "Kept until a pair is chosen.");
            Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("unpair:", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public void AMarkerLeftAtStartWithNoOtherPairIsClearedAndSaysNothing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            var marker = new FakePairRecoveryMarker { Present = true };
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: Services(rig, marker: marker), settings: s =>
            {
                s.PinnedContainerId = RecordedNodes.AirPodsContainer;
                s.PinnedAddress = RecordedNodes.AirPodsAddress;
            });

            Assert.IsEmpty(tray.Cards.Shown);
            Assert.IsFalse(marker.Exists());
        });
    }

    [TestMethod]
    public void TheRealMarkerIsWrittenReadAndClearedAndNamesNoDevice()
    {
        using var folder = new TempFolder();
        var marker = new PairRecoveryMarker(folder.Path, TimeProvider.System);

        Assert.IsFalse(marker.Exists());
        Assert.IsTrue(marker.Write().Ok);
        Assert.IsTrue(marker.Exists());
        string text = File.ReadAllText(Path.Combine(folder.Path, PairRecoveryMarker.FileName));
        StringAssert.Contains(text, "OpenedUtc");
        Assert.IsFalse(text.Contains(FixRig.NewAirPodsAddress, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(marker.Clear().Ok);
        Assert.IsFalse(marker.Exists());
        Assert.IsTrue(marker.Clear().Ok, "Clearing a marker that is not there is not a failure.");
    }
}

internal sealed class FakePairRecoveryMarker : IPairRecoveryMarker
{
    public bool Present { get; set; }

    public List<string> Calls { get; } = [];

    public StepOutcome Write()
    {
        Calls.Add("write");
        Present = true;
        return new StepOutcome("pair-marker-write", true, 0, "ERROR_SUCCESS", null);
    }

    public StepOutcome Clear()
    {
        Calls.Add("clear");
        Present = false;
        return new StepOutcome("pair-marker-clear", true, 0, "ERROR_SUCCESS", null);
    }

    public bool Exists() => Present;
}
