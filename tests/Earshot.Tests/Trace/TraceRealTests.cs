using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Earshot.Infra;
using Earshot.Tests.Phase4;
using Earshot.Tracing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Tracing;

// Real executions of the pieces the fakes stand in for: the tray user's SID, the read grant, the named events, the process
// wait and the tray's file handling. None starts a recording or asks for elevation.
[TestClass]
public sealed class TraceRealTests
{
    private static string NewNonce() => Guid.NewGuid().ToString("N");

    [TestMethod]
    public void TheTrayUsersSidIsReadFromItsProcessToken()
    {
        SecurityIdentifier? sid = TrayUser.SidOf(Environment.ProcessId, out string? problem);

        Assert.IsNotNull(sid, problem);
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        Assert.AreEqual(me.User, sid);
    }

    // The helper asks for no more than it needs: a process handle to wait on and query (SYNCHRONIZE and
    // PROCESS_QUERY_LIMITED_INFORMATION), and a token to query. Before, it waited on Process.SafeHandle, which asks for full
    // access, and opened the token for duplication as well. The held handle still reads the tray's user.
    [TestMethod]
    public void TheHelperAsksOnlyToWaitOnAndQueryTheTray()
    {
        Assert.AreEqual(0x00101000u, TrayUser.WatchRights);
        Assert.AreEqual(0x0008u, TrayUser.TokenRights);

        using Microsoft.Win32.SafeHandles.SafeProcessHandle? handle = TrayUser.OpenForWatch(Environment.ProcessId, out string? problem);
        Assert.IsNotNull(handle, problem);
        SecurityIdentifier? sid = TrayUser.SidOf(handle, Environment.ProcessId, out problem);
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        Assert.AreEqual(me.User, sid, problem);
    }

    // Coverage: before Open has given a handle, the tray's user is a reason, never a throw out of the elevated helper.
    [TestMethod]
    public void TheTraysUserBeforeOpenIsAReasonNotAThrow()
    {
        using var helper = new TraceSignals(new TraceRequest(Environment.ProcessId, NewNonce()), new CapturingLog());

        Assert.IsNull(helper.TrayUser(out string? problem));
        Assert.IsFalse(string.IsNullOrEmpty(problem));
    }

    [TestMethod]
    public void AProcessThatIsNotThereGivesAReasonNotASid()
    {
        SecurityIdentifier? sid = TrayUser.SidOf(int.MaxValue - 7, out string? problem);

        Assert.IsNull(sid);
        Assert.IsFalse(string.IsNullOrEmpty(problem));
        StringAssert.Contains(problem, "(Win32 error ", "The raw code is kept, not only its message.");
    }

    [TestMethod]
    public void TheReadGrantAddsAReadRuleForTheTraysUser()
    {
        using var folder = new TempFolder();
        string file = folder.File("x.etl");
        File.WriteAllText(file, "x");
        var machine = new TraceMachine(Paths.FromEnvironment(n => n == Paths.DataRootVariable ? folder.Path : null), new Boot.Gate.NtfsFolderSecurity(), new CapturingLog());

        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        string? problem = machine.GrantRead(file, current.User!);

        Assert.IsNull(problem);
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        AuthorizationRuleCollection rules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        Assert.IsTrue(rules.OfType<FileSystemAccessRule>().Any(r =>
            r.IdentityReference.Equals(me.User) && r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & FileSystemRights.ReadData) != 0));
    }

    [TestMethod]
    public void TheRunFolderIsOnlyDeletedInsideTheTraceFolder()
    {
        using var folder = new TempFolder();
        string outside = Path.Combine(folder.Path, "outside");
        Directory.CreateDirectory(outside);
        var machine = new TraceMachine(Paths.FromEnvironment(n => n == Paths.DataRootVariable ? folder.Path : null), new Boot.Gate.NtfsFolderSecurity(), new CapturingLog());

        machine.DeleteRunFolder(outside);
        machine.DeleteRunFolder(machine.TraceFolder);

        Assert.IsTrue(Directory.Exists(outside));
    }

    // A run folder that is not there is nothing to delete, not a reparse point: a missing path reads as every attribute set.
    [TestMethod]
    public void AMissingRunFolderIsNotReportedAsAReparsePoint()
    {
        using var folder = new TempFolder();
        var log = new CapturingLog();
        var machine = new TraceMachine(Paths.FromEnvironment(n => n == Paths.DataRootVariable ? folder.Path : null), new Boot.Gate.NtfsFolderSecurity(), log);

        string? problem = machine.DeleteRunFolder(Path.Combine(machine.TraceFolder, NewNonce()));

        Assert.IsNull(problem);
        Assert.IsFalse(log.Entries.Any(e => e.Message.Contains("reparse point", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void TheEventNamesAreInTheSessionNamespaceAndNeedAGoodNonce()
    {
        string nonce = NewNonce();
        Assert.AreEqual(@"Local\Earshot.Trace.Stop." + nonce, TraceEventNames.Stop(nonce));
        Assert.AreEqual(@"Local\Earshot.Trace.Done." + nonce, TraceEventNames.Done(nonce));
        Assert.AreEqual(@"Local\Earshot.Trace.Copied." + nonce, TraceEventNames.Copied(nonce));
        Assert.ThrowsExactly<ArgumentException>(() => TraceEventNames.Stop(@"..\x"));
    }

    private static Process StartSleeper() => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 60 127.0.0.1")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    })!;

    [TestMethod]
    public void TheHelperSeesTheTraysStopTheTrayExitingAndTheTimeLimit()
    {
        string nonce = NewNonce();
        using var tray = TrayTraceSignals.Create(nonce);
        using Process sleeper = StartSleeper();
        try
        {
            using var helper = new TraceSignals(new TraceRequest(sleeper.Id, nonce), new CapturingLog());
            Assert.IsNull(helper.Open());
            Assert.IsTrue(helper.TrayRunning());

            Assert.AreEqual(TraceStopReason.TimedOut, helper.WaitForStop(TimeSpan.FromMilliseconds(200)));

            tray.RequestStop();
            Assert.AreEqual(TraceStopReason.StopRequested, helper.WaitForStop(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            sleeper.Kill(entireProcessTree: true);
            sleeper.WaitForExit();
        }
    }

    [TestMethod]
    public void TheHelperSeesTheTrayProcessExit()
    {
        string nonce = NewNonce();
        using var tray = TrayTraceSignals.Create(nonce);
        using Process sleeper = StartSleeper();
        using var helper = new TraceSignals(new TraceRequest(sleeper.Id, nonce), new CapturingLog());
        Assert.IsNull(helper.Open());

        sleeper.Kill(entireProcessTree: true);
        sleeper.WaitForExit();

        Assert.AreEqual(TraceStopReason.TrayExited, helper.WaitForStop(TimeSpan.FromSeconds(10)));
        Assert.IsFalse(helper.TrayRunning());
    }

    // Events the tray never made: the helper's open fails, and what it says carries the raw code, not only the message.
    [TestMethod]
    public void AnOpenWithNoTrayEventsSaysWhyWithItsCode()
    {
        using var helper = new TraceSignals(new TraceRequest(Environment.ProcessId, NewNonce()), new CapturingLog());

        string? problem = helper.Open();

        Assert.IsNotNull(problem);
        StringAssert.Contains(problem, "HRESULT 0x");
    }

    [TestMethod]
    public void DoneReachesTheTrayAndCopiedReachesTheHelper()
    {
        string nonce = NewNonce();
        using var tray = TrayTraceSignals.Create(nonce);
        using var helper = new TraceSignals(new TraceRequest(Environment.ProcessId, nonce), new CapturingLog());
        Assert.IsNull(helper.Open());

        helper.SignalDone();
        Assert.IsTrue(tray.WaitDone(CancellationToken.None));
        Assert.IsFalse(helper.WaitCopied(TimeSpan.FromMilliseconds(100)));
        tray.SetCopied();
        Assert.IsTrue(helper.WaitCopied(TimeSpan.FromSeconds(10)));
    }

    // The tray's account is read from the handle the helper holds: the test process stands in for the tray.
    [TestMethod]
    public void TheTraysAccountIsReadFromTheHandleTheHelperHolds()
    {
        string nonce = NewNonce();
        using var tray = TrayTraceSignals.Create(nonce);
        using var helper = new TraceSignals(new TraceRequest(Environment.ProcessId, nonce), new CapturingLog());
        Assert.IsNull(helper.Open());

        SecurityIdentifier? user = helper.TrayUser(out string? problem);

        Assert.IsNotNull(user, problem);
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        Assert.AreEqual(me.User, user);
    }

    // The test process stands in for the tray: it is in the helper's own session.
    [TestMethod]
    public void ATrayInThisSessionIsSeenAsSo()
    {
        string nonce = NewNonce();
        using var tray = TrayTraceSignals.Create(nonce);
        using var helper = new TraceSignals(new TraceRequest(Environment.ProcessId, nonce), new CapturingLog());
        Assert.IsNull(helper.Open());

        Assert.IsTrue(helper.TrayInThisSession());
    }

    [TestMethod]
    public void ACancelledWaitForDoneReturnsFalse()
    {
        using var tray = TrayTraceSignals.Create(NewNonce());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.IsFalse(tray.WaitDone(cancelled.Token));
    }

    [TestMethod]
    public void AHelperWithNoEventsFromTheTrayOpensNothing()
    {
        using var helper = new TraceSignals(new TraceRequest(Environment.ProcessId, NewNonce()), new CapturingLog());

        Assert.IsNotNull(helper.Open());
    }

    [TestMethod]
    public void TheSameNonceCannotBeUsedTwoAtOnce()
    {
        string nonce = NewNonce();
        using var first = TrayTraceSignals.Create(nonce);

        Assert.ThrowsExactly<InvalidOperationException>(() => TrayTraceSignals.Create(nonce));
    }

    [TestMethod]
    public void TheTraysFilesReadTheResultCopyTheTraceAndRefuseAFileNameThatLeavesTheFolder()
    {
        using var root = new TempFolder();
        string nonce = NewNonce();
        string run = Path.Combine(root.Path, nonce);
        Directory.CreateDirectory(run);
        File.WriteAllText(Path.Combine(run, "result.txt"), "outcome=Saved\nfile=bluetooth-20261025T143005Z.etl\nwpr=0\n");
        File.WriteAllText(Path.Combine(run, "bluetooth-20261025T143005Z.etl"), "trace");
        var files = new TraceUserFiles(root.Path, new Boot.Gate.NtfsFolderSecurity(), new CapturingLog());

        Assert.IsTrue(TraceResultFile.TryParse(files.ReadResult(nonce), out _));
        string destination = Path.Combine(root.Path, "Local", "Earshot", "traces");
        Assert.IsNull(files.Copy(nonce, "bluetooth-20261025T143005Z.etl", destination, out string copied));
        Assert.AreEqual("trace", File.ReadAllText(copied));
        Assert.IsEmpty(Directory.GetFiles(destination, "*.partial"), "The copy is made under a temporary name and renamed once whole.");
        Assert.IsNotNull(files.Copy(nonce, "bluetooth-20261025T143005Z.etl", destination, out _), "An existing file is not overwritten.");
        Assert.IsNotNull(files.Copy(nonce, @"..\result.txt", destination, out _));
        Assert.IsNotNull(files.Copy(nonce, "result.txt", destination, out _), "Only .etl files are copied.");
    }

    // A copy that Exit cut off is under its temporary name; the start removes it and nothing else.
    [TestMethod]
    public void PartialCopiesAreRemovedAndWholeTracesKept()
    {
        using var root = new TempFolder();
        string destination = Path.Combine(root.Path, "traces");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "bluetooth-20261025T143005Z.etl.partial"), "cut");
        File.WriteAllText(Path.Combine(destination, "bluetooth-20261024T101010Z.etl"), "whole");
        var files = new TraceUserFiles(root.Path, new Boot.Gate.NtfsFolderSecurity(), new CapturingLog());

        Assert.AreEqual(1, files.RemovePartialCopies(destination));
        Assert.IsEmpty(Directory.GetFiles(destination, "*.partial"));
        Assert.IsTrue(File.Exists(Path.Combine(destination, "bluetooth-20261024T101010Z.etl")));
        Assert.AreEqual(0, files.RemovePartialCopies(Path.Combine(root.Path, "no-such-folder")));
    }

    // A marker is a few short lines: a bigger file in the trace folder is not read, so it cannot hold up the tray's start.
    [TestMethod]
    public void AMarkerFileTooBigToBeOneIsNotRead()
    {
        using var root = new TempFolder();
        File.WriteAllText(Path.Combine(root.Path, TraceHelper.MarkerFileName), new string('x', TraceUserFiles.MaxMarkerBytes + 1));
        var files = new TraceUserFiles(root.Path, new Boot.Gate.NtfsFolderSecurity(), new CapturingLog());

        Assert.IsEmpty(files.ReadMarkers());
    }

    // A sign-in after a sign-out is usually a new session, so the marker a recording left is under another session's name: the
    // look at start reads every session's. Before, it read only its own session's, and said nothing.
    [TestMethod]
    public void TheMarkersOfEverySessionAreRead()
    {
        using var root = new TempFolder();
        File.WriteAllText(Path.Combine(root.Path, TraceHelper.MarkerFileNameFor(9001)), "earlier session");
        File.WriteAllText(Path.Combine(root.Path, "unrelated.txt"), "not a marker");
        var files = new TraceUserFiles(root.Path, new Boot.Gate.NtfsFolderSecurity(), new CapturingLog());

        IReadOnlyList<string> markers = files.ReadMarkers();
        Assert.HasCount(1, markers);
        Assert.AreEqual("earlier session", markers[0]);
    }

    // The folder window is opened by explorer.exe's full path in the Windows folder.
    [TestMethod]
    public void TheFolderWindowIsOpenedByExplorersFullPath()
    {
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), TraceUserFiles.ExplorerPath);
        Assert.IsTrue(Path.IsPathFullyQualified(TraceUserFiles.ExplorerPath));
    }

    [TestMethod]
    public void AMissingResultOrMarkerReadsAsNothing()
    {
        using var root = new TempFolder();
        var files = new TraceUserFiles(Path.Combine(root.Path, "no-such-folder"), new Boot.Gate.NtfsFolderSecurity(), new CapturingLog());

        Assert.IsNull(files.ReadResult(NewNonce()));
        Assert.IsEmpty(files.ReadMarkers());
    }

    [TestMethod]
    public void AProcessThatIsRunningIsAliveAndOneThatIsNotIsNot()
    {
        var files = new TraceUserFiles(Path.GetTempPath(), new Boot.Gate.NtfsFolderSecurity(), new CapturingLog());

        Assert.IsTrue(files.ProcessAlive(Environment.ProcessId));
        Assert.IsFalse(files.ProcessAlive(int.MaxValue - 7));
    }

    // The helper may be another account than the tray, so the events grant Administrators wait and set, not only the token's
    // default owner.
    [TestMethod]
    public void TheTraysEventsLetAdministratorsWaitAndSet()
    {
        string nonce = NewNonce();
        using var tray = TrayTraceSignals.Create(nonce);
        using EventWaitHandle stop = EventWaitHandleAcl.OpenExisting(TraceEventNames.Stop(nonce), EventWaitHandleRights.ReadPermissions);

        AuthorizationRuleCollection rules = stop.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        EventWaitHandleRights granted = rules.OfType<EventWaitHandleAccessRule>()
            .Where(r => r.IdentityReference.Equals(administrators) && r.AccessControlType == AccessControlType.Allow)
            .Aggregate((EventWaitHandleRights)0, (all, r) => all | r.EventWaitHandleRights);
        Assert.IsTrue(granted.HasFlag(EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify), granted.ToString());
    }

    // A trace folder made the ordinary way inherits from its parent, so the tray does not believe a result read from it.
    [TestMethod]
    public void AnOrdinaryTraceFolderIsNotTrustedAndNonceFoldersInItAreCountedAsLeftovers()
    {
        using var root = new TempFolder();
        string trace = Path.Combine(root.Path, "Earshot", "trace");
        Directory.CreateDirectory(Path.Combine(trace, NewNonce()));
        Directory.CreateDirectory(Path.Combine(trace, "not-a-run"));
        var log = new CapturingLog();
        var files = new TraceUserFiles(trace, new Boot.Gate.NtfsFolderSecurity(), log);

        Assert.IsNotNull(files.TrustProblem());
        Assert.AreEqual(1, files.LeftoverRunFolders());
    }

    [TestMethod]
    public void ATraceRunWithoutElevationIsRefusedBeforeAnythingRuns()
    {
        bool ran = false;
        int code = Program.RunTrace(["trace", "4321", "0123456789abcdef0123456789abcdef"], new CapturingLog(), _ =>
        {
            ran = true;
            return TraceOutcome.Saved;
        }, FakeToken.PlainUser);

        Assert.AreEqual(ExitCodes.Refused, code);
        Assert.IsFalse(ran);
    }

    [TestMethod]
    public void ATraceRunThatThrowsIsLoggedAndIsASoftwareFailure()
    {
        var log = new CapturingLog();
        int code = Program.RunTrace(["trace", "4321", "0123456789abcdef0123456789abcdef"], log, _ => throw new InvalidOperationException("boom"), FakeToken.ElevatedUser, []);

        Assert.AreEqual(ExitCodes.Software, code);
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Error, "boom"));
    }
}
