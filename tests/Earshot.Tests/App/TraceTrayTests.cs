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

    private static readonly TraceResult Saved = new(TraceOutcome.Saved, TraceCopy.Saved, @"C:\Users\Someone\Documents\Earshot\Bluetooth traces", null);

    [TestMethod]
    public void TheSettingsRowStartsARecordingShowsStopWhileItRunsAndTheSavedLineAtTheEnd()
    {
        StaThread.Run(() =>
        {
            var recorder = new FakeTraceRecorder();
            using var tray = new TrayHarness(snapshot: Target(ConnectionState.Disconnected), traceRecorder: recorder);

            tray.Context.RecordTraceFromSettings();
            Assert.AreEqual(1, recorder.Recorded);

            recorder.BeginRecording();
            WaitFor(() => tray.Cards.Shown.Any(c => c.Content.Status == TraceCopy.Running), "the running card");
            CardContent running = tray.Cards.Shown[^1].Content;
            Assert.AreEqual(TraceCopy.Running, running.Status);
            Assert.AreEqual(TraceCopy.StopButton, running.Action?.Label);

            running.Action!.OnClick();
            Assert.AreEqual(1, recorder.Stopped);

            recorder.Finish(Saved);
            WaitFor(() => tray.Cards.Shown.Any(c => c.Content.Status == TraceCopy.Saved), "the saved card");
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
            rig.Pairing.PairResult = new PairingCallResult("HardwareFailure", PairReasons.HardwareFailure, false, null);
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
            Assert.AreEqual(RadioLines.Unreachable, card.Status);
            Assert.AreEqual(RadioLines.RecordTraceButton, card.Action?.Label);

            card.Action!.OnClick();
            tray.PumpUntilIdle();

            Assert.AreEqual(1, recorder.Recorded);
        });
    }
}
