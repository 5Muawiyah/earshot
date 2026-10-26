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
    public Action? OnRequestStop { get; set; }

    public void RequestStop()
    {
        StopRequested = true;
        OnRequestStop?.Invoke();
    }
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

    public Exception? Throws { get; set; }

    public ITrayTraceSignals Create(string nonce)
    {
        Calls.Add("create " + nonce);
        return Throws is null ? Signals : throw Throws;
    }
}

internal sealed class FakeTraceFiles : ITraceUserFiles
{
    public string? ResultText { get; set; }
    public string? CopyProblem { get; set; }
    public string? Marker { get; set; }
    public string? Untrusted { get; set; }
    public const long DefaultBoot = 1_790_000_000;
    public long Boot { get; set; } = DefaultBoot;
    public int Leftovers { get; set; }
    public HashSet<int> Alive { get; } = [];
    public List<string> Calls { get; } = [];
    public string? TrustProblem() => Untrusted;
    public int LeftoverRunFolders() => Leftovers;
    public int PartialCopies { get; set; }

    public int RemovePartialCopies(string destinationFolder)
    {
        Calls.Add("remove partial copies in " + destinationFolder);
        return PartialCopies;
    }

    public string? ReadResult(string nonce)
    {
        Calls.Add("read result");
        return ResultText;
    }

    public string? Copy(string nonce, string fileName, string destinationFolder, out string copiedTo)
    {
        Calls.Add("copy " + fileName + " to " + destinationFolder);
        copiedTo = Path.Combine(destinationFolder, fileName);
        return CopyProblem;
    }

    public void OpenFolder(string folder) => Calls.Add("open " + folder);
    public string? ReadMarker() => Marker;
    public bool ProcessAlive(int pid) => Alive.Contains(pid);
    public long BootSeconds() => Boot;
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
    private const string Destination = @"C:\Users\p\AppData\Local\Earshot\traces";
    private const string Installed = @"C:\Program Files\Earshot\Earshot.exe";
    private static readonly string Saved = TraceResultFile.Serialize(new TraceResultFile(TraceOutcome.Saved, "bluetooth-20261025T143005Z.etl", 0));

    private sealed class Rig
    {
        public List<string> Calls { get; } = [];
        public FakeTraySignalFactory Factory { get; }
        public FakeLauncher Launcher { get; }
        public FakeTraceFiles Files { get; } = new();
        public TraceRecorder Recorder { get; }

        public string? InstalledCopy { get; set; } = Installed;

        public Rig(bool safeMode = false)
        {
            Factory = new FakeTraySignalFactory(Calls);
            Launcher = new FakeLauncher(Calls);
            Recorder = new TraceRecorder(
                new TraceRecorderOptions(safeMode, () => InstalledCopy, Destination, 777, () => Nonce),
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

    // The events could not be made (one of the names is already there): a result that says so, not an exception out of the
    // recorder, and nothing is asked.
    [TestMethod]
    public async Task EventsThatCannotBeMadeEndTheRunWithAResult()
    {
        var rig = new Rig();
        rig.Factory.Throws = new InvalidOperationException("The event already exists.");

        TraceResult result = await rig.Recorder.RecordAsync(CancellationToken.None);

        Assert.AreEqual(TraceOutcome.Refused, result.Outcome);
        Assert.AreEqual("The Bluetooth trace could not be started. Try again in a moment.", result.Message);
        Assert.AreEqual(TraceState.Idle, rig.Recorder.State);
        Assert.IsFalse(rig.Calls.Any(c => c.StartsWith("launch ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AStopBeforeTheRecordingStartedSaysNothingWasRecorded()
    {
        var rig = new Rig();
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);
        rig.Files.ResultText = TraceResultFile.Serialize(new TraceResultFile(TraceOutcome.StoppedBeforeStart, "", 0));
        rig.Factory.Signals.Finish();
        rig.Launcher.Completion.SetResult(new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0)));

        TraceResult result = await run;

        Assert.AreEqual(TraceOutcome.StoppedBeforeStart, result.Outcome);
        Assert.AreEqual("Stop was pressed before the recording started, so nothing was recorded.", result.Message);
        Assert.IsTrue(rig.Factory.Signals.CopiedSet);
    }

    [TestMethod]
    public async Task ASavedTraceIsCopiedToTheLocalTracesFolderTheFolderIsOpenedAndTheHelperIsToldItMayGo()
    {
        var rig = new Rig();
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);
        rig.Files.ResultText = Saved;
        rig.Factory.Signals.Finish();
        rig.Launcher.Completion.SetResult(new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0)));

        TraceResult result = await run;

        Assert.AreEqual(TraceOutcome.Saved, result.Outcome);
        string folder = Destination;
        CollectionAssert.Contains(rig.Files.Calls, "copy bluetooth-20261025T143005Z.etl to " + folder);
        CollectionAssert.Contains(rig.Files.Calls, "open " + folder);
        Assert.IsTrue(rig.Factory.Signals.CopiedSet);
        Assert.AreEqual(folder, result.FolderPath);
        Assert.AreEqual("Trace saved in " + Destination + ". Delete it when you're done.", result.Message);
        Assert.AreEqual(TraceState.Idle, rig.Recorder.State);
        Assert.IsTrue(rig.Factory.Signals.Disposed);
    }

    [TestMethod]
    public async Task WithNoInstalledCopyNothingIsMadeOrAskedAndTheAnswerIsToSetUpFirst()
    {
        var rig = new Rig { InstalledCopy = null };

        Assert.IsTrue(rig.Recorder.NeedsSetUp);
        TraceResult result = await rig.Recorder.RecordAsync(CancellationToken.None);

        Assert.AreEqual(TraceOutcome.Refused, result.Outcome);
        Assert.AreEqual(TraceCopy.SetUpFirst, result.Message);
        Assert.AreEqual(0, rig.Calls.Count, "No signals made and no prompt asked for.");
        Assert.AreEqual(TraceState.Idle, rig.Recorder.State);
    }

    [TestMethod]
    public async Task OnlyTheInstalledCopyIsRaisedAndItIsReadWhenTheRecordingStarts()
    {
        var rig = new Rig { InstalledCopy = null };
        Assert.IsTrue(rig.Recorder.NeedsSetUp);

        rig.InstalledCopy = @"C:\Program Files\Earshot\Earshot.exe";
        Assert.IsFalse(rig.Recorder.NeedsSetUp, "Set up while the tray ran: the copy is found without a restart.");
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);

        StringAssert.StartsWith(rig.Calls[1], @"launch C:\Program Files\Earshot\Earshot.exe trace ");
        rig.Launcher.Completion.SetResult(new ElevatedRun(null, StepOutcomes.FromWin32("runas", 1223)));
        await run;
    }

    [TestMethod]
    public async Task AHelperThatCouldNotDeleteItsOwnCopySaysWhereThatCopyIs()
    {
        var rig = new Rig();
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);
        rig.Files.ResultText = Saved;
        rig.Factory.Signals.Finish();
        rig.Launcher.Completion.SetResult(new ElevatedRun(ExitCodes.IoError, StepOutcomes.FromWin32("runas", 0)));

        TraceResult result = await run;

        Assert.AreEqual(TraceOutcome.Saved, result.Outcome);
        StringAssert.EndsWith(result.Message, TraceCopy.AdminCopyKept);
    }

    [TestMethod]
    public async Task ACopyThatFailsSaysSoAndLetsTheHelperDeleteItsCopyAtOnce()
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
        Assert.IsTrue(rig.Factory.Signals.CopiedSet, "Nobody can read the helper's copy where it is, so the helper is told to delete it now.");
        Assert.AreEqual(TraceCopy.NotKept(Destination, "disk full"), result.Message);
        Assert.IsFalse(result.Message.Contains("not kept", StringComparison.Ordinal), "Whether the helper's copy went is not known until it ends.");
    }

    // The copy failed and then the helper could not delete its own copy: the person is told where that copy is. Before, the
    // card said the trace was not kept, while a copy that may hold pairing keys stayed under ProgramData.
    [TestMethod]
    public async Task ACopyThatFailsWhoseHelperCouldNotDeleteItsCopySaysWhereThatCopyIs()
    {
        var rig = new Rig();
        rig.Files.CopyProblem = "disk full";
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);
        rig.Files.ResultText = Saved;
        rig.Factory.Signals.Finish();
        rig.Launcher.Completion.SetResult(new ElevatedRun(ExitCodes.IoError, StepOutcomes.FromWin32("runas", 0)));

        TraceResult result = await run;

        StringAssert.Contains(result.Message, TraceCopy.NotKept(Destination, "disk full"));
        StringAssert.Contains(result.Message, TraceCopy.AdminCopyKept);
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
        Assert.IsFalse(rig.Files.Calls.Any(c => c.StartsWith("copy ", StringComparison.Ordinal)));
        Assert.AreEqual(TraceState.Idle, rig.Recorder.State);
    }

    // A result in a folder a standard user could have written is not believed: it could name a file of their choosing.
    [TestMethod]
    public async Task AResultInAnUntrustedFolderIsNotReadAndNothingIsCopied()
    {
        var rig = new Rig();
        rig.Files.Untrusted = "the trace folder is owned by a user";
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);
        rig.Files.ResultText = Saved;
        rig.Factory.Signals.Finish();
        rig.Launcher.Completion.SetResult(new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0)));

        TraceResult result = await run;

        Assert.AreEqual(TraceOutcome.HelperFailed, result.Outcome);
        Assert.AreEqual(TraceCopy.Untrusted, result.Message);
        CollectionAssert.DoesNotContain(rig.Files.Calls, "read result");
        Assert.IsFalse(rig.Files.Calls.Any(c => c.StartsWith("copy ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void RunFoldersLeftByEarlierRecordingsAreSaidAtStart()
    {
        var rig = new Rig();
        Assert.IsNull(rig.Recorder.LeftoverNotice());

        rig.Files.Leftovers = 2;

        Assert.AreEqual(TraceCopy.Leftover(2), rig.Recorder.LeftoverNotice());
        StringAssert.Contains(TraceCopy.Leftover(2), "2 Bluetooth trace folder");
        // A folder another session is recording into now is counted too, so the notice does not call them all earlier.
        Assert.IsFalse(TraceCopy.Leftover(2).Contains("2 earlier", StringComparison.Ordinal));
    }

    // A copy cut off by Exit is left under a temporary name, never as a trace that looks whole; the start removes it.
    [TestMethod]
    public void TheStartRemovesCopiesThatWereCutOff()
    {
        var rig = new Rig();
        rig.Files.PartialCopies = 1;

        rig.Recorder.LeftoverNotice();

        CollectionAssert.Contains(rig.Files.Calls, "remove partial copies in " + rig.Recorder.DestinationFolder);
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

    // A Stop pressed just as the run ends. Before, Stop took the signals without the lock while the run's end disposed them
    // outside it, so the press could reach events that were disposed under it. Now the end waits for a Stop in progress.
    [TestMethod]
    public async Task AStopAsTheRunEndsNeverReachesSignalsDisposedUnderIt()
    {
        var rig = new Rig();
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);
        rig.Files.ResultText = Saved;
        bool disposedDuringStop = false;
        rig.Factory.Signals.OnRequestStop = () =>
        {
            // The run ends while the press is still in progress, and is given ample time (on old code, which disposed outside the
            // lock, it would have by then; on this code it waits for the press).
            rig.Factory.Signals.Finish();
            rig.Launcher.Completion.TrySetResult(new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0)));
            Thread.Sleep(2000);
            disposedDuringStop = rig.Factory.Signals.Disposed;
        };

        await Task.Run(rig.Recorder.Stop);
        await run;

        Assert.IsFalse(disposedDuringStop, "The signals were disposed while Stop was using them.");
        Assert.IsTrue(rig.Factory.Signals.Disposed, "The run's end still disposes them once Stop is done.");
    }

    // Exit: the helper is not asked to stop and save a file nobody will collect. It sees the tray process end and cancels.
    [TestMethod]
    public async Task DisposingTheRecorderWhileItRecordsDoesNotAskTheHelperToSave()
    {
        var rig = new Rig();
        Task<TraceResult> run = rig.Recorder.RecordAsync(CancellationToken.None);
        await Rig.Until(() => rig.Calls.Count >= 2);

        rig.Recorder.Dispose();

        Assert.IsFalse(rig.Factory.Signals.StopRequested);
        rig.Factory.Signals.Finish();
        rig.Launcher.Completion.SetResult(new ElevatedRun(0, StepOutcomes.FromWin32("runas", 0)));
        rig.Files.ResultText = Saved;
        await run;
    }

    // ShellExecute's runas returns once the prompt is answered. Raised on the caller's thread, it would hold the tray's UI
    // thread for as long as the prompt is up.
    [TestMethod]
    public async Task ThePromptIsRaisedAwayFromTheCallersThread()
    {
        var calls = new List<string>();
        using var answered = new ManualResetEventSlim();
        var launcher = new BlockingLauncher(answered);
        var factory = new FakeTraySignalFactory(calls);
        var files = new FakeTraceFiles();
        using var recorder = new TraceRecorder(new TraceRecorderOptions(false, () => Installed, Destination, 777, () => Nonce), launcher, factory, files, new CapturingLog());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Task<TraceResult> run = recorder.RecordAsync(CancellationToken.None);
        long returnedAfter = watch.ElapsedMilliseconds;
        answered.Set();
        await run;

        Assert.IsLessThan(2000L, returnedAfter, "RecordAsync waited for the prompt on the caller's thread.");
    }

    private sealed class BlockingLauncher(ManualResetEventSlim answered) : IElevatedLauncher
    {
        // Like ShellExecute: returns only once the prompt is answered (or after five seconds here), then reports a declined prompt.
        public Task<ElevatedRun> RunAsync(string executable, string arguments, CancellationToken ct)
        {
            answered.Wait(TimeSpan.FromSeconds(5), ct);
            return Task.FromResult(new ElevatedRun(null, StepOutcomes.FromWin32("runas", 1223)));
        }
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
        rig.Files.Marker = TraceMarker.Serialize(new TraceMarker(5150, Nonce, FakeTraceFiles.DefaultBoot));

        Assert.AreEqual("A Bluetooth trace may still be running. Restart the PC to stop it.", rig.Recorder.StillRunningNotice());
    }

    [TestMethod]
    public void AMarkerWhoseHelperIsAliveIsNotAStaleOne()
    {
        var rig = new Rig();
        rig.Files.Marker = TraceMarker.Serialize(new TraceMarker(5150, Nonce, FakeTraceFiles.DefaultBoot));
        rig.Files.Alive.Add(5150);

        Assert.IsNull(rig.Recorder.StillRunningNotice());
    }

    // A restart ends every recording, so a marker written before this start of the PC says nothing. Before, it said a trace
    // may still be running at every start until another trace ran, because a standard user cannot remove it.
    [TestMethod]
    public void AMarkerFromBeforeTheLastRestartSaysNothing()
    {
        var rig = new Rig();
        rig.Files.Marker = TraceMarker.Serialize(new TraceMarker(5150, Nonce, FakeTraceFiles.DefaultBoot - 600));

        Assert.IsNull(rig.Recorder.StillRunningNotice());
    }

    // A marker in a trace folder that fails the machine folder's check could be anyone's, so it says nothing.
    [TestMethod]
    public void AMarkerInATraceFolderThatFailsItsCheckSaysNothing()
    {
        var rig = new Rig();
        rig.Files.Marker = TraceMarker.Serialize(new TraceMarker(5150, Nonce, FakeTraceFiles.DefaultBoot));
        rig.Files.Untrusted = "the trace folder can be changed by Users";

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
        foreach (string text in new[] { TraceCopy.SavedIn(Destination), TraceCopy.NoteFor(Destination), TraceCopy.NoteButton, TraceCopy.SetUpFirst, TraceCopy.AdminCopyKept, TraceCopy.Untrusted, TraceCopy.NotKept(Destination, "x"), TraceCopy.Leftover(1), TraceCopy.Running, TraceCopy.Declined, TraceCopy.StillRunning, TraceCopy.WprMissing, TraceCopy.SafeMode, TraceCopy.StopButton, TraceCopy.StartButton })
        {
            Assert.IsFalse(text.Contains((char)0x2014), text);
        }

        Same("Allow the Windows prompt to start the trace. Then try pairing, and press Stop when you are done.", TraceCopy.Running);
        Same("Stop", TraceCopy.StopButton);
        Same("Record a Bluetooth trace", TraceCopy.StartButton);
        Same(
            "A Bluetooth trace holds the Bluetooth addresses of your devices and may hold pairing keys. It is saved in " + Destination +
            ". Earshot never sends it anywhere. Share it only with someone you trust, and delete it when you're done.",
            TraceCopy.NoteFor(Destination));
        Same("Record", TraceCopy.NoteButton);
        Same("Set up or update Earshot first, then record a Bluetooth trace.", TraceCopy.SetUpFirst);
    }

    // The words are pinned on purpose; this keeps the analyser from reading a pin as a mistake.
    private static void Same(string expected, string actual) => Assert.AreEqual(expected, actual);
}
