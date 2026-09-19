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

    // Broadened from IAnnouncer alone: the claim in VoiceLine.cs ("no free string can reach the
    // synthesiser") is about the whole Voice namespace, not just one interface, and ISpeechEngine.Speak
    // used to be exactly the counterexample (public, took a string) until it and SystemSpeechEngine were
    // made internal. This proves the compiled, public API surface of the namespace as it now stands, not
    // a belief about it: a future type added there with a public string-taking member fails this at
    // once. VoiceOverSettings and AnnouncerCopy are excluded because they hold strings as data (a stored
    // setting, fixed copy) and never pass a caller's string into the synthesiser.
    [TestMethod]
    public void NoPublicTypeInTheVoiceNamespaceHasAPublicMemberThatTakesAFreeString()
    {
        Type[] excluded = [typeof(VoiceOverSettings), typeof(AnnouncerCopy)];
        Type[] voiceTypes = typeof(IAnnouncer).Assembly.GetTypes()
            .Where(t => t.Namespace == "Earshot.Voice" && t.IsPublic && !excluded.Contains(t))
            .ToArray();

        // Guards the test itself: if the namespace filter ever stops matching anything (a rename, a
        // moved file), this must fail loudly rather than pass by finding nothing to check.
        Assert.IsTrue(voiceTypes.Length > 0, "No public types were found in Earshot.Voice; the namespace filter is wrong.");

        foreach (Type type in voiceTypes)
        {
            // DeclaredOnly: every enum inherits Enum.ToString(string?) and Object/ValueType members such
            // as Equals(object), none of which are a call site this namespace controls or that could ever
            // reach the synthesiser; only members this namespace itself declares are in scope.
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                {
                    // Property accessors and operators, not a free-form call site.
                    continue;
                }

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.AreNotEqual(typeof(string), parameter.ParameterType,
                        type.Name + "." + method.Name + " takes a string; Announce(VoiceLine) must be the only way into the speech worker.");
                }
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
    public void TheSpeakStatusHotkeySpeaksDisconnectedExactlyWhenTheSnapshotSaysDisconnected()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), settings: s => s.VoiceOver = s.VoiceOver with { Enabled = true });
            tray.PumpUntilIdle();

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SpeakStatus));
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Voice.SpeakCompleted.Wait(TimeSpan.FromSeconds(5)), "SpeakCompleted never arrived.");

            CollectionAssert.Contains((System.Collections.ICollection)tray.Voice.Utterances, "Disconnected");
        });
    }

    // Connecting, Disconnecting and Unknown are states the app only assumed (a read in flight, or no
    // read at all): the voiceover design says never to announce one of those, and the closed six-phrase
    // set has no line for any of them either way.
    [TestMethod]
    [DataRow(ConnectionState.Connecting)]
    [DataRow(ConnectionState.Disconnecting)]
    [DataRow(ConnectionState.Unknown)]
    public void TheSpeakStatusHotkeySaysNothingForAStateTheAppOnlyAssumed(ConnectionState connection)
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(connection), settings: s => s.VoiceOver = s.VoiceOver with { Enabled = true });
            tray.PumpUntilIdle();

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SpeakStatus));
            tray.PumpUntilIdle();

            Assert.IsEmpty(tray.Voice.Utterances, connection + " must never be spoken as Connected or Disconnected.");
            Assert.IsTrue(tray.Log.Has(LogLevel.Debug, "nothing spoken, state is " + connection),
                "Nothing being spoken must be recorded, not silent.");
        });
    }

    [TestMethod]
    public void TheSpeakStatusHotkeyWithNoDeviceSpeaksNothingAndShowsTheTooltipTextOnACard()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: NoDevice(), settings: s => s.VoiceOver = s.VoiceOver with { Enabled = true });
            tray.PumpUntilIdle();
            int cardsBefore = tray.Cards.Shown.Count;

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SpeakStatus));
            tray.PumpUntilIdle();

            Assert.IsEmpty(tray.Voice.Utterances);
            Assert.IsTrue(tray.Cards.Shown.Count > cardsBefore, "Saying nothing must still show the owner the same short state the tooltip would.");
            string expected = TrayStatus.Tooltip(tray.Monitor.Current, tray.Coordinator.BlockStatus, tray.Settings.Current);
            Assert.AreEqual(expected, tray.Cards.Shown[^1].Content.Status);
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
    public void ClosingWhileTheWorkerIsStuckInSpeakLogsTheTimedOutStopInsteadOfDiscardingIt()
    {
        StaThread.Run(() =>
        {
            var engine = new Earshot.Tests.Voice.FakeSpeechEngine { OpenGateByDefault = false };
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Connected),
                settings: s => s.VoiceOver = s.VoiceOver with { Enabled = true, ShutdownWaitMilliseconds = 0 },
                voiceEngine: engine);
            tray.PumpUntilIdle();

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SpeakStatus));
            tray.PumpUntilIdle();
            Assert.IsTrue(engine.SpeakEntered.Wait(TimeSpan.FromSeconds(5)), "SpeakEntered never arrived.");
            // The worker is now stuck inside Speak, gate closed.

            tray.Context.Dispose();

            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, "worker still speaking"),
                "StopVoice must drain and log the timed-out stop outcome instead of discarding it.");

            engine.ReleaseGate.Release(); // let the background worker finish so nothing is left running
        });
    }

    [TestMethod]
    public void ClosingLogsASpeakFailureThatHadNotBeenDrainedYet()
    {
        StaThread.Run(() =>
        {
            var engine = new Earshot.Tests.Voice.FakeSpeechEngine();
            var thrown = new InvalidOperationException("synth exploded");
            engine.ThrowOnNextSpeaks(1, thrown);
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Connected),
                settings: s => s.VoiceOver = s.VoiceOver with { Enabled = true, FailuresBeforeGivingUp = 10 },
                voiceEngine: engine);
            tray.PumpUntilIdle();

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SpeakStatus));
            tray.PumpUntilIdle();
            Assert.IsTrue(engine.SpeakCompleted.Wait(TimeSpan.FromSeconds(5)), "SpeakCompleted never arrived.");

            // The failure was recorded on the worker thread after AnnounceIfEnabled's own immediate
            // Drain() ran, so it is still sitting undrained at this point: only StopVoice's own drain, on
            // close, can pick it up.
            tray.Context.Dispose();

            Assert.IsTrue(tray.Log.Has(LogLevel.Warn, nameof(InvalidOperationException)),
                "StopVoice must drain and log a speak failure that had not been drained yet.");
        });
    }

    [TestMethod]
    public void ClosingIsBoundedByShutdownWaitMillisecondsEvenWhenTheWorkerIsStuckInSpeak()
    {
        StaThread.Run(() =>
        {
            var engine = new Earshot.Tests.Voice.FakeSpeechEngine { OpenGateByDefault = false };
            using var tray = new TrayHarness(
                snapshot: Target(ConnectionState.Connected),
                settings: s => s.VoiceOver = s.VoiceOver with { Enabled = true, ShutdownWaitMilliseconds = 200 },
                voiceEngine: engine);
            tray.PumpUntilIdle();

            tray.Context.OnHotkeyActivated(null, new HotkeyActivatedEventArgs(HotkeyAction.SpeakStatus));
            tray.PumpUntilIdle();
            Assert.IsTrue(engine.SpeakEntered.Wait(TimeSpan.FromSeconds(5)), "SpeakEntered never arrived.");
            // The worker is stuck inside Speak for the rest of this test.

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            tray.Context.Dispose();
            stopwatch.Stop();

            // Close/StopVoice is documented and accepted to block the UI thread for up to
            // ShutdownWaitMilliseconds when the worker cannot be joined (see the comment on StopVoice's
            // call site in Close()); this confirms the bound actually holds rather than the exit path
            // hanging on a worker wedged in Speak.
            Assert.IsTrue(stopwatch.ElapsedMilliseconds < 5000,
                "Close must not be held far longer than ShutdownWaitMilliseconds by a worker stuck in Speak; took " +
                stopwatch.ElapsedMilliseconds + "ms.");

            engine.ReleaseGate.Release(); // let the background worker finish so nothing is left running
        });
    }

    [TestMethod]
    public void AStopFromAnAnnouncerThatIsNoLongerCurrentIsIgnored()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(settings: s => s.VoiceOver = s.VoiceOver with { Enabled = true });
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Voice.IsOpen);

            int cardsBefore = tray.Cards.Shown.Count;
            bool enabledBefore = tray.Settings.Current.VoiceOver.Enabled;

            // A stale outcome, as if it arrived from an announcer that has already been replaced (a fast
            // settings change racing a worker that had already decided to give up): OnVoiceStopped is
            // internal for exactly this, the same seam OnHotkeyActivated and OnSessionEnding already
            // are. The sender is deliberately not the tray's current announcer.
            tray.Context.OnVoiceStopped(new object(), StepOutcomes.NotAttempted("speak", "stale"));
            tray.PumpUntilIdle();

            Assert.AreEqual(enabledBefore, tray.Settings.Current.VoiceOver.Enabled, "A stale Stopped event must not turn the current speech off.");
            Assert.AreEqual(cardsBefore, tray.Cards.Shown.Count, "A stale Stopped event must not show a card.");
            Assert.IsTrue(tray.Voice.IsOpen, "A stale Stopped event must not dispose the current engine.");
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
