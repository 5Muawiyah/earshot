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

    [TestMethod]
    public void AProcessThatIsNotThereGivesAReasonNotASid()
    {
        SecurityIdentifier? sid = TrayUser.SidOf(int.MaxValue - 7, out string? problem);

        Assert.IsNull(sid);
        Assert.IsFalse(string.IsNullOrEmpty(problem));
    }

    [TestMethod]
    public void TheReadGrantAddsAReadRuleForTheTraysUser()
    {
        using var folder = new TempFolder();
        string file = folder.File("x.etl");
        File.WriteAllText(file, "x");
        var machine = new TraceMachine(Paths.FromEnvironment(n => n == Paths.DataRootVariable ? folder.Path : null), new Boot.Gate.NtfsFolderSecurity(), new CapturingLog());

        string? problem = machine.GrantRead(file, Environment.ProcessId);

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
            using var helper = new TraceSignals(new TraceRequest(sleeper.Id, nonce));
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
        using var helper = new TraceSignals(new TraceRequest(sleeper.Id, nonce));
        Assert.IsNull(helper.Open());

        sleeper.Kill(entireProcessTree: true);
        sleeper.WaitForExit();

        Assert.AreEqual(TraceStopReason.TrayExited, helper.WaitForStop(TimeSpan.FromSeconds(10)));
        Assert.IsFalse(helper.TrayRunning());
    }

    [TestMethod]
    public void DoneReachesTheTrayAndCopiedReachesTheHelper()
    {
        string nonce = NewNonce();
        using var tray = TrayTraceSignals.Create(nonce);
        using var helper = new TraceSignals(new TraceRequest(Environment.ProcessId, nonce));
        Assert.IsNull(helper.Open());

        helper.SignalDone();
        Assert.IsTrue(tray.WaitDone(CancellationToken.None));
        Assert.IsFalse(helper.WaitCopied(TimeSpan.FromMilliseconds(100)));
        tray.SetCopied();
        Assert.IsTrue(helper.WaitCopied(TimeSpan.FromSeconds(10)));
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
        using var helper = new TraceSignals(new TraceRequest(Environment.ProcessId, NewNonce()));

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
        var files = new TraceUserFiles(root.Path);

        Assert.IsTrue(TraceResultFile.TryParse(files.ReadResult(nonce), out _));
        string destination = Path.Combine(root.Path, "Local", "Earshot", "traces");
        Assert.IsNull(files.Copy(nonce, "bluetooth-20261025T143005Z.etl", destination, out string copied));
        Assert.AreEqual("trace", File.ReadAllText(copied));
        Assert.IsNotNull(files.Copy(nonce, "bluetooth-20261025T143005Z.etl", destination, out _), "An existing file is not overwritten.");
        Assert.IsNotNull(files.Copy(nonce, @"..\result.txt", destination, out _));
        Assert.IsNotNull(files.Copy(nonce, "result.txt", destination, out _), "Only .etl files are copied.");
    }

    [TestMethod]
    public void AMissingResultOrMarkerReadsAsNothing()
    {
        using var root = new TempFolder();
        var files = new TraceUserFiles(Path.Combine(root.Path, "no-such-folder"));

        Assert.IsNull(files.ReadResult(NewNonce()));
        Assert.IsNull(files.ReadMarker());
    }

    [TestMethod]
    public void AProcessThatIsRunningIsAliveAndOneThatIsNotIsNot()
    {
        var files = new TraceUserFiles(Path.GetTempPath());

        Assert.IsTrue(files.ProcessAlive(Environment.ProcessId));
        Assert.IsFalse(files.ProcessAlive(int.MaxValue - 7));
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
        int code = Program.RunTrace(["trace", "4321", "0123456789abcdef0123456789abcdef"], log, _ => throw new InvalidOperationException("boom"), FakeToken.ElevatedUser);

        Assert.AreEqual(ExitCodes.Software, code);
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Error, "boom"));
    }
}
