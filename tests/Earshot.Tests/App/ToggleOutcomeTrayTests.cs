using Earshot.App;
using Earshot.Contracts;
using Earshot.Tests.Phase1;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.App;

// What the tray tells of a connect or a disconnect that ended part-way, from the one place every toggle goes through
// (TrayContext.RunToggleAsync). Part-way for a connect is connected and not protected; for a disconnect it is disconnected and the
// block did not take, which leaves the nodes enabled: the PC can then page the AirPods, so it is always told as a failure.
[TestClass]
public sealed class ToggleOutcomeTrayTests
{
    private const string CouldNotBlock = "Could not block the AirPods. Try again.";
    private const string SaidForNotProtected = "Connected but audio quality protection did not apply";

    private static void LeftClick(TrayHarness tray) =>
        tray.Context.OnIconMouseClick(null, new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.Left, 1, 0, 0, 0));

    private static List<GaugeStoryKind> WatchKinds(TrayHarness tray)
    {
        var kinds = new List<GaugeStoryKind>();
        GaugeStoryHost story = tray.Context.GaugeStoryForTest;
        story.Changed += () => kinds.Add(story.Current.Kind);
        return kinds;
    }

    // A disconnect that worked and a block that did not: the nodes stay enabled.
    private static TrayHarness DisconnectWhoseBlockFails()
    {
        var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected), time: new TestTimeProvider(), arrange: t => t.Block.Status = Block(BlockState.Allowed));
        tray.Block.OnBlock = _ => Task.FromResult(ControllerResult.Fail(CouldNotBlock, [StepOutcomes.FromConfigRet("cm-disable", 0x33)]));

        // No card is open to say it, so it is held for the next one (a card that said it would end the cross, which has its own test).
        tray.Toggle.Answer = FailureShown.Held;
        return tray;
    }

    [TestMethod]
    public void ADisconnectWhoseBlockDidNotTakeIsToldAsAFailureOnTheGaugeWhateverBecomesOfItsNotice()
    {
        StaThread.Run(() =>
        {
            using TrayHarness tray = DisconnectWhoseBlockFails();
            List<GaugeStoryKind> kinds = WatchKinds(tray);

            LeftClick(tray);
            tray.PumpUntilIdle();

            CollectionAssert.AreEqual(new[] { GaugeStoryKind.Spinning, GaugeStoryKind.Cross }, kinds, "The cross says it, with no notice needed.");
            Assert.IsFalse(tray.Context.GaugeStoryForTest.Current.Connect, "It is a disconnect's cross.");
            CollectionAssert.AreEqual(new[] { (false, ToggleOutcome.Failed) }, tray.Toggle.Ended);
            Assert.AreEqual(CouldNotBlock, tray.Toggle.Failures.Single().Reason, "and the card says why.");
        });
    }

    // The nodes stay enabled, so the failure must be said even where the cross and the notice cannot be: it is handed to the card as
    // one that has to be held, and it is spoken when spoken status is on.
    [TestMethod]
    public void ADisconnectWhoseBlockDidNotTakeIsHandedToTheCardAsOneThatMustBeSaidAndIsSpoken()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Connected), time: new TestTimeProvider(),
                settings: s => s.VoiceOver = s.VoiceOver with { Enabled = true },
                arrange: t => t.Block.Status = Block(BlockState.Allowed));
            tray.Block.OnBlock = _ => Task.FromResult(ControllerResult.Fail(CouldNotBlock, [StepOutcomes.FromConfigRet("cm-disable", 0x33)]));
            tray.Toggle.Answer = FailureShown.Held;

            LeftClick(tray);
            tray.PumpUntilIdle();

            FailureSaid said = tray.Toggle.Failures.Single();
            Assert.IsTrue(said.MustBeSaid, "A disconnect whose block did not take is held until it has been said.");
            Assert.IsNull(said.NextLabel);
            Assert.IsTrue(tray.Voice.SpeakCompleted.Wait(TimeSpan.FromSeconds(5)), "Something was spoken.");
            Assert.AreEqual("Block failed", string.Join("|", tray.Voice.Utterances));
        });
    }

    [TestMethod]
    public void AConnectThatFailsIsNotHeldOnTheCard()
    {
        StaThread.Run(() =>
        {
            using var connect = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), time: new TestTimeProvider());
            connect.Connection.OnConnect = _ => Task.FromResult(new ConnectResult(ConnectOutcome.AttemptedTimedOut, "Still connecting. Check your AirPods.", []));
            LeftClick(connect);
            connect.PumpUntilIdle();
            Assert.IsFalse(connect.Toggle.Failures.Single().MustBeSaid, "Nothing is at risk when a connect fails.");
        });
    }

    // The gauge may be hidden (a presentation, a full-screen game) for the whole of the cross's hold: the cross waits for a gauge to
    // draw it instead of ending unseen.
    [TestMethod]
    public void TheCrossOfADisconnectWhoseBlockDidNotTakeOutlastsItsHoldWhileNoGaugeHasDrawnIt()
    {
        StaThread.Run(() =>
        {
            var time = new TestTimeProvider();
            using TrayHarness tray = new(snapshot: Target(ConnectionState.Connected), time: time, arrange: t => t.Block.Status = Block(BlockState.Allowed));
            tray.Block.OnBlock = _ => Task.FromResult(ControllerResult.Fail(CouldNotBlock, [StepOutcomes.FromConfigRet("cm-disable", 0x33)]));
            tray.Toggle.Answer = FailureShown.Held;

            LeftClick(tray);
            tray.PumpUntilIdle();
            time.Advance(TimeSpan.FromMinutes(30));
            tray.PumpUntilIdle();

            Assert.AreEqual(GaugeStoryKind.Cross, tray.Context.GaugeStoryForTest.Current.Kind, "No gauge showed it, so it is still owed.");
        });
    }

    [TestMethod]
    public void ACardThatSaysTheBlockFailureEndsTheCrossThatWasOwed()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected), time: new TestTimeProvider(), arrange: t => t.Block.Status = Block(BlockState.Allowed));
            tray.Block.OnBlock = _ => Task.FromResult(ControllerResult.Fail(CouldNotBlock, [StepOutcomes.FromConfigRet("cm-disable", 0x33)]));
            tray.Toggle.Answer = FailureShown.Shown;

            LeftClick(tray);
            tray.PumpUntilIdle();

            Assert.AreEqual(GaugeStoryKind.None, tray.Context.GaugeStoryForTest.Current.Kind, "A card said it, so no cross is owed.");
        });
    }

    [TestMethod]
    public void AConnectedButNotProtectedConnectIsPartialOnTheGaugeAndTheCardAndSpeaksWhatIsTrue()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Disconnected),
                time: new TestTimeProvider(),
                settings: s =>
                {
                    s.ProtectAudioQuality = true;
                    s.VoiceOver = s.VoiceOver with { Enabled = true };
                });
            tray.Protection.Status = Protection(AudioProtectionState.NotProtected);
            tray.Protection.OnApply = (_, _) => Task.FromResult(ControllerResult.Fail("Could not protect audio quality. Try again.",
                [StepOutcomes.FromWin32("bluetooth-set-service-state:111E", 5)]));
            List<GaugeStoryKind> kinds = WatchKinds(tray);

            LeftClick(tray);
            tray.PumpUntilIdle();

            CollectionAssert.AreEqual(new[] { GaugeStoryKind.Spinning, GaugeStoryKind.None }, kinds, "No tick and no cross.");
            CollectionAssert.AreEqual(new[] { (true, ToggleOutcome.Partial) }, tray.Toggle.Ended, "The card is told it ended part-way.");
            FailureSaid said = tray.Toggle.Failures.Single();
            Assert.AreEqual(BlockCoordinator.ConnectedNotProtectedMessage, said.Reason, "and is handed what did not take, to say on the card.");
            Assert.IsNull(said.NextLabel);
            Assert.IsFalse(said.MustBeSaid);
            Assert.IsTrue(tray.Voice.SpeakCompleted.Wait(TimeSpan.FromSeconds(5)), "Something was spoken.");
            Assert.AreEqual(SaidForNotProtected, string.Join("|", tray.Voice.Utterances), "It does not say Connect failed while connected.");
        });
    }
}
