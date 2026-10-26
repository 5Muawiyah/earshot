using System.Windows.Forms;
using Earshot.App;
using Earshot.Bluetooth;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Earshot.Infra;
using Earshot.Interop;
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

    private static readonly string[] LookSteps = ["look", "look-ms"];

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
            rig.Prompter.SayOk(); // Use these AirPods instead of the pinned ones, which are still paired.
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
            Assert.HasCount(2, rig.Prompter.Asked, "Two questions: Look for AirPods, and use these instead of the pinned pair. The pick is the other.");
            StringAssert.Contains(rig.Prompter.Asked[0].Body, PairingText.TurnOffIPhoneBluetooth);
            StringAssert.Contains(rig.Prompter.Asked[0].Body, PairingText.CaseFront);
            StringAssert.Contains(rig.Prompter.Asked[0].Body, PairingText.CaseBack);
            Assert.AreEqual("Look for AirPods", rig.Prompter.Asked[0].OkText);
            Assert.AreEqual(PairAirPodsText.ReplaceButton, rig.Prompter.Asked[1].OkText);
            ConnectHistoryEntry entry = history.Read().Single();
            Assert.AreEqual(ConnectHistoryActions.Pair, entry.Action);
            Assert.AreEqual(ConnectHistoryOutcomes.Paired, entry.Outcome);
            var stepNames = entry.Steps.Select(s => s.Name).ToList();
            CollectionAssert.Contains(stepNames, "replace");
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
            rig.Prompter.SayOk(); // Use these AirPods instead of the pinned ones, which are still paired.
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));

            Click(tray);

            CollectionAssert.Contains(tray.Block.Calls, "set-device:" + FixRig.NewAirPodsAddress);
            Assert.IsFalse(tray.Block.Calls.Contains("setup"));
            Assert.AreEqual(FixRig.NewAirPodsContainer, tray.Settings.Current.PinnedContainerId);
        });
    }

    // Earshot always asks before it takes on a different pair. Saying no keeps the pinned pair, adopts nothing, unpairs nothing,
    // says the new pair is not protected, clears the marker (the person decided) and records the answer.
    [TestMethod]
    public void APairingWhileAnotherPairIsPinnedAsksAndANoKeepsThePinnedPair()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayCancel();
            var marker = new FakePairRecoveryMarker();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig, marker: marker),
                coordinatorServices: new CoordinatorServices { History = history });

            Click(tray);

            Assert.HasCount(2, rig.Prompter.Asked);
            Assert.AreEqual(PairAirPodsText.ReplaceHeading, rig.Prompter.Asked[1].Heading);
            Assert.AreEqual(AirPodsContainer, tray.Settings.Current.PinnedContainerId, "The pinned pair stays the one Earshot uses.");
            Assert.IsFalse(tray.Block.Calls.Any(c => c.StartsWith("set-device:", StringComparison.Ordinal)), "The gate was not given the new pair.");
            Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("unpair:", StringComparison.Ordinal)), "No automatic unpair.");
            Assert.AreEqual(PairAirPodsText.KeptOtherPair, tray.Cards.Shown[^1].Content.Status);
            Assert.IsFalse(marker.Exists());
            ConnectHistoryEntry entry = history.Read().Single();
            HistoryStep replace = entry.Steps.Single(st => st.Name == "replace");
            Assert.AreEqual(NativeCodes.NotAttempted, replace.Code);
            Assert.AreEqual(nameof(FixAnswer.Cancel), replace.CodeName);
            Assert.IsFalse(entry.Steps.Any(st => st.Name == "adopt"), "Nothing was adopted, so there is no adopt step.");
        });
    }

    // With nothing valid pinned there is nothing to replace, so the pick is the only choice and no second question is asked.
    [TestMethod]
    public void APairingWithNothingPinnedAsksOnlyLookForAirPods()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: Services(rig), settings: s =>
            {
                s.PinnedContainerId = Guid.Empty;
                s.PinnedAddress = "";
            });

            Click(tray);

            Assert.HasCount(1, rig.Prompter.Asked);
            Assert.AreEqual(FixRig.NewAirPodsContainer, tray.Settings.Current.PinnedContainerId);
        });
    }

    // The pinned AirPods were removed in Windows first, as test 24 does: a different pair is still asked about. Before, the
    // question came only while the pinned pair was in the device tree, so this adopted with no question.
    [TestMethod]
    public void APairingAfterThePinnedPairWasRemovedStillAsksBeforeUsingTheNewOne()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Where(n => n.Container != RecordedNodes.AirPodsContainer));
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: Services(rig), settings: s =>
            {
                s.PinnedContainerId = RecordedNodes.AirPodsContainer;
                s.PinnedAddress = RecordedNodes.AirPodsAddress;
            });

            Click(tray);

            Assert.IsTrue(rig.Prompter.Asked.Any(q => q.Heading == PairAirPodsText.ReplaceHeading), "Asked before the different pair was used.");
            Assert.AreEqual(FixRig.NewAirPodsContainer, tray.Settings.Current.PinnedContainerId);
        });
    }

    // The question timed out: nobody decided, so the new pair is offered and the marker stays for the next start.
    [TestMethod]
    public void AReplaceQuestionThatTimedOutKeepsTheMarkerAndOffersThePair()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayTimedOut();
            var marker = new FakePairRecoveryMarker();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, marker: marker));

            Click(tray);

            Assert.AreEqual(AirPodsContainer, tray.Settings.Current.PinnedContainerId);
            Assert.IsTrue(marker.Exists(), "Kept: the question was not answered.");
            Assert.AreEqual(PairAirPodsText.LeftPaired, tray.Cards.Shown[^1].Content.Status);
            Assert.AreEqual(PairAirPodsText.ChooseButton, tray.Cards.Shown[^1].Content.Action?.Label);
        });
    }

    // Set-up after a pairing is handed the run straight from the pairing. Before, the pairing let it go and set-up looked at the
    // pin first, so a Fix or an update pressed in between could start.
    [TestMethod]
    public void SetUpAfterAPairingTakesTheRunWithNoGapInBetween()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();
            bool paired = false;
            var unclaimed = new List<string>();
            TrayHarness? running = null;
            Action<string> onPaired = rig.Pairing.OnPaired!;
            rig.Pairing.OnPaired = id =>
            {
                onPaired(id);
                paired = true;
            };
            rig.Pairing.OnListPaired = () =>
            {
                if (paired && running?.Context.ElevatedRunInProgress is null)
                {
                    unclaimed.Add("list-paired with no run claimed");
                }
            };
            string? runAtSetUp = null;
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig),
                arrange: h =>
                {
                    h.Block.Status = Block(BlockState.NotSetUp);
                    h.Block.OnRunSetup = _ =>
                    {
                        runAtSetUp = running?.Context.ElevatedRunInProgress;
                        return Task.FromResult(ControllerResult.Ok("Done"));
                    };
                });
            running = tray;

            Click(tray);

            Assert.IsEmpty(unclaimed, string.Join(", ", unclaimed));
            Assert.AreEqual(1, tray.Block.Calls.Count(c => c == "setup"));
            Assert.IsNotNull(runAtSetUp);
            Assert.IsNull(tray.Context.ElevatedRunInProgress, "The run is let go once set-up is done.");
        });
    }

    // A start-time look at a marker that is still running when a pairing starts stops, rather than show the device picker over
    // the pairing's own result.
    [TestMethod]
    public void ARecoveryThatFindsAPairingUnderWayLeavesThePairsToIt()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();
            var marker = new FakePairRecoveryMarker();
            TrayHarness? running = null;
            rig.Prompter.OnAsk = q =>
            {
                if (q.Heading == PairAirPodsText.ReplaceHeading)
                {
                    running!.Context.StartPinnedPairCheck("start");
                }
            };
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, marker: marker),
                arrange: h => h.Block.OnSetDevice = async _ =>
                {
                    // The adoption takes a moment, so the recovery's own look ends while the pairing still runs.
                    await Task.Delay(500, CancellationToken.None);
                    return ControllerResult.Ok("Done");
                });
            running = tray;

            Click(tray);

            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.Contains("stops, because a pairing has started", StringComparison.Ordinal)),
                string.Join(" | ", tray.Log.Entries.Select(e => e.Message)));
            Assert.IsFalse(tray.Cards.Shown.Any(c => c.Content.Status == PairAirPodsText.RecoverChoose));
        });
    }

    // Exit while the pairing runs: the run still leaves its history entry, and the marker stays for the next start.
    [TestMethod]
    public void ExitDuringAPairingWritesItsHistoryEntryAndKeepsTheMarker()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Pairing.HangOnPair = true;
            rig.Prompter.SayOk();
            var marker = new FakePairRecoveryMarker();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig, marker: marker),
                coordinatorServices: new CoordinatorServices { History = history });
            rig.Ui.OnPick = () => tray.Ui.Post(_ => tray.ClickMenu(MenuModel.Exit), null);

            tray.Ui.Post(_ =>
            {
                tray.Context.Menu.Refresh();
                tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PairAirPods).PerformClick();
            }, null);
            Application.Run(tray.Context);

            ConnectHistoryEntry entry = history.Read().Single();
            Assert.AreEqual(ConnectHistoryOutcomes.Cancelled, entry.Outcome);
            Assert.AreEqual("closing", entry.Steps[0].Name);
            Assert.IsTrue(marker.Exists());
        });
    }

    // A marker an earlier run left (here, one the start-time look has not cleared) is that run's: a pairing that then pairs
    // nothing keeps it, so the next start still looks at what the earlier run left.
    [TestMethod]
    public void APairingThatPairsNothingKeepsAMarkerAnEarlierRunLeft()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice(UnpairedId, "AirPods Pro", FixRig.NewAirPodsAddress, true, 6, null)));
            rig.Pairing.PairResult = new PairingCallResult("Failed", PairReasons.Failed, false, null);
            rig.Prompter.SayOk();
            var marker = new FakePairRecoveryMarker();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, marker: marker));
            marker.Present = true;

            Click(tray);

            CollectionAssert.Contains(marker.Calls, "write");
            Assert.IsFalse(marker.Calls.Contains("clear"), "The earlier run's marker was cleared: " + string.Join(", ", marker.Calls));
            Assert.IsTrue(marker.Exists());
        });
    }

    // What is paired could not be read after a pairing that failed: the card says only that, and the marker stays.
    [TestMethod]
    public void AFailedPairingWithTheDevicesUnreadSaysSoAndKeepsTheMarker()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice(UnpairedId, "AirPods Pro", FixRig.NewAirPodsAddress, true, 6, null)));
            rig.Pairing.PairResult = new PairingCallResult("Failed", PairReasons.Failed, false, null);
            rig.Ui.OnPick = () => rig.Nodes.Inner.ListResult = CfgMgr32.CR_FAILURE;
            rig.Prompter.SayOk();
            var marker = new FakePairRecoveryMarker();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, marker: marker));

            Click(tray);

            Assert.AreEqual(PairAirPodsText.LeftUnread, tray.Cards.Shown[^1].Content.Status);
            Assert.IsTrue(marker.Exists());
        });
    }

    // A radio that stops the pairing pairs nothing, so no marker is written and its own line is the one shown.
    [TestMethod]
    public void ARadioThatStopsThePairingWritesNoMarker()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            var marker = new FakePairRecoveryMarker { WriteFails = true };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, RadioCheckState.Off, marker: marker));

            Click(tray);

            Assert.IsFalse(marker.Calls.Contains("write"));
            Assert.AreEqual(RadioLines.Off, tray.Cards.Shown[^1].Content.Status);
        });
    }

    // Something the run did not expect still leaves its one history entry, with the type and code of what stopped it.
    [TestMethod]
    public void AnUnexpectedThrowLeavesAHistoryEntryWithItsCode()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            var thrown = new InvalidOperationException("not expected");
            var marker = new FakePairRecoveryMarker { WriteThrows = thrown };
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig, marker: marker),
                coordinatorServices: new CoordinatorServices { History = history });

            Click(tray);

            ConnectHistoryEntry entry = history.Read().Single();
            Assert.AreEqual(ConnectHistoryOutcomes.Failed, entry.Outcome);
            Assert.AreEqual("unexpected", entry.Steps[0].Name);
            Assert.AreEqual(thrown.HResult, entry.Steps[0].Code);
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
            rig.Prompter.SayOk(); // Use these AirPods instead of the pinned ones, which are still paired.
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
            Assert.HasCount(2, rig.Prompter.Asked, "Pair AirPods asked its first question, then whether to use the new pair instead.");
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
            rig.Prompter.SayOk(); // Use these AirPods instead of the pinned ones, which are still paired.
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
            rig.Prompter.SayOk(); // Use these AirPods instead of the pinned ones, which are still paired.
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

    // A run that pinned the new pair in the tray but could not give it to the gate (the block status unread), then stopped: at the
    // next start the tray's pin is the new pair, so the recovery finds nothing to offer and clears the marker. Before, the
    // coordinator's own check had waited for the recovery and did not run either, so new AirPods the gate does not name stayed
    // paired and unblocked with nothing said. Now that check runs once the recovery has shown nothing.
    [TestMethod]
    public void AMarkerLeftAtStartWhoseNewPairTheGateDoesNotNameShowsTheNotProtectedCard()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Where(n => n.Container != RecordedNodes.AirPodsContainer).Concat(FixRig.NewAirPodsNodes()));
            rig.Pairing.Paired.Add(new PairedDevice("paired-new", "AirPods Pro", FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer));
            var marker = new FakePairRecoveryMarker { Present = true };
            PairAirPodsServices services = Services(rig, marker: marker);
            var protection = new FakeProtectionCheck { Reading = new ProtectionReading(true, true, 1) };
            using var tray = new TrayHarness(
                snapshot: NoDevice(),
                pairAirPods: services,
                coordinatorServices: new CoordinatorServices { Protection = protection, TrayPairing = services.RunFlag },
                settings: s =>
                {
                    s.PinnedContainerId = FixRig.NewAirPodsContainer;
                    s.PinnedAddress = FixRig.NewAirPodsAddress;
                });

            Assert.IsFalse(marker.Exists(), "No other pair was left to offer.");
            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.Contains("showed nothing, so the check for AirPods that Earshot does not protect runs now", StringComparison.Ordinal)),
                string.Join(" | ", tray.Log.Entries.Select(e => e.Message)));
            Assert.AreEqual(BlockCoordinator.NotProtectedMessage, tray.Cards.Shown[^1].Content.Status);
        });
    }

    // A marker found at start with the device list unreadable: new AirPods may be paired and unblocked and nothing can be told, so
    // the person is asked to choose rather than told nothing, and the marker stays.
    [TestMethod]
    public void AMarkerLeftAtStartWithTheDevicesUnreadAsksToChooseAndKeepsTheMarker()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Nodes.Inner.ListResult = CfgMgr32.CR_FAILURE;
            var marker = new FakePairRecoveryMarker { Present = true };
            using var tray = new TrayHarness(snapshot: NoDevice(), pairAirPods: Services(rig, marker: marker), settings: s =>
            {
                s.PinnedContainerId = RecordedNodes.AirPodsContainer;
                s.PinnedAddress = RecordedNodes.AirPodsAddress;
            });

            CardContent card = tray.Cards.Shown[^1].Content;
            Assert.AreEqual(PairAirPodsText.RecoverUnread, card.Status);
            Assert.AreEqual(PairAirPodsText.ChooseButton, card.Action?.Label);
            Assert.IsTrue(marker.Exists());
        });
    }

    // A pair Earshot could not look at keeps the marker, and the card offers the device picker instead of saying "not AirPods".
    [TestMethod]
    public void APairThatCouldNotBeCheckedKeepsTheMarkerAndOffersThePicker()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice(UnpairedId, "AirPods Pro", FixRig.NewAirPodsAddress, true, 6, null)));
            rig.Pairing.OnPaired = _ => rig.Pairing.Paired.Add(new PairedDevice("paired-new", "AirPods Pro", FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer));
            rig.Prompter.SayOk();
            var marker = new FakePairRecoveryMarker();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, marker: marker));

            Click(tray);

            CardContent card = tray.Cards.Shown[^1].Content;
            Assert.AreEqual(PairingText.Unverified, card.Status);
            Assert.AreEqual(PairAirPodsText.ChooseButton, card.Action?.Label);
            Assert.IsTrue(marker.Exists(), "Kept, so the next start looks again.");
        });
    }

    // The block status could not be read at the adoption, so the gate was not given the new pair: the new AirPods are not
    // blocked at rest. Before, the card then said "Paired. Earshot now uses these AirPods." over the warning, and the marker was
    // cleared.
    [TestMethod]
    public void AnAdoptionWithTheBlockStatusUnreadKeepsTheMarkerAndTheGateWarning()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk(); // Use these AirPods instead of the pinned ones, which are still paired.
            var marker = new FakePairRecoveryMarker();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, marker: marker));
            tray.Block.StatusFailure = new IOException("The status could not be read.");

            Click(tray);

            Assert.AreEqual(TrayContext.GateNotRepinnedMessage, tray.Cards.Shown[^1].Content.Status);
            Assert.IsFalse(tray.Cards.Shown.Any(c => c.Content.Status == PairAirPodsText.PairedAndUsing));
            Assert.IsTrue(marker.Exists());
        });
    }

    [TestMethod]
    public void NoPairingStartsWhenTheRecoveryMarkerCannotBeWritten()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            var marker = new FakePairRecoveryMarker { WriteFails = true };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, marker: marker));

            Click(tray);

            StringAssert.StartsWith(tray.Cards.Shown[^1].Content.Status, "Earshot could not save its note of this pairing");
            Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("pair:", StringComparison.Ordinal)), string.Join(", ", rig.Pairing.Calls));
        });
    }

    // Cancel in the Pair AirPods window is a cancel: before, the cancellation escaped the run and the person got "Something
    // went wrong", no history entry and a marker left behind.
    [TestMethod]
    public void CancelInThePairingWindowSaysCancelledWritesTheHistoryAndClearsTheMarker()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Pairing.HangOnPair = true;
            rig.Prompter.SayOk();
            var marker = new FakePairRecoveryMarker();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            PairAirPodsServices services = Services(rig, marker: marker);
            var cancelling = new PairAirPodsServices
            {
                Api = services.Api, Nodes = services.Nodes, Prompter = services.Prompter, Radio = services.Radio, Options = services.Options,
                LockName = services.LockName, Marker = marker,
                UiFactory = c =>
                {
                    rig.Ui.OnPick = c.Cancel;
                    return rig.Ui;
                },
            };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: cancelling, coordinatorServices: new CoordinatorServices { History = history });

            Click(tray);

            Assert.AreEqual(PairingText.Cancelled, tray.Cards.Shown[^1].Content.Status);
            Assert.IsFalse(tray.Log.Entries.Any(e => e.Level == LogLevel.Error), string.Join(" | ", tray.Log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message)));
            Assert.IsFalse(marker.Exists(), "Nothing new was paired, so the marker is cleared.");
            Assert.AreEqual(ConnectHistoryOutcomes.Cancelled, history.Read().Single().Outcome);
        });
    }

    // Cancel pressed when Windows had already paired the AirPods: they are offered, and the marker stays.
    [TestMethod]
    public void CancelAfterWindowsPairedOffersThePairAndKeepsTheMarker()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Pairing.HangOnPair = true;
            rig.Prompter.SayOk();
            var marker = new FakePairRecoveryMarker();
            PairAirPodsServices services = Services(rig, marker: marker);
            var cancelling = new PairAirPodsServices
            {
                Api = services.Api, Nodes = services.Nodes, Prompter = services.Prompter, Radio = services.Radio, Options = services.Options,
                LockName = services.LockName, Marker = marker,
                UiFactory = c =>
                {
                    rig.Ui.OnPick = () =>
                    {
                        rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Concat(FixRig.NewAirPodsNodes()));
                        c.Cancel();
                    };
                    return rig.Ui;
                },
            };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: cancelling);

            Click(tray);

            CardContent card = tray.Cards.Shown[^1].Content;
            Assert.AreEqual(PairAirPodsText.LeftPaired, card.Status);
            Assert.AreEqual(PairAirPodsText.ChooseButton, card.Action?.Label);
            Assert.IsTrue(marker.Exists());
        });
    }

    // A run that stopped before the session still leaves its one history entry, with the step it stopped at and its time.
    [TestMethod]
    public void DecliningLookForAirPodsLeavesAHistoryEntry()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Prompter.SayCancel();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig), coordinatorServices: new CoordinatorServices { History = history });

            Click(tray);

            ConnectHistoryEntry entry = history.Read().Single();
            Assert.AreEqual(ConnectHistoryOutcomes.Cancelled, entry.Outcome);
            CollectionAssert.AreEqual(LookSteps, entry.Steps.Select(s => s.Name).ToArray());
        });
    }

    // A Fix pressed while Pair AirPods runs would be refused by the lock with no window; the tray says why instead.
    [TestMethod]
    public void AFixPressedDuringAPairingIsRefusedWithACard()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            var launcher = new FakeFixLauncher();
            TrayHarness? running = null;
            rig.Prompter.OnAsk = _ => running!.Context.FixConnectionFromSettings();
            rig.Prompter.SayCancel();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));
            running = tray;
            tray.Context.FixLauncher = launcher;

            Click(tray);

            Assert.AreEqual(0, launcher.Started);
            Assert.IsTrue(tray.Cards.Shown.Any(c => c.Content.Status == FixRunLock.PairAlreadyRunningMessage));
        });
    }

    [TestMethod]
    public void AFixThatEndedOnTheLockSaysSo()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));

            tray.Context.SayFixRefusedByTheLock();

            Assert.AreEqual(FixRunLock.PairAlreadyRunningMessage, tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void TheRunFlagTheCoordinatorReadsIsUpOnlyWhileAPairingRuns()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            bool upDuringTheQuestion = false;
            PairAirPodsServices services = Services(rig);
            rig.Prompter.OnAsk = _ => upDuringTheQuestion = services.RunFlag.Active;
            rig.Prompter.SayCancel();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: services);

            Click(tray);

            Assert.IsTrue(upDuringTheQuestion);
            Assert.IsFalse(services.RunFlag.Active);
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

    public bool WriteFails { get; set; }

    public Exception? WriteThrows { get; set; }

    public StepOutcome Write()
    {
        Calls.Add("write");
        if (WriteThrows is { } thrown)
        {
            throw thrown;
        }

        if (WriteFails)
        {
            return StepOutcomes.FromHResult("pair-marker-write", unchecked((int)0x80070005), "denied");
        }

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
