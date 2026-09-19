using System.Collections;
using System.Text.RegularExpressions;
using Earshot.Contracts;
using Earshot.Voice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Voice;

// The acceptance tests from the voiceover design (SPEC.md section 7), ported against SpeechAnnouncer and
// FakeSpeechEngine. No test here constructs a real SpeechSynthesizer, produces sound or touches a real
// audio device: every one uses FakeSpeechEngine and ManualTime. Every wait is on one of the fake's own
// signals with a timeout; a test that announces and then asserts without waiting on a signal races the
// worker (see FakeSpeechEngine's header). Test names follow this repository's own convention (no
// underscores, CA1707), not the spec's literal PhraseTable_CoversEveryVoiceLine style.
[TestClass]
public sealed class SpeechAnnouncerTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] OneUtterance = ["Connected"];
    private static readonly string[] FirstAndLast = ["Connected", "Allowed at boot"];

    [TestMethod]
    public void PhraseTableCoversEveryVoiceLine()
    {
        foreach (VoiceLine line in Enum.GetValues<VoiceLine>())
        {
            Assert.IsFalse(string.IsNullOrEmpty(VoicePhrases.For(line)), line + " has no phrase.");
        }
    }

    [TestMethod]
    public void PhrasesContainOnlyLettersAndSingleSpaces()
    {
        foreach (VoiceLine line in Enum.GetValues<VoiceLine>())
        {
            string phrase = VoicePhrases.For(line);
            Assert.IsTrue(Regex.IsMatch(phrase, "^[A-Za-z]+( [A-Za-z]+)*$"), "\"" + phrase + "\" is not letters and single spaces only.");
        }
    }

    [TestMethod]
    public void AnnounceReturnsWhileEngineIsStillSpeaking()
    {
        var engine = new FakeSpeechEngine { OpenGateByDefault = false };
        using var announcer = new SpeechAnnouncer(engine, VoiceOverSettings.Default with { Enabled = true }, new ManualTime());
        Assert.IsTrue(announcer.Start().Ok);

        announcer.Announce(VoiceLine.Connected);
        Wait(engine.SpeakEntered, "SpeakEntered");

        // The gate is still closed: the worker is blocked inside Speak, and Announce already returned.
        Assert.AreEqual(0, engine.ReleaseGate.CurrentCount);

        engine.ReleaseGate.Release();
        Wait(engine.SpeakCompleted, "SpeakCompleted");
    }

    [TestMethod]
    public void AnnounceSpeaksTheExactPhraseOnce()
    {
        var engine = new FakeSpeechEngine();
        using var announcer = new SpeechAnnouncer(engine, VoiceOverSettings.Default with { Enabled = true }, new ManualTime());
        Assert.IsTrue(announcer.Start().Ok);

        announcer.Announce(VoiceLine.Connected);
        Wait(engine.SpeakCompleted, "SpeakCompleted");

        CollectionAssert.AreEqual(OneUtterance, (ICollection)engine.Utterances);
    }

    [TestMethod]
    public void BurstCoalescesToFirstAndLast()
    {
        var engine = new FakeSpeechEngine { OpenGateByDefault = false };
        var settings = VoiceOverSettings.Default with { Enabled = true, RepeatGapMilliseconds = 0 };
        using var announcer = new SpeechAnnouncer(engine, settings, new ManualTime());
        Assert.IsTrue(announcer.Start().Ok);

        StepOutcome first = announcer.Announce(VoiceLine.Connected);
        Wait(engine.SpeakEntered, "SpeakEntered (first)"); // worker has taken "Connected" out of the mailbox

        StepOutcome second = announcer.Announce(VoiceLine.Disconnected);
        StepOutcome third = announcer.Announce(VoiceLine.BlockedAtBoot);
        StepOutcome fourth = announcer.Announce(VoiceLine.AllowedAtBoot);

        engine.ReleaseGate.Release(); // lets "Connected" finish
        Wait(engine.SpeakCompleted, "SpeakCompleted (first)");
        Wait(engine.SpeakEntered, "SpeakEntered (second)"); // worker has taken "Allowed at boot"
        engine.ReleaseGate.Release();
        Wait(engine.SpeakCompleted, "SpeakCompleted (second)");

        CollectionAssert.AreEqual(FirstAndLast, (ICollection)engine.Utterances);
        Assert.IsTrue(first.Ok);
        Assert.IsTrue(second.Ok);
        Assert.IsTrue(third.Ok, "The middle line (BlockedAtBoot) was accepted before being superseded.");
        Assert.IsTrue(fourth.Ok);
    }

    [TestMethod]
    public void RepeatInsideGapIsDroppedAndAllowedAfterIt()
    {
        var engine = new FakeSpeechEngine();
        var time = new ManualTime();
        var settings = VoiceOverSettings.Default with { Enabled = true, RepeatGapMilliseconds = 2000 };
        using var announcer = new SpeechAnnouncer(engine, settings, time);
        Assert.IsTrue(announcer.Start().Ok);

        Assert.IsTrue(announcer.Announce(VoiceLine.Connected).Ok);
        Wait(engine.SpeakCompleted, "SpeakCompleted (first)");

        StepOutcome repeat = announcer.Announce(VoiceLine.Connected);
        Assert.IsFalse(repeat.Ok);
        Assert.AreEqual(1, engine.Utterances.Count, "The repeat inside the gap must not be spoken.");

        time.Advance(TimeSpan.FromMilliseconds(2001));
        Assert.IsTrue(announcer.Announce(VoiceLine.Connected).Ok);
        Wait(engine.SpeakCompleted, "SpeakCompleted (second)");

        Assert.AreEqual(2, engine.Utterances.Count);
    }

    [TestMethod]
    public void FailureLineIsRefusedWhenSpeakFailuresIsFalse()
    {
        var engine = new FakeSpeechEngine();
        var settings = VoiceOverSettings.Default with { Enabled = true, SpeakFailures = false };
        using var announcer = new SpeechAnnouncer(engine, settings, new ManualTime());
        Assert.IsTrue(announcer.Start().Ok);

        StepOutcome result = announcer.Announce(VoiceLine.ConnectFailed);

        Assert.IsFalse(result.Ok);
        Assert.IsFalse(string.IsNullOrEmpty(result.Detail));
        Assert.AreEqual(0, engine.Utterances.Count);
    }

    [TestMethod]
    public void OpenWithNoVoiceLeavesFeatureUnavailable()
    {
        var engine = new FakeSpeechEngine { OpenOutcome = StepOutcomes.NotAvailable("open", "no enabled voice") };
        using var announcer = new SpeechAnnouncer(engine, VoiceOverSettings.Default with { Enabled = true }, new ManualTime());

        StepOutcome start = announcer.Start();

        Assert.IsFalse(start.Ok);
        Assert.AreEqual(NativeCodes.NotAvailable, start.Code);
        Assert.IsFalse(announcer.IsAvailable);
        StepOutcome announce = announcer.Announce(VoiceLine.Connected);
        Assert.IsFalse(announce.Ok);
        Assert.AreEqual(0, engine.Utterances.Count);
    }

    [TestMethod]
    public void SpeakThrowsIsRecordedWithHResultAndTypeName()
    {
        var engine = new FakeSpeechEngine();
        var thrown = new InvalidOperationException("synth exploded");
        engine.ThrowOnNextSpeaks(1, thrown);
        var settings = VoiceOverSettings.Default with { Enabled = true, FailuresBeforeGivingUp = 10 };
        using var announcer = new SpeechAnnouncer(engine, settings, new ManualTime());
        Assert.IsTrue(announcer.Start().Ok);

        announcer.Announce(VoiceLine.Connected);
        Wait(engine.SpeakCompleted, "SpeakCompleted (failure)");

        StepOutcome? failure = announcer.Drain().LastOrDefault(o => o.Step == "speak" && !o.Ok);
        Assert.IsNotNull(failure);
        Assert.AreEqual(thrown.HResult, failure!.Code);
        Assert.AreEqual(nameof(InvalidOperationException), failure.CodeName);

        // The next line is still spoken: one failure does not stop the worker.
        Assert.IsTrue(announcer.Announce(VoiceLine.Disconnected).Ok);
        Wait(engine.SpeakCompleted, "SpeakCompleted (next line)");
        CollectionAssert.Contains((ICollection)engine.Utterances, "Disconnected");
    }

    [TestMethod]
    public void RepeatedSpeakFailuresStopTheFeature()
    {
        var engine = new FakeSpeechEngine();
        var thrown = new InvalidOperationException("synth exploded");
        engine.ThrowOnNextSpeaks(3, thrown);
        var settings = VoiceOverSettings.Default with { Enabled = true, FailuresBeforeGivingUp = 3, RepeatGapMilliseconds = 0 };
        using var announcer = new SpeechAnnouncer(engine, settings, new ManualTime());
        Assert.IsTrue(announcer.Start().Ok);

        // The worker signals Stopped exactly once, off its own thread, the moment it gives itself up
        // (IAnnouncer.Stopped). Waiting on that, rather than SpeakCompleted or a poll, is what pins the
        // moment IsAvailable actually flips.
        using var gaveUp = new ManualResetEventSlim(false);
        announcer.Stopped += (_, _) => gaveUp.Set();

        announcer.Announce(VoiceLine.Connected);
        Wait(engine.SpeakCompleted, "SpeakCompleted (1)");
        announcer.Announce(VoiceLine.Disconnected);
        Wait(engine.SpeakCompleted, "SpeakCompleted (2)");
        announcer.Announce(VoiceLine.BlockedAtBoot);
        Assert.IsTrue(gaveUp.Wait(WaitTimeout), "Stopped never arrived within " + WaitTimeout + ".");

        Assert.IsFalse(announcer.IsAvailable);
        StepOutcome afterGivingUp = announcer.Announce(VoiceLine.AllowedAtBoot);
        Assert.IsFalse(afterGivingUp.Ok);
        Assert.AreEqual(0, engine.Utterances.Count, "No line was ever spoken: every call threw.");
    }

    [TestMethod]
    public void StopDropsPendingLines()
    {
        var engine = new FakeSpeechEngine { OpenGateByDefault = false };
        // ShutdownWaitMilliseconds 0: StopSpeaking's Join returns at once, false, since the worker is
        // still blocked inside Speak("Connected") behind the closed gate. That keeps the whole test on
        // one thread and fully deterministic: StopSpeaking clears the mailbox and returns before the gate
        // is ever released, so there is no window where the worker could pick B up.
        var settings = VoiceOverSettings.Default with { Enabled = true, ShutdownWaitMilliseconds = 0 };
        using var announcer = new SpeechAnnouncer(engine, settings, new ManualTime());
        Assert.IsTrue(announcer.Start().Ok);

        announcer.Announce(VoiceLine.Connected);
        Wait(engine.SpeakEntered, "SpeakEntered"); // worker is inside Speak("Connected"), gate closed
        announcer.Announce(VoiceLine.Disconnected); // queued in the mailbox, never taken out

        StepOutcome stopped = announcer.StopSpeaking();
        Assert.IsFalse(stopped.Ok, "The worker was still speaking, so the join must time out rather than block the caller.");

        engine.ReleaseGate.Release(); // lets the in-flight "Connected" finish so the background worker can exit
        Wait(engine.SpeakCompleted, "SpeakCompleted");

        CollectionAssert.DoesNotContain((ICollection)engine.Utterances, "Disconnected");
    }

    [TestMethod]
    public void StopDisposesTheEngineExactlyOnce()
    {
        var engine = new FakeSpeechEngine();
        var announcer = new SpeechAnnouncer(engine, VoiceOverSettings.Default with { Enabled = true }, new ManualTime());
        Assert.IsTrue(announcer.Start().Ok);

        announcer.StopSpeaking();
        Assert.AreEqual(1, engine.DisposeCount);

        announcer.Dispose();
        Assert.AreEqual(1, engine.DisposeCount, "Dispose must be safe to call after StopSpeaking, without disposing the engine twice.");
    }

    [TestMethod]
    public void AnnounceAfterStopIsRefused()
    {
        var engine = new FakeSpeechEngine();
        using var announcer = new SpeechAnnouncer(engine, VoiceOverSettings.Default with { Enabled = true }, new ManualTime());
        Assert.IsTrue(announcer.Start().Ok);
        announcer.StopSpeaking();

        StepOutcome result = announcer.Announce(VoiceLine.Connected);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual(0, engine.Utterances.Count);
    }

    [TestMethod]
    public void SettingsAreClampedAndTheClampIsRecorded()
    {
        var settings = VoiceOverSettings.Default with
        {
            Rate = 99,
            Volume = -5,
            RepeatGapMilliseconds = 999999,
        };

        VoiceOverSettings clamped = settings.Clamped(out IReadOnlyList<StepOutcome> notes);

        Assert.AreEqual(10, clamped.Rate);
        Assert.AreEqual(0, clamped.Volume);
        Assert.AreEqual(60000, clamped.RepeatGapMilliseconds);
        Assert.AreEqual(3, notes.Count);
        Assert.IsTrue(notes.All(n => n.Ok), "A clamp is a correction, not a failure.");
    }

    [TestMethod]
    public void DefaultSettingsAreSilent()
    {
        Assert.IsFalse(VoiceOverSettings.Default.Enabled);
    }

    private static void Wait(SemaphoreSlim signal, string name)
    {
        Assert.IsTrue(signal.Wait(WaitTimeout), name + " never arrived within " + WaitTimeout + ".");
    }
}
