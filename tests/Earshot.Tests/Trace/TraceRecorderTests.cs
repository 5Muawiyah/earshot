using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Tracing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Tracing;

internal sealed class FakeTraySignals : ITrayTraceSignals
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool StopRequested { get; private set; }
    public bool CopiedSet { get; private set; }
    public bool Disposed { get; private set; }
    public void Finish() => _done.TrySetResult();
    public void RequestStop() => StopRequested = true;
    public bool IsDone => _done.Task.IsCompleted;

    public bool WaitDone(CancellationToken ct)
    {
        if (_done.Task.IsCompleted)
        {
            return true;
        }

        try
        {
            _done.Task.Wait(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void SetCopied() => CopiedSet = true;
    public void Dispose() => Disposed = true;
}

internal sealed class FakeTraySignalFactory : ITrayTraceSignalsFactory
{
    public FakeTraySignals Signals { get; } = new();
    public List<string> Calls { get; }
    public FakeTraySignalFactory(List<string> calls) => Calls = calls;

    public ITrayTraceSignals Create(string nonce)
    {
        Calls.Add("create " + nonce);
        return Signals;
    }
}

internal sealed class FakeTraceFiles : ITraceUserFiles
{
    public string? ResultText { get; set; }
    public string? CopyProblem { get; set; }
    public string? Marker { get; set; }
    public HashSet<int> Alive { get; } = [];
    public List<string> Calls { get; } = [];
    public string? ReadResult(string nonce) => ResultText;

    public string? Copy(string nonce, string fileName, string destinationFolder, out string copiedTo)
    {
        Calls.Add("copy " + fileName + " to " + destinationFolder);
        copiedTo = Path.Combine(destinationFolder, fileName);
        return CopyProblem;
    }

    public void OpenFolder(string folder) => Calls.Add("open " + folder);
    public string? ReadMarker() => Marker;
    public bool ProcessAlive(int pid) => Alive.Contains(pid);
}

internal sealed class FakeLauncher : IElevatedLauncher
{
    public List<string> Calls { get; }
    public TaskCompletionSource<ElevatedRun> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public FakeLauncher(List<string> calls) => Calls = calls;

    public Task<ElevatedRun> RunAsync(string executable, string arguments, CancellationToken ct)
    {
        Calls.Add("launch " + executable + " " + arguments);
        return Completion.Task;
    }
}

[TestClass]
public sealed class TraceRecorderTests
{
    private const string Nonce = "fedcba9876543210fedcba9876543210";
    private const string Documents = @"C:\Users\p\Documents";
    private static readonly string Saved = TraceResultFile.Serialize(new TraceResultFile(TraceOutcome.Saved, "bluetooth-20261025T143005Z.etl", 0));

    private sealed class Rig
    {
        public List<string> Calls { get; } = [];
        public FakeTraySignalFactory Factory { get; }
        public FakeLauncher Launcher { get; }
        public FakeTraceFiles Files { get; } = new();
        public TraceRecorder Recorder { get; }

        public Rig(bool safeMode = false)
        {
            Factory = new FakeTraySignalFactory(Calls);
            Launcher = new FakeLauncher(Calls);
            Recorder = new TraceRecorder(
                new TraceRecorderOptions(safeMode, @"C:\Program Files\Earshot\Earshot.exe", Documents, 777, () => Nonce),
                Launcher, Factory, Files, new CapturingLog());
        }

        public static async Task Until(Func<bool> condition)
        {
            for (int i = 0; i < 400 && !condition(); i++)
            {
                await Task.Delay(10);
            }

            Assert.IsTrue(condition(), "Timed out waiting for the recorder.");
        }
    }

    [TestMethod]
    public async Task TheSignalsExistBeforeTheHelperIsLaunchedAndTheHelperGetsTheTrayAndTheNonce()
    {
        var rig = new Rig();
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);

        Assert.AreEqual("create " + Nonce, rig.Calls[0]);
        Assert.AreEqual(@"launch C:\Program Files\Earshot\Earshot.exe trace 777 " + Nonce, rig.Calls[1]);
        Assert.AreEqual(TraceState.Recording, rig.Recorder.State);

        rig.Factory.Signals.Finish();
        rig.Files.ResultText = Saved;
        rig.Launcher.Completion.SetResult(new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0)));
        await run;
    }

    [TestMethod]
    public async Task ASavedTraceIsCopiedToDocumentsTheFolderIsOpenedAndTheHelperIsToldItMayGo()
    {
        var rig = new Rig();
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);
        rig.Files.ResultText = Saved;
        rig.Factory.Signals.Finish();
        rig.Launcher.Completion.SetResult(new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0)));

        TraceResult result = await run;

        Assert.AreEqual(TraceOutcome.Saved, result.Outcome);
        string folder = Path.Combine(Documents, "Earshot", "Bluetooth traces");
        CollectionAssert.Contains(rig.Files.Calls, "copy bluetooth-20261025T143005Z.etl to " + folder);
        CollectionAssert.Contains(rig.Files.Calls, "open " + folder);
        Assert.IsTrue(rig.Factory.Signals.CopiedSet);
        Assert.AreEqual(folder, result.FolderPath);
        Assert.AreEqual(TraceCopy.Saved, result.Message);
        Assert.AreEqual(TraceState.Idle, rig.Recorder.State);
        Assert.IsTrue(rig.Factory.Signals.Disposed);
    }

    [TestMethod]
    public async Task ACopyThatFailsLeavesTheFileWithTheHelperAndSaysWhere()
    {
        var rig = new Rig();
        rig.Files.CopyProblem = "disk full";
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);
        rig.Files.ResultText = Saved;
        rig.Factory.Signals.Finish();
        rig.Launcher.Completion.SetResult(new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0)));

        TraceResult result = await run;

        Assert.AreEqual(TraceOutcome.SetupFailed, result.Outcome);
        Assert.IsFalse(rig.Factory.Signals.CopiedSet, "The helper must not delete the only copy.");
        StringAssert.Contains(result.Message, "disk full");
    }

    [TestMethod]
    public async Task ADeclinedPromptIsReportedAndNothingIsCopied()
    {
        var rig = new Rig();
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);
        rig.Launcher.Completion.SetResult(new ElevatedRun(null, StepOutcomes.FromWin32("runas", 1223, "cancelled")));

        TraceResult result = await run;

        Assert.AreEqual(TraceOutcome.Declined, result.Outcome);
        Assert.AreEqual(TraceCopy.Declined, result.Message);
        Assert.AreEqual(0, rig.Files.Calls.Count);
        Assert.AreEqual(TraceState.Idle, rig.Recorder.State);
    }

    [TestMethod]
    public async Task AHelperThatEndedWithoutSayingDoneIsAFailureWithItsExitCode()
    {
        var rig = new Rig();
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);
        rig.Launcher.Completion.SetResult(new ElevatedRun(70, StepOutcomes.FromWin32("runas", 0)));

        TraceResult result = await run;

        Assert.AreEqual(TraceOutcome.HelperFailed, result.Outcome);
        StringAssert.Contains(result.Message, "70");
    }

    [TestMethod]
    public async Task WhenWprIsMissingTheMessageSaysSo()
    {
        var rig = new Rig();
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);
        rig.Files.ResultText = TraceResultFile.Serialize(new TraceResultFile(TraceOutcome.WprMissing, "", 0));
        rig.Factory.Signals.Finish();
        rig.Launcher.Completion.SetResult(new ElevatedRun(69, StepOutcomes.FromWin32("runas", 0)));

        TraceResult result = await run;

        Assert.AreEqual(TraceOutcome.WprMissing, result.Outcome);
        Assert.AreEqual("Windows Performance Recorder isn't on this PC.", result.Message);
    }

    [TestMethod]
    public async Task StopAsksTheHelperToStopThroughTheTraysEvent()
    {
        var rig = new Rig();
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);

        rig.Recorder.Stop();

        Assert.IsTrue(rig.Factory.Signals.StopRequested);
        rig.Files.ResultText = Saved;
        rig.Factory.Signals.Finish();
        rig.Launcher.Completion.SetResult(new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0)));
        await run;
    }

    [TestMethod]
    public async Task DisposingTheRecorderWhileItRecordsStopsTheTrace()
    {
        var rig = new Rig();
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);

        rig.Recorder.Dispose();

        Assert.IsTrue(rig.Factory.Signals.StopRequested);
        rig.Factory.Signals.Finish();
        rig.Launcher.Completion.SetResult(new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0)));
        rig.Files.ResultText = Saved;
        await run;
    }

    [TestMethod]
    public async Task SafeModeRecordsNothingAndAsksForNothing()
    {
        var rig = new Rig(safeMode: true);

        TraceResult result = await rig.Recorder.RecordAsync(CancellationToken.None);

        Assert.AreEqual(TraceOutcome.Refused, result.Outcome);
        Assert.AreEqual(0, rig.Calls.Count);
        Assert.AreEqual(TraceState.Idle, rig.Recorder.State);
    }

    [TestMethod]
    public async Task ASecondRecordingWhileOneRunsIsRefused()
    {
        var rig = new Rig();
        Task<TraceResult> first = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);

        TraceResult second = await rig.Recorder.RecordAsync(CancellationToken.None);

        Assert.AreEqual(TraceOutcome.Refused, second.Outcome);
        Assert.AreEqual(2, rig.Calls.Count);
        rig.Launcher.Completion.SetResult(new ElevatedRun(null, StepOutcomes.FromWin32("runas", 1223)));
        await first;
    }

    [TestMethod]
    public void AMarkerWhoseHelperIsGoneMakesTheNextStartSayATraceMayStillBeRunning()
    {
        var rig = new Rig();
        rig.Files.Marker = TraceMarker.Serialize(new TraceMarker(5150, Nonce));

        Assert.AreEqual("A Bluetooth trace may still be running. Restart the PC to stop it.", rig.Recorder.StillRunningNotice());
    }

    [TestMethod]
    public void AMarkerWhoseHelperIsAliveIsNotAStaleOne()
    {
        var rig = new Rig();
        rig.Files.Marker = TraceMarker.Serialize(new TraceMarker(5150, Nonce));
        rig.Files.Alive.Add(5150);

        Assert.IsNull(rig.Recorder.StillRunningNotice());
    }

    [TestMethod]
    public void NoMarkerOrAnUnreadableOneSaysNothing()
    {
        var rig = new Rig();
        Assert.IsNull(rig.Recorder.StillRunningNotice());

        rig.Files.Marker = "not a marker";
        Assert.IsNull(rig.Recorder.StillRunningNotice());
    }

    [TestMethod]
    public void TheWordsAreBritishPlainAndHaveNoEmDashes()
    {
        foreach (string text in new[] { TraceCopy.Saved, TraceCopy.Running, TraceCopy.Declined, TraceCopy.StillRunning, TraceCopy.WprMissing, TraceCopy.SafeMode, TraceCopy.StopButton, TraceCopy.StartButton })
        {
            Assert.IsFalse(text.Contains((char)0x2014), text);
        }

        Same("Recording a Bluetooth trace. Try pairing now.", TraceCopy.Running);
        Same("Stop", TraceCopy.StopButton);
        Same("Record a Bluetooth trace", TraceCopy.StartButton);
        Same(
            "Trace saved in Documents\\Earshot\\Bluetooth traces. It holds Bluetooth addresses and may hold pairing keys, so share it only with someone you trust. Earshot never sends it anywhere.",
            TraceCopy.Saved);
    }

    // The words are pinned on purpose; this keeps the analyser from reading a pin as a mistake.
    private static void Same(string expected, string actual) => Assert.AreEqual(expected, actual);
}
