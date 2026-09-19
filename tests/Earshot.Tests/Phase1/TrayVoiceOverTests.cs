using System.Reflection;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Tray;
using Earshot.Voice;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// The tray's VoiceOver wiring: the SpeakStatus hotkey (already defined by the v1.1 hotkeys feature,
// Earshot.Hotkeys.HotkeyAction.SpeakStatus) speaks the tray's own current state and starts no operation,
// the menu tick is the only way to turn speech on, defaults leave the engine unconstructed, and every
// exit path stops and disposes it. TrayHarness injects a FakeSpeechEngine (TrayStartOptions.
// VoiceEngineFactory), so nothing here constructs a real SpeechSynthesizer.
[TestClass]
public sealed class TrayVoiceOverTests
{
    [TestMethod]
    public void TheAnnounceMemberIsTheOnlyWayInNoPublicMemberOnIAnnouncerTakesAString()
    {
        // The interface, not a mock of it: proves the compiled API surface, not a belief about it. A
        // future change that widens IAnnouncer with a string-taking member fails this at once.
        MethodInfo[] methods = typeof(IAnnouncer).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        foreach (MethodInfo method in methods)
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Assert.AreNotEqual(typeof(string), parameter.ParameterType,
                    "IAnnouncer." + method.Name + " takes a string; Announce(VoiceLine) must be the only way in.");
            }
        }
    }

    [TestMethod]
    public void DisabledByDefaultMeansTheEngineIsNeverConstructed()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness();
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Settings.Current.VoiceOver.Enabled, "Defaults must be off.");
            Assert.IsFalse(tray.Voice.IsOpen, "Open must never be called while VoiceOver is off.");
        });
    }

    [TestMethod]
    public void TheSpeakStatusMenuItemTurnsSpeechOnAndOffAndShowsACard()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected));
            tray.PumpUntilIdle();

            tray.ClickMenu(MenuModel.SpeakStatusText);
            tray.PumpUntilIdle();

            Assert.IsTrue(tray.Settings.Current.VoiceOver.Enabled);
            Assert.IsTrue(tray.Voice.IsOpen, "Turning the menu tick on must open the engine.");
            Assert.AreEqual(AnnouncerCopy.SpeechOn, tray.Cards.Shown[^1].Content.Status);

            tray.ClickMenu(MenuModel.SpeakStatusText);
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Settings.Current.VoiceOver.Enabled);
            Assert.AreEqual(AnnouncerCopy.SpeechOff, tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void TheSpeakStatusHotkeySpeaksTheCurrentStateAndStartsNoOperation()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected), settings: s => s.VoiceOver = s.VoiceOver with { Enabled = true });
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Voice.IsOpen, "The announcer must have opened for this test to prove anything.");

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SpeakStatus));
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Voice.SpeakCompleted.Wait(TimeSpan.FromSeconds(5)), "SpeakCompleted never arrived.");

            CollectionAssert.Contains((System.Collections.ICollection)tray.Voice.Utterances, "Connected");
            Assert.IsEmpty(tray.Connection.Calls, "SpeakStatus must never itself start a connect or disconnect.");
            Assert.IsFalse(tray.Block.Calls.Contains(GateVerbs.SetBootOn) || tray.Block.Calls.Contains(GateVerbs.SetBootOff),
                "SpeakStatus must never itself start a gate change.");
        });
    }

    [TestMethod]
    public void TheSpeakStatusHotkeyDoesNothingWhenSpeechIsOff()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Connected));
            tray.PumpUntilIdle();

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SpeakStatus));
            tray.PumpUntilIdle();

            Assert.IsEmpty(tray.Voice.Utterances);
        });
    }

    [TestMethod]
    public void ClosingStopsAndDisposesTheEngine()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s => s.VoiceOver = s.VoiceOver with { Enabled = true });
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Voice.IsOpen);

            tray.Context.Dispose();

            Assert.AreEqual(1, tray.Voice.DisposeCount, "Close must stop and dispose the announcer on every exit path it handles.");
        });
    }

    [TestMethod]
    public void ANoVoiceOpenDisablesTheMenuItemAndShowsTheNoVoiceCard()
    {
        StaThread.Run(() =>
        {
            var engine = new Earshot.Tests.Voice.FakeSpeechEngine { OpenOutcome = StepOutcomes.NotAvailable("open", "no enabled voice") };
            using var tray = new TrayHarness(settings: s => s.VoiceOver = s.VoiceOver with { Enabled = true }, voiceEngine: engine);
            tray.PumpUntilIdle();

            Assert.AreEqual(AnnouncerCopy.NoVoiceInstalled, tray.Cards.Shown[^1].Content.Status);
            tray.Context.Menu.Refresh();
            ToolStripMenuItem item = tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.SpeakStatusNoVoice);
            Assert.IsFalse(item.Enabled);
        });
    }
}
