using System.Windows.Forms;
using Earshot.App;
using Earshot.Bluetooth;
using Earshot.Contracts;
using Earshot.Tests.Phase1;
using Earshot.Tests.Repair;
using Earshot.Tracing;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Earshot.Pairing;
using Earshot.Repair;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.App;

// Records what the tray asked of the recorder; starts nothing, asks for no administrator prompt and writes no file.
internal sealed class FakeTraceRecorder : ITraceRecorder
{
    private readonly TaskCompletionSource<TraceResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TraceState State { get; private set; }

    public event EventHandler? StateChanged;

    public int Recorded { get; private set; }

    public int Stopped { get; private set; }

    public string? Notice { get; set; }

    public bool NeedsSetUp { get; set; }

    public string DestinationFolder { get; set; } = @"C:\Users\Someone\AppData\Local\Earshot\traces";

    public string? Leftover { get; set; }

    public string? LeftoverNotice() => Leftover;

    public Task<TraceResult> RecordAsync(CancellationToken ct)
    {
        Recorded++;
        return _done.Task;
    }

    // The helper is up and recording.
    public void BeginRecording()
    {
        State = TraceState.Recording;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Finish(TraceResult result)
    {
        State = TraceState.Idle;
        StateChanged?.Invoke(this, EventArgs.Empty);
        _done.TrySetResult(result);
    }

    public void Stop() => Stopped++;

    public string? StillRunningNotice() => Notice;

    public void Dispose()
    {
    }
}

// Record a Bluetooth trace from the tray: the settings page row, the card that says the AirPods could not be reached, and the
// notice at start about a trace an earlier helper left running. The recorder is faked at its boundary; the real one has its own
// tests, and the one prompt end to end is a live check.
[TestClass]
public sealed class TraceTrayTests
{
    // The tray's continuations come back through the message loop, so the test pumps it until the card it waits for is there.
    private static void WaitFor(Func<bool> condition, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("Timed out waiting for " + what + ".");
            }

            Application.DoEvents();
            Thread.Sleep(1);
        }
    }

    private const string Folder = @"C:\Users\Someone\AppData\Local\Earshot\traces";
    private static readonly TraceResult Saved = new(TraceOutcome.Saved, TraceCopy.SavedIn(Folder), Folder, null);

    // The note every way in shows first. Nothing is asked of the recorder until its Record button is pressed.
    private static void PressRecordOnTheNote(TrayHarness tray, FakeTraceRecorder recorder)
    {
        string text = TraceCopy.NoteFor(recorder.DestinationFolder);
        WaitFor(() => tray.Cards.Shown.Any(c => c.Content.Status == text), "the note");
        CardContent note = tray.Cards.Shown.Last(c => c.Content.Status == text).Content;
        Assert.AreEqual(TraceCopy.NoteButton, note.Action?.Label);
        Assert.AreEqual(0, recorder.Recorded, "Nothing is recorded before the note's button is pressed.");
        note.Action!.OnClick();
    }

    [TestMethod]
    public void TheSettingsRowStartsARecordingShowsStopWhileItRunsAndTheSavedLineAtTheEnd()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);

            tray.Context.RecordTraceFromSettings();
            PressRecordOnTheNote(tray, recorder);
            Assert.AreEqual(1, recorder.Recorded);

            recorder.BeginRecording();
            WaitFor(() => tray.Cards.Shown.Any(c => c.Content.Status == TraceCopy.Running), "the running card");
            CardContent running = tray.Cards.Shown[^1].Content;
            Assert.AreEqual(TraceCopy.Running, running.Status);
            Assert.AreEqual(TraceCopy.StopButton, running.Action?.Label);

            running.Action!.OnClick();
            Assert.AreEqual(1, recorder.Stopped);

            recorder.Finish(Saved);
            WaitFor(() => tray.Cards.Shown.Any(c => c.Content.Status == TraceCopy.SavedIn(Folder)), "the saved card");
        });
    }

    // The result card held back by Windows (a full-screen app when the recording ended): the running card, whose Stop would do
    // nothing now, is taken down rather than left up.
    [TestMethod]
    public void WhenTheResultCardIsHeldBackTheRunningCardIsTakenDown()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);
            tray.Context.RecordTraceFromSettings();
            PressRecordOnTheNote(tray, recorder);
            recorder.BeginRecording();
            WaitFor(() => tray.Cards.Shown.Any(c => c.Content.Status == TraceCopy.Running), "the running card");
            int hides = tray.Cards.Hides;
            tray.Cards.HoldBackNearTray = true;

            recorder.Finish(Saved);
            WaitFor(() => tray.Cards.Hides > hides, "the running card taken down");

            Assert.IsTrue(tray.Cards.HeldBack.Any(c => c.Content.Status == TraceCopy.SavedIn(Folder)));
        });
    }

    // Stop is always within reach: the running card stays up until it is used or replaced, and the settings row's button reads
    // Stop while the trace records. Before, the card closed after twelve seconds and the row still said Record, so a person
    // waiting for the prompt and then pairing found nothing labelled Stop, and the trace ran its full ten minutes. The note,
    // long enough to need its time, stays up too.
    [TestMethod]
    public void StopStaysWithinReachWhileTheTraceRecords()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);

            tray.Context.RecordTraceFromSettings();
            string note = TraceCopy.NoteFor(recorder.DestinationFolder);
            WaitFor(() => tray.Cards.Shown.Any(c => c.Content.Status == note), "the note");
            Assert.IsTrue(tray.Cards.Shown.Last(c => c.Content.Status == note).Content.StaysUp, "The note stays up.");
            Assert.IsFalse(tray.Context.WidgetCardHostForTest.ReadSettings().TraceRecording);
            PressRecordOnTheNote(tray, recorder);

            recorder.BeginRecording();
            WaitFor(() => tray.Cards.Shown.Any(c => c.Content.Status == TraceCopy.Running), "the running card");

            Assert.IsTrue(tray.Cards.Shown.Last(c => c.Content.Status == TraceCopy.Running).Content.StaysUp, "The running card stays up.");
            Assert.IsTrue(tray.Context.WidgetCardHostForTest.ReadSettings().TraceRecording, "The row's button reads Stop.");

            recorder.Finish(Saved);
            WaitFor(() => tray.Cards.Shown.Any(c => c.Content.Status == TraceCopy.SavedIn(Folder)), "the saved card");
            Assert.IsFalse(tray.Context.WidgetCardHostForTest.ReadSettings().TraceRecording, "Record again once it has ended.");
        });
    }

    [TestMethod]
    public void AskingAgainWhileItRecordsStopsItAndStartsNothingNew()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);
            tray.Context.RecordTraceFromSettings();
            PressRecordOnTheNote(tray, recorder);
            recorder.BeginRecording();
            WaitFor(() => tray.Cards.Shown.Any(c => c.Content.Status == TraceCopy.Running), "the running card");

            tray.Context.RecordTraceFromSettings();

            Assert.AreEqual(1, recorder.Recorded);
            Assert.AreEqual(1, recorder.Stopped);
        });
    }

    [TestMethod]
    public void SafeModeRefusesARecordingBeforeAskingTheRecorder()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder();
            using var tray = new TrayHarness(safeMode: true, snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);

            tray.Context.RecordTraceFromSettings();
            tray.PumpUntilIdle();

            Assert.AreEqual(0, recorder.Recorded);
            Assert.AreEqual(TraceCopy.SafeMode, tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void ARecorderThatLeftASessionRunningIsSaidSoOnceAtStart()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder { Notice = TraceCopy.StillRunning };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);

            Assert.AreEqual(1, tray.Cards.Shown.Count(c => c.Content.Status == TraceCopy.StillRunning));
        });
    }

    // Both at once are one card, so the second does not replace the first: the trace that may still run is the one to act on.
    [TestMethod]
    public void ASessionLeftRunningAndRunFoldersLeftAreSaidInOneCard()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder { Notice = TraceCopy.StillRunning, Leftover = TraceCopy.Leftover(1) };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);

            CardContent card = tray.Cards.Shown.Single(c => c.Content.Status.Contains(TraceCopy.StillRunning, StringComparison.Ordinal)).Content;
            StringAssert.Contains(card.Status, TraceCopy.Leftover(1));
            Assert.IsTrue(card.StaysUp, "Shown at sign-in, it stays until it is closed or replaced.");
        });
    }

    [TestMethod]
    public void RunFoldersLeftByAnEarlierRecordingAreSaidOnceAtStart()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder { Leftover = TraceCopy.Leftover(1) };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);

            Assert.AreEqual(1, tray.Cards.Shown.Count(c => c.Content.Status == TraceCopy.Leftover(1)));
        });
    }

    [TestMethod]
    public void WithoutARecorderTheRowSaysSoAndNothingStarts()
    {
        StaThread.Run(() =>
        {
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected));

            tray.Context.RecordTraceFromSettings();
            tray.PumpUntilIdle();

            Assert.AreEqual(TraceTrayText.NotAvailable, tray.Cards.Shown[^1].Content.Status);
        });
    }

    // A pairing that found the AirPods but could not reach them gets a card with the trace button; pressing it records.
    [TestMethod]
    public void ThePairingCardForAnUnreachablePairOffersAndStartsARecording()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice("no-name", "", FixRig.NewAirPodsAddress, true, 6, null)));
            rig.Pairing.PairResult = new PairingCallResult("AuthenticationTimeout", PairReasons.AuthenticationTimeout, false, null);
            rig.Prompter.SayOk();
            var recorder = new FakeTraceRecorder();
            var services = new PairAirPodsServices
            {
                Api = rig.Pairing,
                Nodes = rig.Nodes,
                Prompter = rig.Prompter,
                UiFactory = _ => rig.Ui,
                Radio = new FakeRadioCheck(Pairing.PairingSessionTests.Radio(RadioCheckState.Ok)),
                Options = rig.Host().Options,
                LockName = @"Local\Earshot.Tests.Trace." + Guid.NewGuid().ToString("N"),
            };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: services, traceRecorder: recorder);
            tray.Cards.Shown.Clear();

            tray.Context.Menu.Refresh();
            tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PairAirPods).PerformClick();
            tray.PumpUntilIdle();

            CardContent card = tray.Cards.Shown[^1].Content;
            Assert.AreEqual(RadioLines.UnreachableLine(PairReasons.AuthenticationTimeout), card.Status);
            Assert.AreEqual(RadioLines.RecordTraceButton, card.Action?.Label);

            card.Action!.OnClick();
            tray.PumpUntilIdle();
            PressRecordOnTheNote(tray, recorder);
            tray.PumpUntilIdle();

            Assert.AreEqual(1, recorder.Recorded);

            // The card's button asks for a trace; pressed again while one records, it says so and does not stop it. Before, it
            // was the row's switch and stopped the recording with nothing said.
            recorder.BeginRecording();
            tray.PumpUntilIdle();
            card.Action!.OnClick();
            tray.PumpUntilIdle();

            Assert.AreEqual(0, recorder.Stopped);
            Assert.AreEqual(TraceCopy.AlreadyRecording, tray.Cards.Shown[^1].Content.Status);
        });
    }

    [TestMethod]
    public void TheNoteIsShownFirstAndClosingItRecordsNothing()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);

            tray.Context.RecordTraceFromSettings();
            tray.PumpUntilIdle();

            CardContent note = tray.Cards.Shown[^1].Content;
            Assert.AreEqual(TraceCopy.NoteFor(recorder.DestinationFolder), note.Status);
            StringAssert.Contains(note.Status, recorder.DestinationFolder, "The note says where the trace will be saved.");
            StringAssert.Contains(note.Status, "Bluetooth addresses");
            StringAssert.Contains(note.Status, "pairing keys");
            StringAssert.Contains(note.Status, "never sends it");
            StringAssert.Contains(note.Status, "delete it");
            Assert.AreEqual(0, recorder.Recorded);
        });
    }

    [TestMethod]
    public void WithNoInstalledCopyTheRowSaysToSetUpFirstAndNothingIsAsked()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder { NeedsSetUp = true };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);

            tray.Context.RecordTraceFromSettings();
            tray.PumpUntilIdle();

            Assert.AreEqual(TraceCopy.SetUpFirst, tray.Cards.Shown[^1].Content.Status);
            Assert.IsFalse(tray.Cards.Shown.Any(c => c.Content.Status == TraceCopy.NoteFor(recorder.DestinationFolder)));
            Assert.AreEqual(0, recorder.Recorded);
        });
    }

    [TestMethod]
    public void AFixResultThatAsksForATraceShowsTheSameNote()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);

            tray.Context.RecordTraceFromFixResult();
            PressRecordOnTheNote(tray, recorder);

            Assert.AreEqual(1, recorder.Recorded);
        });
    }

    // The Fix result's button asks for a trace; it is not the row's switch, so while one records it says so and stops nothing.
    [TestMethod]
    public void AFixResultAskingWhileOneRecordsSaysSoAndStopsNothing()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);
            recorder.BeginRecording();
            tray.PumpUntilIdle();

            tray.Context.RecordTraceFromFixResult();
            tray.PumpUntilIdle();

            Assert.AreEqual(TraceCopy.AlreadyRecording, tray.Cards.Shown[^1].Content.Status);
            Assert.AreEqual(0, recorder.Stopped);
            Assert.AreEqual(0, recorder.Recorded);
        });
    }

    // With no installed copy the unreachable card says its line and offers no trace button.
    [TestMethod]
    public void ThePairingCardOffersNoTraceButtonWithoutAnInstalledCopy()
    {
        StaThread.Run(() =>
        {
            using var rig = new FixRig();
            rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice("no-name", "", FixRig.NewAirPodsAddress, true, 6, null)));
            rig.Pairing.PairResult = new PairingCallResult("AuthenticationTimeout", PairReasons.AuthenticationTimeout, false, null);
            rig.Prompter.SayOk();
            var recorder = new FakeTraceRecorder { NeedsSetUp = true };
            var services = new PairAirPodsServices
            {
                Api = rig.Pairing,
                Nodes = rig.Nodes,
                Prompter = rig.Prompter,
                UiFactory = _ => rig.Ui,
                Radio = new FakeRadioCheck(Pairing.PairingSessionTests.Radio(RadioCheckState.Ok)),
                Options = rig.Host().Options,
                LockName = @"Local\Earshot.Tests.Trace." + Guid.NewGuid().ToString("N"),
            };
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), pairAirPods: services, traceRecorder: recorder);
            tray.Cards.Shown.Clear();

            tray.Context.Menu.Refresh();
            tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == MenuModel.PairAirPods).PerformClick();
            tray.PumpUntilIdle();

            CardContent card = tray.Cards.Shown[^1].Content;
            Assert.AreEqual(RadioLines.UnreachableLine(PairReasons.AuthenticationTimeout), card.Status);
            Assert.IsNull(card.Action);
        });
    }
}
