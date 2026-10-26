using Earshot.Contracts;
using Earshot.Tracing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Tracing;

internal sealed class FakeTraceMachine : ITraceMachine
{
    public List<string> Calls { get; } = [];
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool WprThere { get; set; } = true;
    public string? PrepareProblem { get; set; }
    public string? GrantProblem { get; set; }
    public byte[]? ProfileOnDiskOverride { get; set; }
    public string WprPath => @"C:\Windows\System32\wpr.exe";
    public string TraceFolder => @"C:\ProgramData\Earshot\trace";
    public bool FolderDeleted { get; private set; }
    public string? DeleteFolderProblem { get; set; }

    public bool FileExists(string path) => path == WprPath ? WprThere : Files.ContainsKey(path);

    public string? PrepareRunFolder(string nonce, out string runFolder)
    {
        runFolder = Path.Combine(TraceFolder, nonce);
        Calls.Add("folder " + nonce);
        return PrepareProblem;
    }

    public string? WriteFile(string path, byte[] bytes)
    {
        Calls.Add("write " + Path.GetFileName(path));
        Files[path] = bytes;
        return null;
    }

    public byte[]? ReadFile(string path)
    {
        if (ProfileOnDiskOverride is not null && path.EndsWith(".wprp", StringComparison.Ordinal))
        {
            return ProfileOnDiskOverride;
        }

        return Files.TryGetValue(path, out byte[]? b) ? b : null;
    }

    public string? DeleteFile(string path)
    {
        Calls.Add("delete " + Path.GetFileName(path));
        Files.Remove(path);
        return null;
    }

    public string? DeleteRunFolder(string runFolder)
    {
        Calls.Add("delete folder");
        FolderDeleted = DeleteFolderProblem is null;
        return DeleteFolderProblem;
    }

    public string? GrantRead(string path, int trayPid)
    {
        Calls.Add("grant " + Path.GetFileName(path) + " " + trayPid);
        return GrantProblem;
    }
}

internal sealed class FakeTraceSignals : ITraceSignals
{
    public List<string> Calls { get; } = [];
    public string? OpenProblem { get; set; }
    public bool TrayAlive { get; set; } = true;
    public TraceStopReason Reason { get; set; } = TraceStopReason.StopRequested;
    public bool StopAskedFirst { get; set; }

    public bool StopAsked()
    {
        Calls.Add("stop asked?");
        return StopAskedFirst;
    }
    public bool CopiedSet { get; set; } = true;
    public TimeSpan? WaitedForStop { get; private set; }
    public TimeSpan? WaitedForCopy { get; private set; }

    public string? Open()
    {
        Calls.Add("open");
        return OpenProblem;
    }

    public bool TrayRunning() => TrayAlive;

    public bool SameSession { get; set; } = true;

    public bool TrayInThisSession() => SameSession;

    public TraceStopReason WaitForStop(TimeSpan max)
    {
        Calls.Add("wait");
        WaitedForStop = max;
        return Reason;
    }

    public void SignalDone() => Calls.Add("done");

    // The tray exits while the helper waits for it to copy.
    public bool TrayExitsDuringCopyWait { get; set; }

    public bool WaitCopied(TimeSpan max)
    {
        Calls.Add("wait copied");
        WaitedForCopy = max;
        if (TrayExitsDuringCopyWait)
        {
            TrayAlive = false;
        }

        return CopiedSet;
    }
}

internal sealed class FakeRunner : IProcessRunner
{
    public List<string> Calls { get; } = [];
    public Func<IReadOnlyList<string>, ProcessRun>? Respond { get; set; }
    public List<string?> TempFolders { get; } = [];

    public ProcessRun Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, string? tempFolder = null)
    {
        Calls.Add(string.Join(' ', args));
        TempFolders.Add(tempFolder);
        return Respond?.Invoke(args) ?? new ProcessRun(true, 0, false, "", null);
    }
}

[TestClass]
public sealed class TraceHelperTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static readonly string[] ExpectedSignalOrder = ["open", "stop asked?", "wait", "done", "wait copied"];
    private static readonly DateTime Now = new(2026, 10, 25, 14, 30, 5, DateTimeKind.Utc);

    private static (TraceOutcome Outcome, FakeTraceMachine Machine, FakeTraceSignals Signals, FakeRunner Runner, CapturingLog Log) Run(
        Action<FakeTraceMachine, FakeTraceSignals, FakeRunner>? arrange = null)
    {
        var machine = new FakeTraceMachine();
        var signals = new FakeTraceSignals();
        var runner = new FakeRunner();
        arrange?.Invoke(machine, signals, runner);
        var log = new CapturingLog();
        TraceOutcome outcome = TraceHelper.Run(new TraceRequest(4321, Nonce), machine, signals, runner, log, () => Now);
        return (outcome, machine, signals, runner, log);
    }

    [TestMethod]
    public void ARecordingCancelsAnyOldSessionStartsRecordsAndStopsToADatedFile()
    {
        var (outcome, machine, signals, runner, _) = Run();

        Assert.AreEqual(TraceOutcome.Saved, outcome);
        Assert.AreEqual(3, runner.Calls.Count);
        Assert.AreEqual("-cancel -instancename " + WprCommand.InstanceName, runner.Calls[0]);
        StringAssert.StartsWith(runner.Calls[1], "-start ");
        StringAssert.EndsWith(runner.Calls[1], @"BluetoothStack.wprp!BluetoothStack.Verbose -filemode -recordtempto C:\ProgramData\Earshot\trace\" + Nonce + " -instancename " + WprCommand.InstanceName);
        Assert.AreEqual(@"-stop C:\ProgramData\Earshot\trace\" + Nonce + @"\bluetooth-20261025T143005Z.etl -instancename " + WprCommand.InstanceName, runner.Calls[2]);
        CollectionAssert.AreEqual(ExpectedSignalOrder, signals.Calls);
        Assert.IsTrue(machine.FolderDeleted);
    }

    [TestMethod]
    public void TheProfileIsWrittenIntoThePrivateFolderAndCheckedBeforeItIsUsed()
    {
        var (outcome, machine, _, runner, _) = Run((m, _, _) => m.ProfileOnDiskOverride = [1, 2, 3]);

        Assert.AreEqual(TraceOutcome.ProfileChanged, outcome);
        Assert.AreEqual(0, runner.Calls.Count, "No wpr command may run on a profile that does not match.");
        Assert.IsTrue(machine.FolderDeleted);
    }

    [TestMethod]
    public void TheFirstWprCommandIsACancelOfAnOldSessionAndItsFailureIsOnlyLogged()
    {
        var (outcome, _, _, runner, log) = Run((_, _, r) => r.Respond = a => a[0] == "-cancel"
            ? new ProcessRun(true, unchecked((int)0xc5600603), false, "no trace running", null)
            : new ProcessRun(true, 0, false, "", null));

        Assert.AreEqual(TraceOutcome.Saved, outcome);
        Assert.AreEqual(3, runner.Calls.Count);
        Assert.IsTrue(log.Has(LogLevel.Info, "0xc5600603"), "The raw cancel code is recorded.");
    }

    [TestMethod]
    public void AStartThatWprRefusesIsReportedWithItsCodeAndNothingIsLeftRunning()
    {
        var (outcome, machine, signals, runner, log) = Run((_, _, r) => r.Respond = a => a[0] == "-start"
            ? new ProcessRun(true, unchecked((int)0xc5600611), false, "bad profile", null)
            : new ProcessRun(true, 0, false, "", null));

        Assert.AreEqual(TraceOutcome.StartFailed, outcome);
        Assert.IsTrue(log.Has(LogLevel.Warn, "0xc5600611"));
        CollectionAssert.DoesNotContain(runner.Calls.Select(c => c.Split(' ')[0]).ToList(), "-stop");
        CollectionAssert.Contains(signals.Calls, "done");
        Assert.IsTrue(machine.FolderDeleted);
        Assert.IsFalse(machine.Files.Keys.Any(k => k.EndsWith(TraceHelper.MarkerFileName, StringComparison.Ordinal)), "The marker is removed.");
    }

    [TestMethod]
    public void WithNoRecorderOnThePcNothingIsStarted()
    {
        var (outcome, machine, signals, runner, _) = Run((m, _, _) => m.WprThere = false);

        Assert.AreEqual(TraceOutcome.WprMissing, outcome);
        Assert.AreEqual(0, runner.Calls.Count);
        Assert.IsTrue(machine.FolderDeleted);
        CollectionAssert.Contains(signals.Calls, "done");
    }

    [TestMethod]
    public void WhenTheTrayIsAlreadyGoneNothingIsStarted()
    {
        var (outcome, _, _, runner, _) = Run((_, s, _) => s.TrayAlive = false);

        Assert.AreEqual(TraceOutcome.Refused, outcome);
        Assert.AreEqual(0, runner.Calls.Count);
    }

    [TestMethod]
    public void WhenTheTraysSignalsCannotBeOpenedNothingIsStarted()
    {
        var (outcome, machine, _, runner, _) = Run((_, s, _) => s.OpenProblem = "no such event");

        Assert.AreEqual(TraceOutcome.Refused, outcome);
        Assert.AreEqual(0, runner.Calls.Count);
        Assert.AreEqual(0, machine.Calls.Count);
    }

    [TestMethod]
    public void AFolderThatCannotBeMadePrivateStopsTheRun()
    {
        var (outcome, _, _, runner, _) = Run((m, _, _) => m.PrepareProblem = "exists");

        Assert.AreEqual(TraceOutcome.SetupFailed, outcome);
        Assert.AreEqual(0, runner.Calls.Count);
    }

    [TestMethod]
    public void TheRecordingLastsAtMostTenMinutes()
    {
        var (_, _, signals, _, _) = Run();

        Assert.AreEqual(TimeSpan.FromMinutes(10), signals.WaitedForStop);
        Assert.AreEqual(TimeSpan.FromMinutes(10), TraceHelper.MaxRecording);
    }

    // Stop pressed while the administrator prompt was still up: nothing is recorded. Before, the stop event was already set,
    // so the helper started wpr, saw the stop at once and saved a few seconds of trace, which holds addresses.
    [TestMethod]
    public void AStopPressedBeforeTheRecordingStartsRecordsNothing()
    {
        var (outcome, _, signals, runner, _) = Run((_, s, _) => s.StopAskedFirst = true);

        Assert.AreEqual(TraceOutcome.StoppedBeforeStart, outcome);
        Assert.IsFalse(runner.Calls.Any(c => c.StartsWith("-start ", StringComparison.Ordinal)), string.Join(" | ", runner.Calls));
        Assert.IsFalse(runner.Calls.Any(c => c.StartsWith("-stop ", StringComparison.Ordinal)));
        CollectionAssert.Contains(signals.Calls, "wait copied", "The tray is still there to read the result.");
        Assert.AreEqual(ExitCodes.Ok, TraceHelper.ExitCodeFor(outcome));
    }

    [TestMethod]
    public void AMarkerNamesTheHelperWhileTheSessionRunsAndIsGoneAfterwards()
    {
        var (_, machine, _, _, _) = Run();

        // One marker per Windows session, so two people tracing at once do not write over each other's.
        Assert.AreEqual("running-S" + System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt", TraceHelper.MarkerFileName);
        int wrote = machine.Calls.IndexOf("write " + TraceHelper.MarkerFileName);
        int removed = machine.Calls.IndexOf("delete " + TraceHelper.MarkerFileName);
        Assert.IsTrue(wrote >= 0 && removed > wrote, string.Join(" | ", machine.Calls));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    public void AStopFromTheTrayOrTheTimeLimitStopsTheSessionAndSavesTheFile(int reasonNumber)
    {
        var (outcome, _, _, runner, _) = Run((_, s, _) => s.Reason = (TraceStopReason)reasonNumber);

        Assert.AreEqual(TraceOutcome.Saved, outcome);
        Assert.IsTrue(runner.Calls.Any(c => c.StartsWith("-stop ", StringComparison.Ordinal)));
    }

    // Exit, a sign-out or a crash of the tray during a recording: nobody would collect a file, which holds addresses and maybe
    // pairing keys. Before, the trace was saved and left in the administrators-only folder with nobody told.
    [TestMethod]
    public void WhenTheTrayExitsTheSessionIsCancelledNothingIsSavedAndTheFolderIsGone()
    {
        var (outcome, machine, signals, runner, log) = Run((_, s, _) => s.Reason = TraceStopReason.TrayExited);

        Assert.AreEqual(TraceOutcome.Discarded, outcome);
        Assert.IsFalse(runner.Calls.Any(c => c.StartsWith("-stop ", StringComparison.Ordinal)), "Nothing is saved.");
        Assert.AreEqual("-cancel -instancename " + WprCommand.InstanceName, runner.Calls[^1]);
        CollectionAssert.DoesNotContain(signals.Calls, "wait copied");
        Assert.IsTrue(machine.FolderDeleted);
        Assert.IsTrue(log.Has(LogLevel.Info, "the tray exited, so the recording was cancelled"));
        Assert.AreEqual(ExitCodes.Ok, TraceHelper.ExitCodeFor(outcome));
    }

    // A cancel that failed may have left the session recording, so the marker stays and the next start says a trace may still
    // be running. Before, the marker was deleted whatever the cancel returned.
    [TestMethod]
    public void AFailedCancelAfterTheTrayExitedKeepsTheMarker()
    {
        var (_, machine, _, _, log) = Run((_, s, r) =>
        {
            s.Reason = TraceStopReason.TrayExited;
            r.Respond = a => a[0] == "-cancel" ? new ProcessRun(true, 1, false, "", null) : new ProcessRun(true, 0, false, "", null);
        });

        CollectionAssert.DoesNotContain(machine.Calls, "delete " + TraceHelper.MarkerFileName);
        Assert.IsTrue(log.Has(LogLevel.Warn, "the marker is kept"), string.Join(" | ", log.Entries.Select(e => e.Message)));
    }

    [TestMethod]
    public void AFailedStopWhoseCancelAlsoFailedKeepsTheMarker()
    {
        var (outcome, machine, _, _, _) = Run((_, _, r) => r.Respond = a => a[0] is "-stop" or "-cancel" ? new ProcessRun(true, 1, false, "", null) : new ProcessRun(true, 0, false, "", null));

        Assert.AreEqual(TraceOutcome.StopFailed, outcome);
        CollectionAssert.DoesNotContain(machine.Calls, "delete " + TraceHelper.MarkerFileName);
    }

    [TestMethod]
    public void AFailedStopWhoseCancelWorkedRemovesTheMarker()
    {
        var (outcome, machine, _, _, _) = Run((_, _, r) => r.Respond = a => a[0] == "-stop" ? new ProcessRun(true, 1, false, "", null) : new ProcessRun(true, 0, false, "", null));

        Assert.AreEqual(TraceOutcome.StopFailed, outcome);
        CollectionAssert.Contains(machine.Calls, "delete " + TraceHelper.MarkerFileName);
    }

    // Every wpr run has the private run folder as its TEMP and TMP, so nothing it makes for itself lands in the person's own
    // temporary folder, where a program of theirs could read it.
    [TestMethod]
    public void EveryWprRunHasTheRunFolderAsItsTemporaryFolder()
    {
        var (_, machine, _, runner, _) = Run();

        Assert.IsNotEmpty(runner.TempFolders);
        Assert.IsTrue(runner.TempFolders.All(f => f == Path.Combine(machine.TraceFolder, Nonce)), string.Join(" | ", runner.TempFolders));
    }

    // The trace is made readable for the user of the process named on the command line, so a process in another session (another
    // person signed in on this PC) gets nothing started. Before, any process id was taken.
    [TestMethod]
    public void ATrayInAnotherSessionIsRefusedBeforeAnythingStarts()
    {
        var (outcome, machine, _, runner, _) = Run((_, s, _) => s.SameSession = false);

        Assert.AreEqual(TraceOutcome.Refused, outcome);
        Assert.IsEmpty(runner.Calls);
        Assert.IsFalse(machine.Calls.Any(c => c.StartsWith("folder ", StringComparison.Ordinal)));
    }

    // The log says which it was: the tray exited, or it did not answer in time. Before, a tray that exited after a few seconds
    // was logged as not answering within the whole wait.
    [TestMethod]
    public void ATrayThatExitsBeforeItCopiesIsLoggedAsExited()
    {
        var (_, _, _, _, exited) = Run((_, s, _) => { s.CopiedSet = false; s.TrayExitsDuringCopyWait = true; });
        var (_, _, _, _, silent) = Run((_, s, _) => s.CopiedSet = false);

        Assert.IsTrue(exited.Has(LogLevel.Warn, "the tray exited before it said it was finished"), string.Join(" | ", exited.Entries.Select(e => e.Message)));
        Assert.IsFalse(exited.Has(LogLevel.Warn, "did not say it was finished within"));
        Assert.IsTrue(silent.Has(LogLevel.Warn, "did not say it was finished within"));
    }

    [TestMethod]
    public void AStartThatTimesOutIsCancelledSoNothingIsLeftRecording()
    {
        var (outcome, _, _, runner, _) = Run((_, _, r) => r.Respond = a => a[0] == "-start"
            ? new ProcessRun(true, -1, true, "", null)
            : new ProcessRun(true, 0, false, "", null));

        Assert.AreEqual(TraceOutcome.StartFailed, outcome);
        Assert.AreEqual("-cancel -instancename " + WprCommand.InstanceName, runner.Calls[^1]);
    }

    [TestMethod]
    public void TheTraysUserCanReadTheFileAndTheResultButNothingElseIsOpened()
    {
        var (_, machine, _, _, _) = Run();

        CollectionAssert.Contains(machine.Calls, "grant bluetooth-20261025T143005Z.etl 4321");
        CollectionAssert.Contains(machine.Calls, "grant result.txt 4321");
    }

    [TestMethod]
    public void TheResultFileSaysWhatWasSaved()
    {
        var (_, machine, _, _, _) = Run();

        byte[] bytes = machine.Files[Path.Combine(@"C:\ProgramData\Earshot\trace", Nonce, "result.txt")];
        Assert.IsTrue(TraceResultFile.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out TraceResultFile? parsed));
        Assert.AreEqual(TraceOutcome.Saved, parsed!.Outcome);
        Assert.AreEqual("bluetooth-20261025T143005Z.etl", parsed.FileName);
    }

    [TestMethod]
    public void AStopThatFailsIsReportedAndTheSessionIsCancelledSoNothingKeepsRunning()
    {
        var (outcome, _, signals, runner, _) = Run((_, _, r) => r.Respond = a => a[0] == "-stop"
            ? new ProcessRun(true, unchecked((int)0xc5600607), false, "", null)
            : new ProcessRun(true, 0, false, "", null));

        Assert.AreEqual(TraceOutcome.StopFailed, outcome);
        Assert.AreEqual("-cancel -instancename " + WprCommand.InstanceName, runner.Calls[^1]);
        CollectionAssert.Contains(signals.Calls, "done");
    }

    [TestMethod]
    public void AStopThatTimesOutIsAFailureToo()
    {
        var (outcome, _, _, _, _) = Run((_, _, r) => r.Respond = a => a[0] == "-stop"
            ? new ProcessRun(true, -1, true, "", null)
            : new ProcessRun(true, 0, false, "", null));

        Assert.AreEqual(TraceOutcome.StopFailed, outcome);
    }

    [TestMethod]
    public void TheTrayHasAMinuteOrMoreToCopyTheFile()
    {
        var (_, _, signals, _, _) = Run();

        Assert.IsTrue(signals.WaitedForCopy >= TimeSpan.FromMinutes(1));
    }

    [TestMethod]
    public void ATrayThatNeverSaysItCopiedTheFileHasItsCopyDeletedAfterTheWait()
    {
        var (outcome, machine, signals, _, log) = Run((_, s, _) => s.CopiedSet = false);

        Assert.AreEqual(TraceOutcome.Saved, outcome);
        CollectionAssert.Contains(signals.Calls, "wait copied");
        Assert.IsTrue(machine.FolderDeleted, "Nobody will collect it, so it is not left holding addresses.");
        Assert.IsTrue(log.Has(LogLevel.Warn, "nobody took the file"));
    }

    [TestMethod]
    public void AFileThatCouldNotBeMadeReadableIsKeptAndSaidSo()
    {
        var (outcome, machine, _, _, log) = Run((m, _, _) => m.GrantProblem = "access denied");

        Assert.AreEqual(TraceOutcome.SavedInPlace, outcome);
        Assert.IsFalse(machine.FolderDeleted);
        Assert.IsTrue(log.Has(LogLevel.Warn, "access denied"));
    }

    [TestMethod]
    public void AHelperCopyThatCouldNotBeDeletedIsReportedInTheResultAndTheLog()
    {
        var (outcome, _, _, _, log) = Run((m, _, _) => m.DeleteFolderProblem = "in use");

        Assert.AreEqual(TraceOutcome.CopyKept, outcome);
        Assert.AreEqual(ExitCodes.IoError, TraceHelper.ExitCodeFor(outcome));
        Assert.IsTrue(log.Has(LogLevel.Warn, "in use"));
    }

    [TestMethod]
    public void ARunFolderWithNoTraceInItThatStaysIsLoggedButIsNotACopyKept()
    {
        var (outcome, _, _, _, log) = Run((m, _, _) =>
        {
            m.WprThere = false;
            m.DeleteFolderProblem = "in use";
        });

        Assert.AreEqual(TraceOutcome.WprMissing, outcome);
        Assert.IsTrue(log.Has(LogLevel.Warn, "in use"));
    }

    [TestMethod]
    public void ExitCodesFollowTheOutcome()
    {
        Assert.AreEqual(ExitCodes.Ok, TraceHelper.ExitCodeFor(TraceOutcome.Saved));
        Assert.AreEqual(ExitCodes.Ok, TraceHelper.ExitCodeFor(TraceOutcome.SavedInPlace));
        Assert.AreEqual(ExitCodes.Refused, TraceHelper.ExitCodeFor(TraceOutcome.Refused));
        Assert.AreEqual(ExitCodes.Unavailable, TraceHelper.ExitCodeFor(TraceOutcome.WprMissing));
        Assert.AreEqual(ExitCodes.Software, TraceHelper.ExitCodeFor(TraceOutcome.StartFailed));
        Assert.AreEqual(ExitCodes.Software, TraceHelper.ExitCodeFor(TraceOutcome.StopFailed));
    }
}
