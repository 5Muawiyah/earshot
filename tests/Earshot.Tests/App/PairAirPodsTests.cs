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

    private static readonly string[] StepsBeforeTheSession = ["look", "look-ms", "pair-marker-write"];

    private static readonly string[] ClosingSteps = ["closing", "closing-ms"];

    private static readonly string[] StepsBeforeAThrow = ["look", "look-ms", "unexpected", "unexpected-ms"];

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

    private static PairAirPodsServices Services(FixRig rig, RadioCheckState radio = RadioCheckState.Ok, string? lockName = null, IPairRecoveryMarker? marker = null, TimeSpan? stalePinBudget = null, TimeSpan? radioWait = null) => new()
    {
        Marker = marker ?? NoPairRecoveryMarker.Instance,
        StalePinBudget = stalePinBudget ?? StalePin.DefaultBudget,
        Api = rig.Pairing,
        Nodes = rig.Nodes,
        Prompter = rig.Prompter,
        UiFactory = _ => rig.Ui,
        Radio = new FakeRadioCheck(PairingSessionTests.Radio(radio)) { Wait = radioWait ?? TimeSpan.Zero },
        Options = rig.Host().Options,
        LockName = lockName ?? @"Local\Earshot.Tests.Pair." + Guid.NewGuid().ToString("N"),
    };

    private static MouseEventArgs Press(MouseButtons button) => new(button, clicks: 1, x: 0, y: 0, delta: 0);

    // The message loop until Exit ends it, with a give-up timer, so a regression fails the test instead of hanging the run.
    private static void RunUntilClosed(TrayHarness tray)
    {
        bool timedOut = false;
        using var giveUp = new System.Windows.Forms.Timer { Interval = 20000 };
        giveUp.Tick += (_, _) =>
        {
            timedOut = true;
            Application.ExitThread();
        };
        giveUp.Start();
        Application.Run(tray.Context);
        Assert.IsFalse(timedOut, "Exit did not end the run within 20 seconds.");
    }

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
            CollectionAssert.Contains(stepNames, "replace-ms");
            CollectionAssert.Contains(stepNames, "adopt");
            CollectionAssert.Contains(stepNames, "setup");
            // The steps before the session (the question and the marker) lead the entry, so a run that went on has them too.
            CollectionAssert.AreEqual(StepsBeforeTheSession, stepNames.Take(3).ToArray());
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
            // The new pair's device group may settle just after the answer: the tray answers for it, so the coordinator's card
            // does not replace the one that was just shown.
            Assert.IsTrue(tray.Coordinator.NewDeviceHook!(CancellationToken.None).GetAwaiter().GetResult(), "The tray answers for the new device it was just told about.");
            HistoryStep replace = entry.Steps.Single(st => st.Name == "replace");
            Assert.AreEqual(NativeCodes.NotAttempted, replace.Code);
            Assert.AreEqual(nameof(FixAnswer.Cancel), replace.CodeName);
            Assert.IsFalse(entry.Steps.Any(st => st.Name == "adopt"), "Nothing was adopted, so there is no adopt step.");
        });
    }

    // A new device group that arrives while Pair AirPods' first question is up (the pairing flag is still down): the pairing
    // answers for the pairs, so the tray's hook says it answered and the coordinator's card about unprotected AirPods, with a Fix
    // button the lock would refuse, is not shown over the question.
    [TestMethod]
    public void ANewDeviceWhilePairAirPodsAsksItsFirstQuestionIsAnsweredByThePairing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            TrayHarness? running = null;
            bool? answered = null;
            rig.Prompter.OnAsk = _ => answered ??= running!.Coordinator.NewDeviceHook!(CancellationToken.None).GetAwaiter().GetResult();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));
            running = tray;

            Click(tray);

            Assert.IsTrue(answered, "The hook said nothing was answered while the pairing's first question was up.");
        });
    }

    // The same new device group, then the person declines the first question: nothing answered for the new pair, so the
    // coordinator's check runs once the run ends. Before, the pair made in Windows went unchecked until the next start.
    [TestMethod]
    public void ANewDeviceDuringTheFirstQuestionIsCheckedWhenThePersonDeclines()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayCancel();
            var protection = new FakeProtectionCheck { Reading = new ProtectionReading(true, true, 1) };
            TrayHarness? running = null;
            rig.Prompter.OnAsk = _ => running!.Coordinator.NewDeviceHook!(CancellationToken.None).GetAwaiter().GetResult();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig),
                coordinatorServices: new CoordinatorServices { Protection = protection });
            running = tray;
            tray.PumpUntilIdle();
            int before = protection.Reads;

            Click(tray);
            TrayHarness.PumpUntil(() => protection.Reads > before, "The check was not run after the person declined.");

            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.Contains("a new device group came while Pair AirPods asked its first question", StringComparison.Ordinal)),
                string.Join(" | ", tray.Log.Entries.Select(e => e.Message)));
        });
    }

    // A device picker left open when Pair AirPods started: its choice is not applied while the pairing runs, since a pin made
    // then would race the pairing's adoption.
    [TestMethod]
    public void AChoiceFromAPickerOpenBeforePairAirPodsIsNotAppliedWhileItRuns()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            TrayHarness? running = null;
            var other = new Earshot.Tray.PickerChoice("Other", "Other", "AABBCCDDEEFF", Guid.NewGuid());
            rig.Prompter.OnAsk = q => _ = running!.Context.ApplyPickedChoiceAsync(other, CardPlace.NearTray);
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));
            running = tray;
            Guid pinnedBefore = tray.Settings.Current.PinnedContainerId;

            Click(tray);

            Assert.AreEqual(pinnedBefore, tray.Settings.Current.PinnedContainerId);
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Choose device: the choice is not applied, because Pair AirPods started while the picker was open."));
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

    // The Look for AirPods question shows the two kinds of case with their pictures, each with its line under it, so the
    // pairing-mode steps are pictures in Earshot itself and not only in the test window.
    [TestMethod]
    public void TheFirstQuestionShowsEachKindOfCaseWithItsPicture()
    {
        FixPrompt question = PairAirPodsText.FirstQuestion;

        Assert.IsNotNull(question.Pictures);
        CollectionAssert.AreEqual(new[] { PromptPictures.CaseFront, PromptPictures.CaseBack }, question.Pictures.Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(new[] { PairingText.CaseFront, PairingText.CaseBack }, question.Pictures.Select(p => p.Caption).ToArray());
        Assert.AreEqual(PairAirPodsText.LookButton, question.OkText);
        foreach (PromptPicture picture in question.Pictures)
        {
            using System.Drawing.Image? image = PromptPictures.Load(picture.Name);
            Assert.IsNotNull(image, picture.Name + " is carried in Earshot.");
            Assert.IsGreaterThan(0, image.Width);
        }
    }

    // The window that shows it: the iPhone line on its own, each picture over its caption, Look for AirPods and Cancel, with
    // Cancel the default as in the other questions. Built, not shown.
    [TestMethod]
    public void ThePictureQuestionHoldsTheIntroEachPictureItsCaptionAndBothButtons()
    {
        StaThread.Run(() =>
        {
            using System.Windows.Forms.Form form = PicturePromptWindow.Build(PairAirPodsText.FirstQuestion, out Button ok);

            List<Control> all = Flatten(form).ToList();
            Assert.HasCount(2, all.OfType<PictureBox>().Where(p => p.Image is not null).ToList());
            List<string> texts = all.OfType<Label>().Select(l => l.Text).ToList();
            CollectionAssert.Contains(texts, PairingText.TurnOffIPhoneBluetooth);
            CollectionAssert.Contains(texts, PairingText.CaseFront);
            CollectionAssert.Contains(texts, PairingText.CaseBack);
            Assert.IsFalse(texts.Any(t => t.Contains(PairingText.CaseFront, StringComparison.Ordinal) && t != PairingText.CaseFront), "Each case line is said once, under its picture.");
            Assert.AreEqual(PairAirPodsText.LookButton, ok.Text);
            Assert.AreEqual("Cancel", ((Button)form.CancelButton!).Text);
            Assert.AreSame<object?>(form.CancelButton, form.AcceptButton, "Enter presses Cancel, as in the other questions.");
        });
    }

    private static IEnumerable<Control> Flatten(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control below in Flatten(child))
            {
                yield return below;
            }
        }
    }

    // Exit while a question is up: the questions are asked with the tray's lifetime, so Exit closes them instead of waiting
    // out a question nobody will answer.
    [TestMethod]
    public void ThePairingQuestionsCloseWhenEarshotCloses()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Prompter.SayCancel();
            var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));
            CancellationToken asked;
            try
            {
                Click(tray);
                asked = rig.Prompter.Tokens.Single();
                Assert.IsTrue(asked.CanBeCanceled);
                Assert.IsFalse(asked.IsCancellationRequested);
            }
            finally
            {
                tray.Dispose();
            }

            Assert.IsTrue(asked.IsCancellationRequested, "Closing Earshot closes the question.");
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
            Assert.AreEqual(PairAirPodsText.ReplaceNotAnswered, tray.Cards.Shown[^1].Content.Status);
            Assert.AreEqual(PairAirPodsText.ChooseButton, tray.Cards.Shown[^1].Content.Action?.Label);
        });
    }

    // Set-up after a pairing is handed the run straight from the pairing. Before, the pairing let it go and set-up looked at the
    // pin first, so a Fix or an update pressed in between could start.
    // A set-up the person declined at the administrator prompt is recorded with Windows' code, not as "not attempted". Before,
    // the code was only in the log.
    [TestMethod]
    public void ASetUpDeclinedAfterAPairingIsRecordedWithItsCode()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig),
                coordinatorServices: new CoordinatorServices { History = history },
                arrange: h =>
                {
                    h.Block.Status = Block(BlockState.NotSetUp);
                    h.Block.OnRunSetup = _ => Task.FromResult(ControllerResult.Fail("Declined", [StepOutcomes.FromWin32("runas", 1223, "The operation was cancelled by the user.", ok: false)]));
                });

            Click(tray);

            HistoryStep setup = history.Read().Single().Steps.Single(s => s.Name == "setup");
            Assert.AreEqual(1223, setup.Code);
        });
    }

    // Exit chosen during the set-up a pairing handed over to: set-up still ends and is waited for, and the history says how it
    // ended from its own result, since the block status is not read while Earshot closes. Before, the unread status was
    // recorded as a set-up not attempted.
    [TestMethod]
    public void ASetUpThatEndsWhileEarshotClosesIsRecordedFromItsOwnResult()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            TrayHarness? running = null;
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig),
                coordinatorServices: new CoordinatorServices { History = history },
                arrange: h =>
                {
                    h.Block.Status = Block(BlockState.NotSetUp);
                    h.Block.OnRunSetup = async _ =>
                    {
                        running!.Ui.Post(_ => running.ClickMenu(MenuModel.Exit), null);
                        await Task.Delay(300, CancellationToken.None);
                        return ControllerResult.Ok("Done");
                    };
                });
            running = tray;
            bool timedOut = false;
            using var giveUp = new System.Windows.Forms.Timer { Interval = 20000 };
            giveUp.Tick += (_, _) =>
            {
                timedOut = true;
                Application.ExitThread();
            };
            giveUp.Start();

            tray.Ui.Post(_ =>
            {
                tray.Context.Menu.Refresh();
                tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PairAirPods).PerformClick();
            }, null);
            Application.Run(tray.Context);

            Assert.IsFalse(timedOut, "Exit did not finish.");
            HistoryStep setup = history.Read().Single().Steps.Single(s => s.Name == "setup");
            Assert.AreEqual(0, setup.Code, setup.CodeName);
            Assert.AreEqual("S_OK", setup.CodeName);
        });
    }

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
                    // The adoption lasts until the recovery's own look has ended (or ten seconds, on code that never says so), so
                    // the look always ends while the pairing still runs, however slow the runner.
                    var waited = System.Diagnostics.Stopwatch.StartNew();
                    while (waited.Elapsed < TimeSpan.FromSeconds(10) && running?.Log.Has(LogLevel.Info, "stops, because a pairing has started") != true)
                    {
                        await Task.Delay(10, CancellationToken.None);
                    }

                    return ControllerResult.Ok("Done");
                });
            running = tray;

            Click(tray);

            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.Contains("stops, because a pairing has started", StringComparison.Ordinal)),
                string.Join(" | ", tray.Log.Entries.Select(e => e.Message)));
            Assert.IsFalse(tray.Cards.Shown.Any(c => c.Content.Status == PairAirPodsText.RecoverChoose));

            // The check for AirPods Earshot does not protect waited for the look, which gave way: it runs once the pairing has
            // ended. Before, it was skipped for the whole session.
            const string Owed = "The check for AirPods that Earshot does not protect runs now: a pairing started during the tray's start-time look at the pairs";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!tray.Log.Has(LogLevel.Info, Owed) && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                tray.PumpUntilIdle();
                Thread.Sleep(5);
            }

            Assert.IsTrue(tray.Log.Has(LogLevel.Info, Owed), string.Join(" | ", tray.Log.Entries.Select(e => e.Message)));
        });
    }

    // A start-time recovery that ends while a pairing shows only its first question drops the flag: nothing is scanned yet.
    // Before, the recovery kept it up for the pairing, for as long as the question stayed.
    [TestMethod]
    public void ARecoveryThatEndsAtThePairingsFirstQuestionDropsTheFlag()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayCancel();
            var marker = new FakePairRecoveryMarker();
            PairAirPodsServices services = Services(rig, marker: marker, stalePinBudget: TimeSpan.FromSeconds(1));
            TrayHarness? running = null;
            bool? atQuestion = null;
            rig.Prompter.OnAsk = q =>
            {
                if (q.Heading != PairAirPodsText.FirstQuestion.Heading || running is null)
                {
                    return;
                }

                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!running.Log.Has(LogLevel.Info, "stops, because a pairing has started") && watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }

                atQuestion = services.RunFlag.Active;
            };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: services);
            running = tray;

            // A start-time recovery whose look at the paired list is slow (it ends on its budget), with Pair AirPods pressed meanwhile.
            marker.Present = true;
            rig.Pairing.HangOnList = true;
            tray.Context.StartPinnedPairCheck("start");
            Click(tray);

            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "stops, because a pairing has started"), "The recovery was still looking when the pairing started.");
            Assert.IsFalse(atQuestion, "The flag stayed up for a pairing that had only asked its first question.");
        });
    }

    // The check a recovery owes once a pairing it gave way to ends is not run while that pairing's first question is still up
    // (the flag is down then): it runs when the pairing ends. Before, the recovery's own end ran it at once, and the card about
    // unprotected AirPods, with a Fix button the lock refuses, showed over the question.
    [TestMethod]
    public void TheCheckARecoveryOwesWaitsForThePairingItGaveWayToToEnd()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayCancel();
            var marker = new FakePairRecoveryMarker();
            PairAirPodsServices services = Services(rig, marker: marker, stalePinBudget: TimeSpan.FromSeconds(1));
            var protection = new FakeProtectionCheck { Reading = new ProtectionReading(true, true, 1) };
            TrayHarness? running = null;
            int? readsAtQuestion = null;
            rig.Prompter.OnAsk = q =>
            {
                if (q.Heading != PairAirPodsText.FirstQuestion.Heading || running is null)
                {
                    return;
                }

                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!running.Log.Has(LogLevel.Info, "stops, because a pairing has started") && watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }

                // The recovery's own end, and anything it starts, has time to run while the question is up.
                watch.Restart();
                while (watch.Elapsed < TimeSpan.FromMilliseconds(500))
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }

                readsAtQuestion = protection.Reads;
            };
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), pairAirPods: services,
                coordinatorServices: new CoordinatorServices { Protection = protection, TrayPairing = services.RunFlag });
            running = tray;
            int readsBefore = protection.Reads;

            marker.Present = true;
            rig.Pairing.HangOnList = true;
            tray.Context.StartPinnedPairCheck("start");
            Click(tray);
            TrayHarness.PumpUntil(() => protection.Reads > readsBefore, "The owed check did not run once the pairing ended.");

            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "stops, because a pairing has started"), "The recovery was still looking when the pairing started.");
            Assert.AreEqual(readsBefore, readsAtQuestion, "The owed check ran while the pairing's first question was up.");
        });
    }

    // look-ms is how long the first question was up, not the radio read before it.
    [TestMethod]
    public void TheLookTimeIsTheQuestionsAloneNotTheRadioReadBeforeIt()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayCancel();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig, radioWait: TimeSpan.FromMilliseconds(1500)),
                coordinatorServices: new CoordinatorServices { History = history });

            Click(tray);

            HistoryStep look = history.Read().Single().Steps.Single(s => s.Name == "look-ms");
            Assert.IsLessThan(1000, look.Code, "The radio's 1.5 seconds were counted as the question's.");
        });
    }

    // The end of a pairing tells the coordinator, which reads the state again at once instead of after its doubled wait.
    [TestMethod]
    public void TheEndOfAPairingTellsTheCoordinatorToReadAgain()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayCancel();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));

            Click(tray);

            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "The tray's pairing has ended, so the state is read again now."),
                string.Join(" | ", tray.Log.Entries.Select(e => e.Message)));
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
            RunUntilClosed(tray);

            ConnectHistoryEntry entry = history.Read().Single();
            Assert.AreEqual(ConnectHistoryOutcomes.Cancelled, entry.Outcome);
            // The steps taken before Exit stay in the entry: before, only the closing step was kept.
            string[] names = entry.Steps.Select(s => s.Name).ToArray();
            CollectionAssert.AreEqual(StepsBeforeTheSession, names.Take(3).ToArray(), string.Join(", ", names));
            CollectionAssert.Contains(names, "scan");
            CollectionAssert.AreEqual(ClosingSteps, names.TakeLast(2).ToArray(), string.Join(", ", names));
            Assert.IsTrue(marker.Exists());
        });
    }

    // The automatic blocks wait for a pairing only once one may form: not while the first question is up, which can stay for
    // minutes with nothing scanned. Before, the flag was up from the press.
    [TestMethod]
    public void ThePairingFlagIsRaisedOnlyAfterLookForAirPods()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();
            PairAirPodsServices services = Services(rig);
            bool? atQuestion = null;
            bool? atPick = null;
            rig.Prompter.OnAsk = q =>
            {
                if (q.Heading == PairAirPodsText.FirstQuestion.Heading)
                {
                    atQuestion ??= services.RunFlag.Active;
                }
            };
            rig.Ui.OnPick = () => atPick = services.RunFlag.Active;
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: services);

            Click(tray);

            Assert.IsFalse(atQuestion, "Raised while only the first question was up.");
            Assert.IsTrue(atPick, "Up once the scan has started.");
            Assert.IsFalse(services.RunFlag.Active, "Dropped at the end.");
        });
    }

    // Choose device while Pair AirPods runs says the pairing is running and opens no picker, whose choice would race the
    // pairing's adoption. Before, the picker opened. The menu's click is posted to the UI thread, so it is run here while the
    // pick is still up: run 118 found it run after the pairing had ended, which opened the real picker on the runner's desktop.
    [TestMethod]
    public void ChooseDeviceIsRefusedWhileAPairingRuns()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();
            TrayHarness? running = null;
            rig.Ui.OnPick = () =>
            {
                running!.ClickMenu(MenuModel.ChooseDevice);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!running.Log.Has(LogLevel.Info, "Choose device: not opened, because Pair AirPods is running.") && watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Application.DoEvents();
                    Thread.Sleep(1);
                }
            };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));
            running = tray;

            Click(tray);

            Assert.IsTrue(tray.Cards.Shown.Any(c => c.Content.Status == FixRunLock.PairAlreadyRunningMessage), string.Join(" | ", tray.Cards.Shown.Select(c => c.Content.Status)));
            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Choose device: not opened, because Pair AirPods is running."));
        });
    }

    // Exit while the pick is up closes it: the entry says Earshot closed, not that the person chose nothing.
    [TestMethod]
    public void ExitAtThePickIsRecordedAsClosing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Ui.Pick = -1;
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig),
                coordinatorServices: new CoordinatorServices { History = history });
            rig.Ui.OnPick = () => tray.ClickMenu(MenuModel.Exit);

            tray.Ui.Post(_ =>
            {
                tray.Context.Menu.Refresh();
                tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PairAirPods).PerformClick();
            }, null);
            RunUntilClosed(tray);

            ConnectHistoryEntry entry = history.Read().Single();
            string[] names = entry.Steps.Select(s => s.Name).ToArray();
            CollectionAssert.Contains(names, "closing", string.Join(", ", names));
            CollectionAssert.DoesNotContain(names, "cancelled", string.Join(", ", names));
        });
    }

    // Exit at "Use these AirPods instead?" closes it: the entry says Earshot closed, not that the person kept the old pair.
    [TestMethod]
    public void ExitAtTheReplaceQuestionIsRecordedAsClosing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayCancel();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig),
                coordinatorServices: new CoordinatorServices { History = history });
            rig.Prompter.OnAsk = q =>
            {
                if (q.Heading == PairAirPodsText.ReplaceHeading)
                {
                    tray.ClickMenu(MenuModel.Exit);
                }
            };

            tray.Ui.Post(_ =>
            {
                tray.Context.Menu.Refresh();
                tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PairAirPods).PerformClick();
            }, null);
            RunUntilClosed(tray);

            string[] names = history.Read().Single().Steps.Select(s => s.Name).ToArray();
            CollectionAssert.Contains(names, "closing", string.Join(", ", names));
            CollectionAssert.DoesNotContain(names, "replace", string.Join(", ", names));
        });
    }

    // Exit while the gate is given the new pair, and the gate run is cut off: the entry says Earshot closed, not that the
    // adoption failed, and the marker is kept for the next start.
    [TestMethod]
    public void ExitDuringTheAdoptionIsRecordedAsClosing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            var marker = new FakePairRecoveryMarker();
            TrayHarness? running = null;
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig, marker: marker),
                coordinatorServices: new CoordinatorServices { History = history },
                arrange: h => h.Block.OnSetDevice = async _ =>
                {
                    running!.Ui.Post(_ => running.ClickMenu(MenuModel.Exit), null);
                    await Task.Delay(500, CancellationToken.None);
                    throw new OperationCanceledException("The gate run was cut off by Exit.");
                });
            running = tray;

            tray.Ui.Post(_ =>
            {
                tray.Context.Menu.Refresh();
                tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PairAirPods).PerformClick();
            }, null);
            RunUntilClosed(tray);

            string[] names = history.Read().Single().Steps.Select(s => s.Name).ToArray();
            CollectionAssert.Contains(names, "closing", string.Join(", ", names));
            CollectionAssert.DoesNotContain(names, "adopt", string.Join(", ", names));
            CollectionAssert.Contains(names, "adopt-ms", string.Join(", ", names));
            Assert.IsTrue(marker.Exists(), "The marker is kept for the next start.");
        });
    }

    // Exit while the gate is given the new pair, and the gate takes it: the tray saves the same pin, so the tray and the gate
    // never name different pairs, and the adoption is recorded as done. Before, the pin was not saved because Earshot was closing.
    [TestMethod]
    public void ExitAfterTheGateTookTheNewPairStillSavesItsPin()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            TrayHarness? running = null;
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig),
                coordinatorServices: new CoordinatorServices { History = history },
                arrange: h => h.Block.OnSetDevice = async _ =>
                {
                    running!.Ui.Post(_ => running.ClickMenu(MenuModel.Exit), null);
                    await Task.Delay(500, CancellationToken.None);
                    return ControllerResult.Ok("Device set");
                });
            running = tray;

            tray.Ui.Post(_ =>
            {
                tray.Context.Menu.Refresh();
                tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PairAirPods).PerformClick();
            }, null);
            RunUntilClosed(tray);

            Assert.AreEqual(FixRig.NewAirPodsContainer, tray.Settings.Current.PinnedContainerId, "The tray names the pair the gate took.");
            HistoryStep[] steps = history.Read().Single().Steps.ToArray();
            Assert.AreEqual(0, steps.Single(s => s.Name == "adopt").Code, string.Join(", ", steps.Select(s => s.Name)));
        });
    }

    // A click while Pair AirPods runs is refused with a line, so no switch races the run's adoption. Before, it connected.
    [TestMethod]
    public void AClickWhilePairAirPodsRunsIsRefused()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Ui.HoldPick = TimeSpan.FromSeconds(2);
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));
            rig.Ui.OnPick = () => tray.Ui.Post(_ => tray.Context.OnIconMouseClick(null, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0)), null);

            Click(tray);

            Assert.IsFalse(tray.Connection.Calls.Any(c => c.Connect), string.Join(", ", tray.Connection.Calls));
            Assert.IsTrue(tray.Cards.Shown.Any(c => c.Content.Status == FixRunLock.PairAlreadyRunningMessage), string.Join(" | ", tray.Cards.Statuses));
        });
    }

    // Connect in Windows pressed on an earlier card while Pair AirPods runs is refused the same way: it allows the pinned pair's
    // nodes, which the run's adoption would then meet. Before, it ran.
    [TestMethod]
    public void ConnectInWindowsWhilePairAirPodsRunsIsRefused()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Ui.HoldPick = TimeSpan.FromSeconds(2);
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));
            tray.Connection.OnConnect = _ => Task.FromResult(new ConnectResult(
                ConnectOutcome.AttemptedTimedOut, "Still connecting. Check your AirPods.", [StepOutcomes.FromHResult("ks-reconnect:src", 0)]));
            tray.Context.OnIconMouseClick(null, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            tray.PumpUntilIdle();
            CardAction connectInWindows = tray.Cards.Shown.Last(c => c.Content.Action?.Label == ConnectInWindowsOffer.ButtonLabel).Content.Action!;
            rig.Ui.OnPick = () => tray.Ui.Post(_ => connectInWindows.OnClick(), null);

            Click(tray);

            Assert.IsTrue(tray.Log.Has(LogLevel.Info, "Connect in Windows ignored: Pair AirPods is running."), string.Join(" | ", tray.Log.Entries.Select(e => e.Message)));
            Assert.IsFalse(tray.Log.Has(LogLevel.Info, "Connect in Windows: started from the card."));
            Assert.IsTrue(tray.Cards.Shown.Any(c => c.Content.Status == FixRunLock.PairAlreadyRunningMessage));
        });
    }

    // Block at boot and Protect audio quality while Pair AirPods runs are refused the same way: both are gate changes that would
    // race the run's adoption. Before, they ran.
    [TestMethod]
    public void GateChangesWhilePairAirPodsRunsAreRefused()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Ui.HoldPick = TimeSpan.FromSeconds(2);
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig));
            bool protectBefore = tray.Settings.Current.ProtectAudioQuality;
            rig.Ui.OnPick = () => tray.Ui.Post(_ =>
            {
                tray.ClickMenu(MenuModel.BlockAtBoot);
                tray.ClickMenu(MenuModel.ProtectAudioQuality);
            }, null);

            Click(tray);

            Assert.IsFalse(tray.Block.Calls.Any(c => c.StartsWith("setboot", StringComparison.Ordinal) || c.StartsWith("protect", StringComparison.Ordinal)), string.Join(", ", tray.Block.Calls));
            Assert.AreEqual(protectBefore, tray.Settings.Current.ProtectAudioQuality, "The setting was not flipped.");
            Assert.AreEqual(2, tray.Cards.Shown.Count(c => c.Content.Status == FixRunLock.PairAlreadyRunningMessage), string.Join(" | ", tray.Cards.Statuses));
        });
    }

    // A refusal the gate gave just as Exit began is still the adoption's result, with its code, not Earshot closing.
    [TestMethod]
    public void AGateRefusalAsExitBeginsKeepsItsCode()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            TrayHarness? running = null;
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig),
                coordinatorServices: new CoordinatorServices { History = history },
                arrange: h => h.Block.OnSetDevice = async _ =>
                {
                    running!.Ui.Post(_ => running.ClickMenu(MenuModel.Exit), null);
                    await Task.Delay(500, CancellationToken.None);
                    return ControllerResult.Fail("refused", [StepOutcomes.FromWin32("set-device", 1062, "not started", ok: false)]);
                });
            running = tray;

            tray.Ui.Post(_ =>
            {
                tray.Context.Menu.Refresh();
                tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PairAirPods).PerformClick();
            }, null);
            RunUntilClosed(tray);

            HistoryStep[] steps = history.Read().Single().Steps.ToArray();
            HistoryStep adopt = steps.Single(s => s.Name == "adopt");
            Assert.AreEqual(1062, adopt.Code, string.Join(", ", steps.Select(s => s.Name)));
        });
    }

    // Exit while the first question is up closes it: the entry says Earshot closed, not that the person said no.
    [TestMethod]
    public void ExitWhileTheFirstQuestionIsUpIsRecordedAsClosing()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayCancel();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig),
                coordinatorServices: new CoordinatorServices { History = history });
            rig.Prompter.OnAsk = q =>
            {
                if (q.Heading == PairAirPodsText.FirstQuestion.Heading)
                {
                    tray.ClickMenu(MenuModel.Exit);
                }
            };

            tray.Ui.Post(_ =>
            {
                tray.Context.Menu.Refresh();
                tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PairAirPods).PerformClick();
            }, null);
            RunUntilClosed(tray);

            ConnectHistoryEntry entry = history.Read().Single();
            CollectionAssert.AreEqual(ClosingSteps, entry.Steps.Select(s => s.Name).ToArray());
            Assert.IsFalse(tray.Log.Has(LogLevel.Info, "the person did not press Look for AirPods"));
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

    // Earshot's own time limit ended the pairing: Windows may still finish it, with nodes that arrive after the look, so the
    // marker stays for the next start. Before, a look that came too early cleared it.
    [TestMethod]
    public void APairingEarshotStoppedWaitingForKeepsTheMarker()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice(UnpairedId, "AirPods Pro", FixRig.NewAirPodsAddress, true, 6, null)));
            rig.Pairing.PairResult = new PairingCallResult(PairingSession.CancelledTimeout, 1460, false, null);
            rig.Prompter.SayOk();
            var marker = new FakePairRecoveryMarker();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, marker: marker));

            Click(tray);

            CollectionAssert.Contains(marker.Calls, "write");
            Assert.IsTrue(marker.Exists(), string.Join(", ", marker.Calls));
        });
    }

    // A marker an earlier run left is still there after this run adopts its own pair: that run may have left a pair this one did
    // not look at. Before, the adoption cleared it with this run's own.
    [TestMethod]
    public void AnAdoptionKeepsAMarkerAnEarlierRunLeft()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();
            var marker = new FakePairRecoveryMarker();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, marker: marker));
            marker.Present = true;

            Click(tray);

            Assert.AreEqual(FixRig.NewAirPodsContainer, tray.Settings.Current.PinnedContainerId, "The new pair was adopted.");
            Assert.IsTrue(marker.Exists(), "The earlier run's marker is there for the next start: " + string.Join(", ", marker.Calls));
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
            rig.Prompter.OnAsk = _ => Thread.Sleep(40);
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            var protection = new FakeProtectionCheck();
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig, marker: marker),
                coordinatorServices: new CoordinatorServices { History = history, Protection = protection });
            int readsBefore = protection.Reads;

            Click(tray);

            ConnectHistoryEntry entry = history.Read().Single();
            Assert.AreEqual(ConnectHistoryOutcomes.Failed, entry.Outcome);
            // The steps taken before the throw stay, and the run is timed: before, the entry had only the throw, at 0 ms.
            CollectionAssert.AreEqual(StepsBeforeAThrow, entry.Steps.Select(s => s.Name).ToArray());
            Assert.AreEqual(thrown.HResult, entry.Steps.Single(s => s.Name == "unexpected").Code);
            Assert.IsGreaterThanOrEqualTo(40L, entry.DurationMs, "The run took at least as long as its first question.");
            // Windows may have paired before the throw, so the check against the gate's own device runs once the run has let go.
            Assert.AreEqual(readsBefore + 1, protection.Reads, string.Join(" | ", tray.Log.Entries.Select(e => e.Message)));
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

                Assert.AreEqual(FixRunLock.LockHeldMessage, tray.Cards.Shown[^1].Content.Status);
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
    public void AClickWithNothingFoundSaysSoWithAPairAirPodsButtonThatStartsPairing()
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
            Assert.AreEqual("Earshot can't find your AirPods on this PC.", card.Status, "The click reads names, not what is paired, so it claims only that.");
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

    // The gate refused the new pair: its code goes in the adopt step, and the card about the pair left unprotected stays up with
    // the picker. Before, the step said "not attempted" and the card went after a few seconds with no button.
    [TestMethod]
    public void AnAdoptionTheGateRefusedIsRecordedWithItsCodeAndItsCardStaysUp()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            rig.Prompter.SayOk();
            rig.Prompter.SayOk();
            using var folder = new TempFolder();
            var history = new ConnectHistory(folder.File("history.json"), TimeProvider.System, new CapturingLog());
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: Services(rig),
                coordinatorServices: new CoordinatorServices { History = history },
                arrange: h => h.Block.OnSetDevice = _ => Task.FromResult(ControllerResult.Fail("refused", [StepOutcomes.FromWin32("set-device", 1062, "not started", ok: false)])));

            Click(tray);

            HistoryStep adopt = history.Read().Single().Steps.Single(s => s.Name == "adopt");
            Assert.AreEqual(1062, adopt.Code);
            CardContent card = tray.Cards.Shown.Last(c => c.Content.Status == PairAirPodsText.PairedNotAdopted).Content;
            Assert.IsTrue(card.StaysUp);
            Assert.AreEqual(PairAirPodsText.ChooseButton, card.Action?.Label);
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
            Assert.IsTrue(card.StaysUp, "Shown at sign-in, the card must not go after twelve seconds.");
            Assert.AreEqual(RecordedNodes.AirPodsContainer, tray.Settings.Current.PinnedContainerId, "Nothing was adopted by itself.");
            Assert.IsTrue(marker.Exists(), "Kept until a pair is chosen.");
            Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("unpair:", StringComparison.Ordinal)));
        });
    }

    // The coordinator's own start answer to a Fix run (a gate run) ends after the tray's look at the marker has shown its card and
    // dropped the flag: the coordinator's start check still sees that the tray's look answered for the pairs, so its card about
    // unprotected AirPods does not replace "Choose AirPods". Before, only the flag held it back.
    [TestMethod]
    public void AMarkerLeftAtStartKeepsItsCardWhenTheCoordinatorsStartCheckComesAfterIt()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Concat(FixRig.NewAirPodsNodes()));
            rig.Pairing.Paired.Add(new PairedDevice("paired-new", "AirPods Pro", FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer));
            var marker = new FakePairRecoveryMarker { Present = true };
            PairAirPodsServices services = Services(rig, marker: marker);
            var protection = new FakeProtectionCheck { Reading = new ProtectionReading(true, true, 1) };
            var slowGateRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovery = new FakeStartRecovery { Hold = slowGateRun.Task };
            using var tray = new TrayHarness(
                snapshot: NoDevice(),
                pairAirPods: services,
                coordinatorServices: new CoordinatorServices { Protection = protection, TrayPairing = services.RunFlag, Recovery = recovery },
                settings: s =>
                {
                    s.PinnedContainerId = RecordedNodes.AirPodsContainer;
                    s.PinnedAddress = RecordedNodes.AirPodsAddress;
                });
            TrayHarness.PumpUntil(() => tray.Cards.Shown.Any(c => c.Content.Status == PairAirPodsText.RecoverChoose) && !services.RunFlag.Active, "The tray's look did not show its card.");

            slowGateRun.SetResult();
            TrayHarness.PumpUntil(
                () => tray.Log.Entries.Any(e => e.Message.Contains("answers for the pairs, so the check for AirPods that Earshot does not protect did not run", StringComparison.Ordinal)),
                "The coordinator's start check ran over the tray's card: " + string.Join(" | ", tray.Cards.Statuses));

            Assert.AreEqual(PairAirPodsText.RecoverChoose, tray.Cards.Shown[^1].Content.Status, string.Join(" | ", tray.Cards.Statuses));
            Assert.AreEqual(0, protection.Reads);
        });
    }

    // At sign-in Windows may hold the card back (quiet time, a full-screen app): the recovery then counts as having shown
    // nothing, so the coordinator's own check runs, and the marker stays for the next start. Before, the check was skipped for
    // the session as if the card had been seen.
    [TestMethod]
    public void AMarkerLeftAtStartWhoseCardIsHeldBackLetsTheCoordinatorsCheckRunAndKeepsTheMarker()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Concat(FixRig.NewAirPodsNodes()));
            rig.Pairing.Paired.Add(new PairedDevice("paired-new", "AirPods Pro", FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer));
            var marker = new FakePairRecoveryMarker { Present = true };
            PairAirPodsServices services = Services(rig, marker: marker);
            var protection = new FakeProtectionCheck { Reading = new ProtectionReading(true, true, 1) };
            using var tray = new TrayHarness(
                snapshot: NoDevice(),
                pairAirPods: services,
                coordinatorServices: new CoordinatorServices { Protection = protection, TrayPairing = services.RunFlag },
                arrange: h => h.Cards.HoldBackNearTray = true,
                settings: s =>
                {
                    s.PinnedContainerId = RecordedNodes.AirPodsContainer;
                    s.PinnedAddress = RecordedNodes.AirPodsAddress;
                });
            TrayHarness.PumpUntil(() => protection.Reads > 0, "The coordinator's check did not run after the held-back card.");

            Assert.IsTrue(tray.Cards.HeldBack.Any(c => c.Content.Status == PairAirPodsText.RecoverChoose));
            Assert.IsTrue(marker.Exists(), "Kept for the next start.");
        });
    }

    // The same with the pinned pair removed in Windows: the recovery's look at the pinned pair shows its card, and Windows holds
    // that back too, so it answered nothing and the coordinator's check still runs. Before, that look's card counted as seen.
    [TestMethod]
    public void AMarkerLeftAtStartWhoseStalePinCardIsHeldBackLetsTheCoordinatorsCheckRun()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Pairing.Paired.RemoveAll(p => p.Id == "paired-old");
            foreach (string id in RecordedNodes.AirPodsTargets)
            {
                rig.Gate.Nodes[id].Present = false;
            }

            rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Concat(FixRig.NewAirPodsNodes()));
            rig.Pairing.Paired.Add(new PairedDevice("paired-new", "AirPods Pro", FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer));
            var marker = new FakePairRecoveryMarker { Present = true };
            PairAirPodsServices services = Services(rig, marker: marker);
            var protection = new FakeProtectionCheck { Reading = new ProtectionReading(true, true, 1) };
            using var tray = new TrayHarness(
                snapshot: NoDevice(),
                pairAirPods: services,
                coordinatorServices: new CoordinatorServices { Protection = protection, TrayPairing = services.RunFlag },
                arrange: h => h.Cards.HoldBackNearTray = true,
                settings: s =>
                {
                    s.PinnedContainerId = RecordedNodes.AirPodsContainer;
                    s.PinnedAddress = RecordedNodes.AirPodsAddress;
                });
            TrayHarness.PumpUntil(() => protection.Reads > 0, "The coordinator's check did not run after the held-back stale-pin card.");

            Assert.IsTrue(tray.Cards.HeldBack.Any(c => c.Content.Status == PairAirPodsText.StaleChosen), string.Join(" | ", tray.Cards.HeldBack.Select(c => c.Content.Status)));
            Assert.IsTrue(marker.Exists(), "Kept for the next start.");
        });
    }

    // A start-time recovery that throws showed nothing about the pairs, so the check for unprotected AirPods still runs. The throw
    // comes after an await, as a real paired-list read's does, so the coordinator has already found the flag up and left the
    // check to the look; before, the rethrow skipped the look's own call for it.
    [TestMethod]
    public void ARecoveryThatThrowsDoesNotHoldBackTheCoordinatorsStartCheck()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Pairing.ThrowOnListLater = new IOException("the paired list failed");
            var marker = new FakePairRecoveryMarker { Present = true };
            PairAirPodsServices services = Services(rig, marker: marker);
            var protection = new FakeProtectionCheck { Reading = new ProtectionReading(true, true, 1) };
            using var tray = new TrayHarness(
                snapshot: NoDevice(),
                pairAirPods: services,
                coordinatorServices: new CoordinatorServices { Protection = protection, TrayPairing = services.RunFlag },
                settings: s =>
                {
                    s.PinnedContainerId = RecordedNodes.AirPodsContainer;
                    s.PinnedAddress = RecordedNodes.AirPodsAddress;
                });
            TrayHarness.PumpUntil(() => protection.Reads > 0, "The coordinator's start check was held back by a recovery that threw.");
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
            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.Contains("The check for AirPods that Earshot does not protect runs now: the look at a pairing that did not finish showed nothing", StringComparison.Ordinal)),
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
            Assert.IsTrue(card.StaysUp, "Shown at sign-in, the card must not go after twelve seconds.");
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
            Assert.IsTrue(card.StaysUp, "A pair left unprotected until one is chosen: the card stays until it is used.");
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

    // Cancel pressed while Windows is pairing: the bond may still form and its nodes arrive later, so the marker stays for the next
    // start's look. Before, one look straight after the cancel cleared it.
    [TestMethod]
    public void CancelWhileWindowsIsPairingKeepsTheMarker()
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
                // Cancel the moment Windows is asked to pair. Run 121 found a cancel 300 ms after the pick racing the rig's own
                // 300 ms pairing limit, which then ended the run as a timeout.
                UiFactory = c =>
                {
                    rig.Pairing.OnPair = c.Cancel;
                    return rig.Ui;
                },
            };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), time: TimeProvider.System, pairAirPods: cancelling);

            Click(tray);

            Assert.IsTrue(rig.Pairing.Calls.Any(c => c.StartsWith("pair:", StringComparison.Ordinal)), "Windows was asked to pair.");
            Assert.AreEqual(PairingText.Cancelled, tray.Cards.Shown[^1].Content.Status);
            Assert.IsTrue(marker.Exists(), "Kept for the next start's look.");
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
            Assert.IsTrue(tray.Cards.Shown.Any(c => c.Content.Status == "Earshot is already pairing the AirPods."), "The tray knows its own pairing runs.");
        });
    }

    [TestMethod]
    public void AFixThatEndedOnTheLockSaysSo()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));

            tray.Context.SayFixRefusedByTheLock();

            // The lock cannot tell a run from another program that holds its name, so the card says a run may be open. Before,
            // it said one was, also when nothing was.
            Assert.AreEqual("Fix connection or Pair AirPods may already be running. If neither is, restart the PC, then try again.", tray.Cards.Shown[^1].Content.Status);
        });
    }

    // Up while a pairing may form (here, at the pick after the scan) and down once the run has ended. While only the first
    // question is up it is down (ThePairingFlagIsRaisedOnlyAfterLookForAirPods).
    [TestMethod]
    public void TheRunFlagTheCoordinatorReadsIsUpOnlyWhileAPairingRuns()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            bool upDuringThePick = false;
            PairAirPodsServices services = Services(rig);
            rig.Ui.OnPick = () => upDuringThePick = services.RunFlag.Active;
            rig.Ui.Pick = -1;
            rig.Prompter.SayOk();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: services);

            Click(tray);

            Assert.IsTrue(upDuringThePick);
            Assert.IsFalse(services.RunFlag.Active);
        });
    }

    // A write whose temp file could not take the marker's place (here a folder stands where the marker goes) leaves no temp file.
    [TestMethod]
    public void AFailedMarkerWriteLeavesNoTempFile()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, PairRecoveryMarker.FileName));
        var marker = new PairRecoveryMarker(folder.Path, TimeProvider.System);

        StepOutcome written = marker.Write();

        Assert.IsFalse(written.Ok);
        Assert.IsFalse(File.Exists(Path.Combine(folder.Path, PairRecoveryMarker.FileName + ".tmp")));
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
