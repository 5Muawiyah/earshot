using System.Collections;
using System.Text.RegularExpressions;
using Earshot.Contracts;
using Earshot.Voice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Voice;

// The acceptance tests for the voiceover feature, ported against SpeechAnnouncer and
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

        // SpeakCompleted has not fired: the worker is still blocked inside Speak, not finished, proving
        // Announce returned while the engine is still speaking. (ReleaseGate.CurrentCount is 0 here
        // regardless of anything the code under test does, since nothing has released it yet, so that
        // was not actually testing the "still speaking" claim.)
        Assert.AreEqual(0, engine.SpeakCompleted.CurrentCount, "The worker must still be inside Speak.");

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

        // The two dropped lines (Disconnected, superseded when BlockedAtBoot arrived; BlockedAtBoot,
        // superseded when AllowedAtBoot arrived) must themselves be recorded, not just silently replaced
        // in the mailbox: deleting the Record(...) call for a superseded line must not leave this green.
        int superseded = announcer.Drain().Count(o => o.Step == "announce" && !o.Ok && o.Detail == "superseded");
        Assert.AreEqual(2, superseded, "Both dropped lines must be recorded as superseded.");
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
    public void RepeatGapUsesTheMonotonicClockNotTheWallClock()
    {
        var engine = new FakeSpeechEngine();
        var time = new ManualTime();
        var settings = VoiceOverSettings.Default with { Enabled = true, RepeatGapMilliseconds = 2000 };
        using var announcer = new SpeechAnnouncer(engine, settings, time);
        Assert.IsTrue(announcer.Start().Ok);

        Assert.IsTrue(announcer.Announce(VoiceLine.Connected).Ok);
        Wait(engine.SpeakCompleted, "SpeakCompleted (first)");

        // The wall clock steps back a full day, as a system clock correction (NTP, the owner's own
        // change) could do, while no real time passes at all.
        time.StepWallClockBackwardOnly(TimeSpan.FromDays(1));

        // Real (monotonic) time now advances well past the repeat gap. A wall-clock-based gap would
        // still see "now" more than a day before the last accepted time and refuse forever; the repeat
        // gap must be measured on the monotonic clock, which this step never touched.
        time.Advance(TimeSpan.FromMilliseconds(2001));

        StepOutcome second = announcer.Announce(VoiceLine.Connected);
        Assert.IsTrue(second.Ok, "The repeat gap must be measured on the monotonic clock, not the wall clock.");
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
        Assert.AreEqual(NativeCodes.NotAttempted, result.Code);
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

        // The next line is still spoken: one failure does not stop the worker. Waiting for it to
        // complete before draining also closes a race that a fix here found by execution: the fake
        // signals SpeakCompleted from inside Speak, before Speak returns to RunWorker, which only then
        // takes the lock and calls Record(outcome) for the failure. Draining right after the first
        // SpeakCompleted could still race that Record call. RunWorker processes one line fully, Record
        // included, before it can pick up the next, so once the second SpeakCompleted has fired, the
        // first line's Record call is guaranteed to have already run on that same worker thread, and
        // Drain below cannot race it.
        Assert.IsTrue(announcer.Announce(VoiceLine.Disconnected).Ok);
        Wait(engine.SpeakCompleted, "SpeakCompleted (next line)");
        CollectionAssert.Contains((ICollection)engine.Utterances, "Disconnected");

        StepOutcome? failure = announcer.Drain().LastOrDefault(o => o.Step == "speak" && !o.Ok);
        Assert.IsNotNull(failure);
        Assert.AreEqual(thrown.HResult, failure!.Code);
        Assert.AreEqual(nameof(InvalidOperationException), failure.CodeName);
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
    public void ConcurrentStopSpeakingCallsDisposeTheEngineExactlyOnce()
    {
        // The worker is held inside Speak (gate closed) until both stop threads have already passed the
        // barrier and read _worker, and ShutdownWaitMilliseconds is generous: both concurrent Join calls
        // can then succeed once the gate is released below, which is what actually exercises the bug (a
        // short-lived, already-idle worker lets both Joins return true almost immediately regardless of
        // whether the fix is in place, so it would not reliably turn red on the old code).
        var engine = new FakeSpeechEngine { OpenGateByDefault = false };
        var settings = VoiceOverSettings.Default with { Enabled = true, ShutdownWaitMilliseconds = 5000 };
        var announcer = new SpeechAnnouncer(engine, settings, new ManualTime());
        Assert.IsTrue(announcer.Start().Ok);

        announcer.Announce(VoiceLine.Connected);
        Wait(engine.SpeakEntered, "SpeakEntered"); // worker is blocked inside Speak, gate closed

        // Two threads race to stop the same announcer, the way an exit path and a settings change could
        // overlap. _worker is claimed under the lock (see StopSpeaking's comment), so only one of them
        // can ever see a non-null worker to join and dispose. The gate stays closed until both threads
        // have started their own StopSpeaking call, so both are blocked inside their own Join (the
        // worker cannot finish yet) before either can succeed: that is what actually exercises the bug,
        // since both Joins are then released together when the gate opens and the worker finishes, each
        // able to see the same pre-read worker reference and proceed to dispose. Releasing the gate
        // immediately after starting the two tasks, with nothing blocking either Join, lets one call
        // finish and null _worker before the other has even read it, which does not reproduce the race.
        using var ready = new Barrier(2);
        using var bothStarted = new CountdownEvent(2);
        void StopFromAnotherThread()
        {
            ready.SignalAndWait();
            bothStarted.Signal();
            announcer.StopSpeaking();
        }

        Task first = Task.Run(StopFromAnotherThread);
        Task second = Task.Run(StopFromAnotherThread);
        Assert.IsTrue(bothStarted.Wait(WaitTimeout), "Both stop threads never started.");
        engine.ReleaseGate.Release(); // let the in-flight "Connected" finish so both Joins can succeed together
        Assert.IsTrue(Task.WaitAll([first, second], WaitTimeout), "Both concurrent StopSpeaking calls must return.");

        Assert.AreEqual(1, engine.DisposeCount, "Two concurrent StopSpeaking calls must not both dispose the engine.");

        announcer.Dispose();
        Assert.AreEqual(1, engine.DisposeCount, "Dispose afterwards must not dispose the engine a second time either.");
    }

    // Both tests below exercise StopSpeaking and Dispose racing on a never-started announcer, tens of
    // thousands of times each with a fresh announcer and a barrier forcing both calls to start together.
    // Neither could be made to fail on the old code (StopSpeaking's own _signal.Set() outside its lock and
    // before the "worker is null" check; Dispose's own "if (_disposed) return; _disposed = true;" outside
    // any lock): a standalone probe against a raw System.Threading.ManualResetEventSlim on this machine
    // (.NET 10, 200000 attempts each) found that calling Set() after Dispose(), and calling Dispose()
    // concurrently from two threads, both come back with no exception here, so the ObjectDisposedException
    // the old code risked never actually surfaced in either of these tests on the old code either. That
    // contradicts what the class this defect is filed under assumed; the probe is recorded here rather
    // than silently trusting the assumption. Microsoft's own remarks on IDisposable.Dispose still warn
    // that a disposed object's other methods may throw, an allowed outcome this runtime just does not
    // currently produce for this type, so the fix (both mutations now share the one lock, which removes
    // the data race outright rather than leaning on that observed but undocumented tolerance) stays in:
    // it is defended by code reasoning and by Microsoft's own contract for Dispose, not by an execution
    // that could be forced red here. These two tests still assert what must hold regardless (no exception,
    // no deadlock under a Join timeout) so a regression that reintroduces an actual throw is still caught.
    [TestMethod]
    public void StopSpeakingNeverRacesDisposeOfANeverStartedAnnouncer()
    {
        var engine = new FakeSpeechEngine();
        var settings = VoiceOverSettings.Default with { Enabled = true };
        for (int attempt = 0; attempt < 2000; attempt++)
        {
            var announcer = new SpeechAnnouncer(engine, settings, new ManualTime());
            using var ready = new Barrier(2);
            Exception? stopException = null;
            Exception? disposeException = null;

            // Dedicated OS threads, not the thread pool: a pooled Task.Run can be handed to a thread the
            // pool already warmed up for the previous attempt, which serialises the two calls often
            // enough that this never lands in the actual race window.
            var stopper = new Thread(() =>
            {
                ready.SignalAndWait();
                try { announcer.StopSpeaking(); }
                catch (Exception ex) { stopException = ex; }
            });
            var disposer = new Thread(() =>
            {
                ready.SignalAndWait();
                try { announcer.Dispose(); }
                catch (Exception ex) { disposeException = ex; }
            });

            stopper.Start();
            disposer.Start();
            Assert.IsTrue(stopper.Join(WaitTimeout), "The StopSpeaking thread never finished (attempt " + attempt + ").");
            Assert.IsTrue(disposer.Join(WaitTimeout), "The Dispose thread never finished (attempt " + attempt + ").");
            Assert.IsNull(stopException, "StopSpeaking must never throw racing a Dispose of a never-started announcer (attempt " + attempt + "): " + stopException);
            Assert.IsNull(disposeException, "Dispose must never throw racing a StopSpeaking of a never-started announcer (attempt " + attempt + "): " + disposeException);
        }
    }

    [TestMethod]
    public void ConcurrentDisposeCallsOnANeverStartedAnnouncerNeverThrow()
    {
        var engine = new FakeSpeechEngine();
        var settings = VoiceOverSettings.Default with { Enabled = true };
        for (int attempt = 0; attempt < 2000; attempt++)
        {
            var announcer = new SpeechAnnouncer(engine, settings, new ManualTime());
            using var ready = new Barrier(2);
            Exception? firstException = null;
            Exception? secondException = null;

            // Dedicated OS threads, not the thread pool: see StopSpeakingNeverRacesDisposeOfANever
            // StartedAnnouncer's comment on why Task.Run did not reliably land in the race window.
            var first = new Thread(() =>
            {
                ready.SignalAndWait();
                try { announcer.Dispose(); }
                catch (Exception ex) { firstException = ex; }
            });
            var second = new Thread(() =>
            {
                ready.SignalAndWait();
                try { announcer.Dispose(); }
                catch (Exception ex) { secondException = ex; }
            });

            first.Start();
            second.Start();
            Assert.IsTrue(first.Join(WaitTimeout), "The first Dispose thread never finished (attempt " + attempt + ").");
            Assert.IsTrue(second.Join(WaitTimeout), "The second Dispose thread never finished (attempt " + attempt + ").");
            Assert.IsNull(firstException, "A concurrent Dispose must never throw (attempt " + attempt + "): " + firstException);
            Assert.IsNull(secondException, "A concurrent Dispose must never throw (attempt " + attempt + "): " + secondException);
        }
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
        Assert.AreEqual(NativeCodes.NotAttempted, result.Code);
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
    public void ShutdownWaitMillisecondsIsClampedToTheDocumentedRange()
    {
        VoiceOverSettings tooLow = VoiceOverSettings.Default with { ShutdownWaitMilliseconds = 1 };
        VoiceOverSettings tooHigh = VoiceOverSettings.Default with { ShutdownWaitMilliseconds = 999999 };

        VoiceOverSettings clampedLow = tooLow.Clamped(out IReadOnlyList<StepOutcome> lowNotes);
        VoiceOverSettings clampedHigh = tooHigh.Clamped(out IReadOnlyList<StepOutcome> highNotes);

        Assert.AreEqual(100, clampedLow.ShutdownWaitMilliseconds);
        Assert.AreEqual(1, lowNotes.Count);
        Assert.AreEqual(10000, clampedHigh.ShutdownWaitMilliseconds);
        Assert.AreEqual(1, highNotes.Count);
    }

    [TestMethod]
    public void FailuresBeforeGivingUpIsClampedToTheDocumentedRange()
    {
        VoiceOverSettings tooLow = VoiceOverSettings.Default with { FailuresBeforeGivingUp = 0 };
        VoiceOverSettings tooHigh = VoiceOverSettings.Default with { FailuresBeforeGivingUp = 99 };

        VoiceOverSettings clampedLow = tooLow.Clamped(out IReadOnlyList<StepOutcome> lowNotes);
        VoiceOverSettings clampedHigh = tooHigh.Clamped(out IReadOnlyList<StepOutcome> highNotes);

        Assert.AreEqual(1, clampedLow.FailuresBeforeGivingUp);
        Assert.AreEqual(1, lowNotes.Count);
        Assert.AreEqual(10, clampedHigh.FailuresBeforeGivingUp);
        Assert.AreEqual(1, highNotes.Count);
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
