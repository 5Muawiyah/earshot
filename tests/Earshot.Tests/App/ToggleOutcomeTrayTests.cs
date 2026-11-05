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
            CollectionAssert.AreEqual(new[] { (true, ToggleOutcome.Partial) }, tray.Toggle.Ended);
            Assert.IsTrue(tray.Voice.SpeakCompleted.Wait(TimeSpan.FromSeconds(5)), "Something was spoken.");
            Assert.AreEqual(SaidForNotProtected, string.Join("|", tray.Voice.Utterances), "It does not say Connect failed while connected.");
        });
    }
}
