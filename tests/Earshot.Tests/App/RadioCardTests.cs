using System.Windows.Forms;
using Earshot.App;
using Earshot.Bluetooth;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase1;
using Earshot.Tests.Repair;
using Earshot.Tests.Widget;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.App;

// A Connect that fails while the radio is off, switched off, broken or absent says that, with a button to Bluetooth settings, and
// offers nothing that cannot help: no Fix connection, no Connect in Windows and no Apple reset advice. With the radio on, or
// unread, the failure card keeps the offers it had.
[TestClass]
public sealed class RadioCardTests
{
    private static MouseEventArgs Press(MouseButtons button) => new(button, clicks: 1, x: 0, y: 0, delta: 0);

    private static PairAirPodsServices Services(FixRig rig, RadioCheckState state, int? problem = null) => new()
    {
        Api = rig.Pairing,
        Nodes = rig.Nodes,
        Prompter = rig.Prompter,
        UiFactory = _ => rig.Ui,
        Radio = new FakeRadioCheck(Pairing.PairingSessionTests.Radio(state, problem)),
        Options = rig.Host().Options,
        LockName = @"Local\Earshot.Tests.Radio." + Guid.NewGuid().ToString("N"),
    };

    private static void FailTheConnect(TrayHarness tray)
    {
        tray.Connection.OnConnect = _ => Task.FromResult(new ConnectResult(
            ConnectOutcome.AttemptedTimedOut, "Still connecting. Check your AirPods.", [StepOutcomes.FromHResult("ks-reconnect:src", 0)]));
        tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
        tray.PumpUntilIdle();
    }

    // A click made while the failed connect's radio is being read is a new connect, not the opposite of a switch that has
    // already ended. Before, it was taken as a disconnect of the switch still "in flight", and the failure card never came.
    [TestMethod]
    public void AClickWhileTheFailureIsLookedAtConnectsAgainAndDoesNotDisconnect()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            var radio = new FakeRadioCheck(Pairing.PairingSessionTests.Radio(RadioCheckState.Ok));
            PairAirPodsServices services = Services(rig, RadioCheckState.Ok);
            var slow = new PairAirPodsServices
            {
                Api = services.Api, Nodes = services.Nodes, Prompter = services.Prompter, UiFactory = services.UiFactory, Radio = radio,
                Options = services.Options, LockName = services.LockName,
            };
            long now = 0;
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: slow, tickCount: () => now);
            tray.Connection.OnConnect = _ => Task.FromResult(new ConnectResult(
                ConnectOutcome.AttemptedTimedOut, "Still connecting. Check your AirPods.", [StepOutcomes.FromHResult("ks-reconnect:src", 0)]));

            // The read after the failed connect lasts until the second click ends it, so the click always lands during the look.
            // Run 124 found a two-second read that could end first on a busy runner; then the click was a plain new connect.
            // Any other read (the start's own look) answers at once.
            tray.PumpUntilIdle();
            int failureRead = radio.Checks + 1;
            radio.OnCheck = n => radio.Wait = n == failureRead ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            TrayHarness.PumpUntil(() => radio.Checks == failureRead, "The radio was not read after the failed connect.");
            now += 5000;
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            Assert.AreEqual(2, tray.Connection.Calls.Count(c => c.Connect), string.Join(", ", tray.Connection.Calls));
            Assert.IsFalse(tray.Connection.Calls.Any(c => !c.Connect), "The second click was taken as a disconnect.");
        });
    }

    // Exit pressed while such a click waits for the looks to end: the click ends with them, and nothing is sent after Exit began.
    // A second click meanwhile is logged and adds nothing. Before, the request reached the coordinator after its shutdown.
    [TestMethod]
    public void ExitWhileAClickWaitsForTheFailuresLooksSendsNoNewRequest()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            var radio = new FakeRadioCheck(Pairing.PairingSessionTests.Radio(RadioCheckState.Ok));
            PairAirPodsServices services = Services(rig, RadioCheckState.Ok);
            var slow = new PairAirPodsServices
            {
                Api = services.Api, Nodes = services.Nodes, Prompter = services.Prompter, UiFactory = services.UiFactory, Radio = radio,
                Options = services.Options, LockName = services.LockName,
            };
            long now = 0;
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: slow, tickCount: () => now);
            tray.Connection.OnConnect = _ => Task.FromResult(new ConnectResult(
                ConnectOutcome.AttemptedTimedOut, "Still connecting. Check your AirPods.", [StepOutcomes.FromHResult("ks-reconnect:src", 0)]));

            // The read after the failed connect does not answer until Exit has begun, cancel or not, so the looks are still running
            // when Exit comes. Runs 124 and 126 found a read that ended on the click's own cancel, inline, so the click's request
            // started before Exit and was a plain new connect. Any other read (the start's own look) answers at once.
            tray.PumpUntilIdle();
            int failureRead = radio.Checks + 1;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            radio.OnCheck = n => radio.HoldUntil = n == failureRead ? release.Task : null;
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            TrayHarness.PumpUntil(() => radio.Checks == failureRead, "The radio was not read after the failed connect.");
            now += 5000;
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            now += 5000;
            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.Context.ExitFromSignal();
            release.TrySetResult();
            tray.PumpUntilIdle();

            Assert.AreEqual(1, tray.Connection.Calls.Count(c => c.Connect), string.Join(", ", tray.Connection.Calls));
            Assert.IsFalse(tray.Log.Entries.Any(e => e.Message.Contains("connect (cancelled", StringComparison.Ordinal)), "A request was sent after Exit began.");
            Assert.IsTrue(tray.Log.Entries.Any(e => e.Message.Contains("so this one adds nothing", StringComparison.Ordinal)), "The second click was dropped without a word.");
        });
    }

    // A click landing as the read after the failure answers Off: the old connect shows no radio card, so only the new one's
    // shows. Before, both did.
    [TestMethod]
    public void AClickAsTheFailuresRadioReadAnswersShowsOnlyTheNewRequestsCard()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            var radio = new FakeRadioCheck(Pairing.PairingSessionTests.Radio(RadioCheckState.Off));
            PairAirPodsServices services = Services(rig, RadioCheckState.Off);
            var watched = new PairAirPodsServices
            {
                Api = services.Api, Nodes = services.Nodes, Prompter = services.Prompter, UiFactory = services.UiFactory, Radio = radio,
                Options = services.Options, LockName = services.LockName,
            };
            long now = 0;
            TrayHarness? running = null;
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: watched, tickCount: () => now);
            running = tray;
            tray.PumpUntilIdle();
            tray.Cards.Shown.Clear();
            int failureRead = radio.Checks + 1;
            radio.OnCheck = n =>
            {
                if (n == failureRead)
                {
                    now += 5000;
                    running.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
                }
            };
            tray.Connection.OnConnect = _ => Task.FromResult(new ConnectResult(
                ConnectOutcome.AttemptedTimedOut, "Still connecting. Check your AirPods.", [StepOutcomes.FromHResult("ks-reconnect:src", 0)]));

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            Assert.AreEqual(2, tray.Connection.Calls.Count(c => c.Connect), string.Join(", ", tray.Connection.Calls));
            Assert.AreEqual(1, tray.Cards.Shown.Count(c => c.Content.Status == RadioLines.Off), string.Join(" | ", tray.Cards.Statuses));
        });
    }

    [TestMethod]
    [DataRow((int)RadioCheckState.Off, "Bluetooth is off.")]
    [DataRow((int)RadioCheckState.SwitchedOff, "Bluetooth is turned off by a switch, the firmware or a policy on this PC.")]
    [DataRow((int)RadioCheckState.NoAdapter, "Windows finds no Bluetooth on this PC.")]
    public void AFailedConnectWithABlockedRadioShowsItsLineAndOpensBluetoothSettings(int radioState, string line)
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            var launcher = new FakeSettingsLauncher();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, (RadioCheckState)radioState), settingsLauncher: launcher);

            FailTheConnect(tray);

            CardContent card = tray.Cards.Shown[^1].Content;
            Assert.AreEqual(line, card.Status);
            Assert.AreEqual(RadioLines.BluetoothSettingsButton, card.Action?.Label);
            if ((RadioCheckState)radioState == RadioCheckState.Off)
            {
                LiveTests.LiveTest24Lines.AssertLogged(tray.Log, LiveTests.LiveTest24Lines.RadioOffAfterConnect);
            }

            Assert.IsFalse(tray.Cards.Shown.Any(c => c.Content.Action?.Label is "Fix connection" or "Connect in Windows"), "Nothing that cannot help is offered.");
            StringAssert.DoesNotMatch(card.Status, new System.Text.RegularExpressions.Regex("reset|Reset|Apple|setup button", System.Text.RegularExpressions.RegexOptions.CultureInvariant));

            card.Action!.OnClick();
            tray.PumpUntilIdle();

            Assert.HasCount(1, launcher.Opened);
            Assert.AreEqual("ms-settings:bluetooth", launcher.Opened[0]);
        });
    }

    // A connect that was accepted and never connected, with nothing heard from the air, is the Unknown class: it may be either
    // side, so it never leads to Fix. The radio is still read after it, and a radio that is off is still the whole story.
    [TestMethod]
    public void AnUnknownClassFailureStillShowsTheRadiosLine()
    {
        var timedOut = new ConnectResult(ConnectOutcome.AttemptedTimedOut, "Still connecting. Check your AirPods.", [StepOutcomes.FromHResult("ks-reconnect:src", 0)]);
        Assert.AreEqual(FailureClass.Unknown, ConnectFailureClassifier.Classify(timedOut, DeviceHint.Unknown, retried: false).Class);

        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, RadioCheckState.Off));

            FailTheConnect(tray);

            Assert.AreEqual(RadioLines.Off, tray.Cards.Shown[^1].Content.Status);
            Assert.AreEqual(RadioLines.BluetoothSettingsButton, tray.Cards.Shown[^1].Content.Action?.Label);
        });
    }

    // An adapter without Classic Bluetooth has its line and no button: there is nothing to change in Bluetooth settings, and
    // nothing for the AirPods is offered either.
    [TestMethod]
    public void AnAdapterWithoutClassicBluetoothShowsItsLineWithNoButton()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, RadioCheckState.NoClassic));

            FailTheConnect(tray);

            Assert.AreEqual(RadioLines.NoClassic, tray.Cards.Shown[^1].Content.Status);
            Assert.IsNull(tray.Cards.Shown[^1].Content.Action);
            Assert.IsFalse(tray.Cards.Shown.Any(c => c.Content.Action?.Label is "Fix connection" or "Connect in Windows"));
        });
    }

    [TestMethod]
    public void AProblemCodeIsNamedInTheLine()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, RadioCheckState.Problem, 10));

            FailTheConnect(tray);

            Assert.AreEqual("Bluetooth on this PC isn't working. Windows reports problem code 10.", tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void AnOkOrUnknownRadioKeepsTheOffersTheFailureHad()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: Services(rig, RadioCheckState.Ok));

            FailTheConnect(tray);

            Assert.AreEqual(ConnectInWindowsOffer.ButtonLabel, tray.Cards.Shown[^1].Content.Action?.Label);
        });
    }

    [TestMethod]
    public void ASuccessfulConnectNeverReadsTheRadio()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            var radio = new CountingRadio();
            PairAirPodsServices services = new()
            {
                Api = rig.Pairing, Nodes = rig.Nodes, Prompter = rig.Prompter, UiFactory = _ => rig.Ui, Radio = radio,
                Options = rig.Host().Options, LockName = @"Local\Earshot.Tests.Radio." + Guid.NewGuid().ToString("N"),
            };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: services);

            tray.Context.OnIconMouseClick(null, Press(MouseButtons.Left));
            tray.PumpUntilIdle();

            Assert.AreEqual(0, radio.Reads);
        });
    }

    private sealed class CountingRadio : IRadioCheck
    {
        public int Reads { get; private set; }

        public Task<RadioCheckResult> CheckAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Pairing.PairingSessionTests.Radio(RadioCheckState.Ok));
        }
    }
}
