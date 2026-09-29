using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Service;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Service;

// The service's state machine against a fake control manager. The fake runs the service's main function on its own
// thread, as the real dispatcher does, and the test plays the manager: it sends controls on its own thread and reads
// what the service reported. The shut-down block itself is a fake that a test can hold, release or make throw.
[TestClass]
public sealed class HandBackServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private sealed class Harness : IDisposable
    {
        private readonly TempFolder _temp = new();
        private readonly ManualResetEventSlim _released = new(true);

        public Harness()
        {
            Install = Path.Combine(_temp.Path, "ProgramFiles", "Earshot");
            Machine = Path.Combine(_temp.Path, "ProgramData", "Earshot");
            Directory.CreateDirectory(Install);
            Directory.CreateDirectory(Machine);
        }

        public string Install { get; }

        public string Machine { get; }

        public FakeServiceHost Host { get; } = new();

        public FakeFolderSecurity Folders { get; } = new();

        public CapturingLog Log { get; } = new();

        public ManualTime Time { get; } = new();

        public IProcessToken Token { get; set; } = FakeToken.System;

        public string BaseDirectory { get; set; } = "";

        public List<(string Nonce, DateTimeOffset Deadline)> GateCalls { get; } = new();

        public Exception? GateThrows { get; set; }

        public PreshutdownResult GateResult { get; set; } =
            new(GateExitCode.Success, nameof(BlockState.Blocked), "already blocked", true, []) { StatusStep = new StepOutcome("write-status", true, 0, "S_OK", null) };

        // Holds the shut-down block until Release is called.
        public void Hold() => _released.Reset();

        public void Release() => _released.Set();

        public bool GateStarted => GateCalls.Count > 0;

        public HandBackService Service { get; private set; } = null!;

        public Task<GateExitCode> Start()
        {
            if (BaseDirectory.Length == 0)
            {
                BaseDirectory = Install + Path.DirectorySeparatorChar;
            }

            return Task.Run(() => HandBackServiceProgram.Run(
                Host, () => Token, Folders, Install, Machine, Log, Time,
                (nonce, deadline) =>
                {
                    lock (GateCalls)
                    {
                        GateCalls.Add((nonce, deadline));
                    }

                    _released.Wait(Patience);
                    if (GateThrows is not null)
                    {
                        throw GateThrows;
                    }

                    return GateResult;
                },
                () => BaseDirectory));
        }

        public void WaitRunning() => Assert.IsTrue(Host.WaitForState(AdvApi32.SERVICE_RUNNING, Patience), "The service never reported running.");

        public static bool WaitFor(Func<bool> condition)
        {
            DateTime end = DateTime.UtcNow + Patience;
            while (DateTime.UtcNow < end)
            {
                if (condition())
                {
                    return true;
                }

                Thread.Sleep(5);
            }

            return condition();
        }

        public static GateExitCode Finish(Task<GateExitCode> run)
        {
            Assert.IsTrue(run.Wait(Patience), "The service did not end.");
            return run.Result;
        }

        public void Dispose()
        {
            _released.Set();
            _temp.Dispose();
        }
    }

    private static string States(IEnumerable<ServiceStatusReport> reports) =>
        string.Join(",", reports.Select(r => r.State));

    [TestMethod]
    public void AServiceThatIsNotLocalSystemStopsAtStartWithTheReasonAndNeverRuns()
    {
        using var h = new Harness();
        h.Token = FakeToken.ElevatedUser;

        Task<GateExitCode> run = h.Start();
        GateExitCode exit = Harness.Finish(run);

        Assert.AreEqual(GateExitCode.NotElevated, exit);
        Assert.AreEqual("2,1", States(h.Host.Reports), "Start pending, then stopped; running is never reported.");
        ServiceStatusReport stopped = h.Host.Reports[^1];
        Assert.AreEqual(AdvApi32.ERROR_SERVICE_SPECIFIC_ERROR, stopped.Win32ExitCode);
        Assert.AreEqual((uint)GateExitCode.NotElevated, stopped.ServiceSpecificExitCode);
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "Hand-back service: refused, not running as Local System (not-elevated)."));
        Assert.IsEmpty(h.GateCalls);
    }

    [TestMethod]
    public void AProgramOutsideTheInstallFolderStopsAtStartWithItsOwnCode()
    {
        using var h = new Harness();
        h.BaseDirectory = Path.Combine(Path.GetTempPath(), "somewhere-else") + Path.DirectorySeparatorChar;

        GateExitCode exit = Harness.Finish(h.Start());

        Assert.AreEqual(GateExitCode.NotFromInstallFolder, exit);
        Assert.AreEqual("2,1", States(h.Host.Reports));
        Assert.AreEqual((uint)GateExitCode.NotFromInstallFolder, h.Host.Reports[^1].ServiceSpecificExitCode);
        Assert.AreEqual(24u, h.Host.Reports[^1].ServiceSpecificExitCode);
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "(not-from-install-folder)"));
    }

    [TestMethod]
    public void AProgramInAFolderThatMerelyStartsWithTheInstallPathIsOutside()
    {
        using var h = new Harness();
        h.BaseDirectory = h.Install + "-evil" + Path.DirectorySeparatorChar;

        Assert.AreEqual(GateExitCode.NotFromInstallFolder, Harness.Finish(h.Start()));
    }

    [TestMethod]
    public void AnInstallFolderAStandardUserCouldWriteStopsTheServiceAtStart()
    {
        using var h = new Harness();
        h.Folders.SddlFor = path => string.Equals(path, h.Install, StringComparison.OrdinalIgnoreCase)
            ? "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;0x1200a9;;;BU)(A;ID;FA;;;AU)"
            : null;

        GateExitCode exit = Harness.Finish(h.Start());

        Assert.AreEqual(GateExitCode.FolderNotSecure, exit);
        Assert.AreEqual("2,1", States(h.Host.Reports));
        Assert.AreEqual(10u, h.Host.Reports[^1].ServiceSpecificExitCode);
        Assert.IsEmpty(h.GateCalls);
    }

    [TestMethod]
    public void AHappyStartReportsStartPendingThenRunningAcceptingOnlyStopAndPreshutdown()
    {
        using var h = new Harness();

        Task<GateExitCode> run = h.Start();
        h.WaitRunning();

        Assert.AreEqual(2, h.Host.Reports.Count);
        ServiceStatusReport pending = h.Host.Reports[0];
        Assert.AreEqual(AdvApi32.SERVICE_START_PENDING, pending.State);
        Assert.AreEqual(0u, pending.ControlsAccepted, "No control is accepted while the start is pending.");
        Assert.AreEqual(1u, pending.CheckPoint);
        Assert.AreEqual(3_000u, pending.WaitHint);
        ServiceStatusReport running = h.Host.Reports[1];
        Assert.AreEqual(AdvApi32.SERVICE_RUNNING, running.State);
        Assert.AreEqual(0x101u, running.ControlsAccepted, "Stop and pre-shutdown, nothing else.");
        Assert.AreEqual(0u, running.ControlsAccepted & AdvApi32.SERVICE_ACCEPT_SHUTDOWN);
        Assert.AreEqual(0u, running.CheckPoint);
        Assert.AreEqual(0u, running.WaitHint);
        Assert.AreEqual(0u, running.Win32ExitCode);
        Assert.IsTrue(h.Host.HandlerRegistered);
        Assert.AreEqual("dispatch,register", string.Join(",", h.Host.Events.Take(2)), "The handler is registered before the first report.");

        h.Host.Send(AdvApi32.SERVICE_CONTROL_STOP);
        Harness.Finish(run);
    }

    [TestMethod]
    public void ControlsAreNotTakenBeforeTheServiceIsRunning()
    {
        using var h = new Harness();
        using var service = new HandBackService(h.Host, () => FakeToken.System, h.Folders, h.Install, h.Machine, h.Log, h.Time, (_, _) => h.GateResult);

        Assert.AreEqual(AdvApi32.ERROR_CALL_NOT_IMPLEMENTED, service.Handle(AdvApi32.SERVICE_CONTROL_STOP, 0, 0));
        Assert.AreEqual(AdvApi32.ERROR_CALL_NOT_IMPLEMENTED, service.Handle(AdvApi32.SERVICE_CONTROL_PRESHUTDOWN, 0, 0));
        Assert.AreEqual(AdvApi32.ERROR_CALL_NOT_IMPLEMENTED, service.Handle(AdvApi32.SERVICE_CONTROL_INTERROGATE, 0, 0));
        Assert.IsEmpty(h.Host.Reports);
    }

    [TestMethod]
    public void InterrogateIsAnsweredAtOnceWithNoReport()
    {
        using var h = new Harness();
        Task<GateExitCode> run = h.Start();
        h.WaitRunning();
        int before = h.Host.Reports.Count;

        uint answer = h.Host.Send(AdvApi32.SERVICE_CONTROL_INTERROGATE);

        Assert.AreEqual(0u, answer);
        Assert.HasCount(before, h.Host.Reports, "The state did not change, so nothing is reported.");
        h.Host.Send(AdvApi32.SERVICE_CONTROL_STOP);
        Harness.Finish(run);
    }

    // Anything but stop, interrogate and pre-shutdown, whoever sent it and whether or not the manager should have.
    [TestMethod]
    [DataRow(128u)]
    [DataRow(129u)]
    [DataRow(200u)]
    [DataRow(255u)]
    [DataRow(AdvApi32.SERVICE_CONTROL_SHUTDOWN)]
    [DataRow(AdvApi32.SERVICE_CONTROL_POWEREVENT)]
    [DataRow(AdvApi32.SERVICE_CONTROL_SESSIONCHANGE)]
    [DataRow(AdvApi32.SERVICE_CONTROL_PAUSE)]
    [DataRow(AdvApi32.SERVICE_CONTROL_CONTINUE)]
    [DataRow(0u)]
    [DataRow(7u)]
    [DataRow(uint.MaxValue)]
    public void AnyOtherControlIsNotImplementedAndDoesNothing(uint control)
    {
        using var h = new Harness();
        Task<GateExitCode> run = h.Start();
        h.WaitRunning();
        int before = h.Host.Reports.Count;

        uint answer = h.Host.Send(control);

        Assert.AreEqual(AdvApi32.ERROR_CALL_NOT_IMPLEMENTED, answer);
        Assert.HasCount(before, h.Host.Reports, "No report for a control that is not handled.");
        Assert.IsEmpty(h.GateCalls, "No work runs for a control that is not handled.");
        h.Host.Send(AdvApi32.SERVICE_CONTROL_STOP);
        Harness.Finish(run);
        Assert.IsEmpty(h.GateCalls);
    }

    [TestMethod]
    public void StopReportsStopPendingThenStoppedOnceAndRunsNoWork()
    {
        using var h = new Harness();
        Task<GateExitCode> run = h.Start();
        h.WaitRunning();

        uint answer = h.Host.Send(AdvApi32.SERVICE_CONTROL_STOP);
        GateExitCode exit = Harness.Finish(run);

        Assert.AreEqual(0u, answer);
        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.AreEqual("2,4,3,1", States(h.Host.Reports));
        ServiceStatusReport pending = h.Host.Reports[2];
        Assert.AreEqual(1u, pending.CheckPoint);
        Assert.AreEqual(10_000u, pending.WaitHint);
        Assert.AreEqual(0u, pending.ControlsAccepted);
        ServiceStatusReport stopped = h.Host.Reports[3];
        Assert.AreEqual(0u, stopped.Win32ExitCode);
        Assert.AreEqual(1, h.Host.Reports.Count(r => r.State == AdvApi32.SERVICE_STOPPED));
        Assert.IsEmpty(h.GateCalls);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back service: stop received."));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back service: stopped."));
    }

    // The handler returns before the work does: the work waits on a gate the test opens only after the handler has
    // returned, and the deadline handed to it is the budget counted from the moment the control arrived.
    [TestMethod]
    public void PreshutdownReturnsAtOnceAndRunsTheBlockOnceWithTheBudgetFromWhenItArrived()
    {
        using var h = new Harness();
        h.Hold();
        Task<GateExitCode> run = h.Start();
        h.WaitRunning();
        DateTimeOffset arrived = h.Time.GetUtcNow();

        uint answer = h.Host.Send(AdvApi32.SERVICE_CONTROL_PRESHUTDOWN);
        h.Time.Advance(TimeSpan.FromSeconds(3));

        Assert.AreEqual(0u, answer, "The handler returned while the block is still held.");
        Assert.IsTrue(Harness.WaitFor(() => h.GateStarted), "The block never started.");
        ServiceStatusReport pending = h.Host.Reports.Last(r => r.State == AdvApi32.SERVICE_STOP_PENDING);
        Assert.AreEqual(1u, pending.CheckPoint);
        Assert.AreEqual(10_000u, pending.WaitHint);
        Assert.IsFalse(h.Host.Reports.Any(r => r.State == AdvApi32.SERVICE_STOPPED), "Not stopped while the block runs.");
        Assert.IsFalse(run.IsCompleted);

        h.Release();
        GateExitCode exit = Harness.Finish(run);

        Assert.AreEqual(GateExitCode.Success, exit);
        (string nonce, DateTimeOffset deadline) = h.GateCalls.Single();
        Assert.IsTrue(BoundaryValidation.IsNonce(nonce));
        Assert.AreEqual(arrived + TimeSpan.FromMilliseconds(8_000), deadline, "The deadline is the arrival plus the 8,000 ms budget, not counted from a later now.");
        Assert.AreEqual(1, h.Host.Reports.Count(r => r.State == AdvApi32.SERVICE_STOPPED));
        Assert.AreEqual("2,4,3,1", States(h.Host.Reports));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back service: preshutdown received at "));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back service: nothing to do: already blocked."));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back service: finished in 3000 ms; block not sent: already blocked; state Blocked; status file written."));
    }

    [TestMethod]
    public void APreshutdownReceivedTwiceRunsOnceAndAStopDuringTheWorkWaitsForIt()
    {
        using var h = new Harness();
        h.Hold();
        Task<GateExitCode> run = h.Start();
        h.WaitRunning();

        Assert.AreEqual(0u, h.Host.Send(AdvApi32.SERVICE_CONTROL_PRESHUTDOWN));
        Assert.IsTrue(Harness.WaitFor(() => h.GateStarted));
        Assert.AreEqual(0u, h.Host.Send(AdvApi32.SERVICE_CONTROL_PRESHUTDOWN));
        Assert.AreEqual(0u, h.Host.Send(AdvApi32.SERVICE_CONTROL_STOP));

        Assert.IsFalse(run.Wait(TimeSpan.FromMilliseconds(300)), "Stopped early: the work was still running.");
        Assert.IsFalse(h.Host.Reports.Any(r => r.State == AdvApi32.SERVICE_STOPPED));

        h.Release();
        Harness.Finish(run);

        Assert.HasCount(1, h.GateCalls);
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back service: preshutdown received again; the first run continues."));
        Assert.AreEqual(1, h.Host.Reports.Count(r => r.State == AdvApi32.SERVICE_STOPPED));
        Assert.AreEqual(AdvApi32.SERVICE_STOPPED, h.Host.Reports[^1].State, "Stopped is the last report.");
    }

    [TestMethod]
    public void AShutDownBlockThatThrowsIsLoggedWithItsTypeAndTheServiceStillStopsOnce()
    {
        using var h = new Harness();
        h.GateThrows = new InvalidOperationException("boom");
        Task<GateExitCode> run = h.Start();
        h.WaitRunning();

        h.Host.Send(AdvApi32.SERVICE_CONTROL_PRESHUTDOWN);
        GateExitCode exit = Harness.Finish(run);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.IsTrue(h.Log.Entries.Any(e => e.Level == LogLevel.Error && e.Message.Contains("InvalidOperationException", StringComparison.Ordinal) && e.Exception is InvalidOperationException));
        Assert.AreEqual(1, h.Host.Reports.Count(r => r.State == AdvApi32.SERVICE_STOPPED));
        Assert.AreEqual(0u, h.Host.Reports[^1].Win32ExitCode);
    }

    [TestMethod]
    public void ABlockThatFailedIsLoggedAsAWarningWithEachPartOfTheOutcome()
    {
        using var h = new Harness();
        h.GateResult = new PreshutdownResult(GateExitCode.Partial, nameof(BlockState.Unknown), "block sent", false, [])
        {
            BlockSent = true,
            BlockSentUtc = new DateTimeOffset(2026, 9, 15, 12, 0, 1, TimeSpan.Zero),
            VetoedNodes = 1,
            RetryAfter = TimeSpan.FromMilliseconds(1_000),
            StatusStep = StepOutcomes.FromHResult("write-status", unchecked((int)0x80070005), "denied", ok: false),
        };
        Task<GateExitCode> run = h.Start();
        h.WaitRunning();

        h.Host.Send(AdvApi32.SERVICE_CONTROL_PRESHUTDOWN);
        Harness.Finish(run);

        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back service: block sent at 2026-09-15T12:00:01.000Z."));
        Assert.IsTrue(h.Log.Has(LogLevel.Info, "Hand-back service: veto on 1 node(s); sent again after 1000 ms."));
        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "block partial; state Unknown; status file not written: E_ACCESSDENIED."));
    }

    [TestMethod]
    public void ARunThatRanOutOfBudgetWaitingSaysWhichLock()
    {
        using var h = new Harness();
        h.GateResult = new PreshutdownResult(GateExitCode.Failed, null, "another device change held the lock", true, [])
        {
            CutShortWaitingFor = "the device change lock",
        };
        Task<GateExitCode> run = h.Start();
        h.WaitRunning();

        h.Host.Send(AdvApi32.SERVICE_CONTROL_PRESHUTDOWN);
        Harness.Finish(run);

        Assert.IsTrue(h.Log.Has(LogLevel.Warn, "Hand-back service: cut short at 0 ms while waiting for the device change lock; nothing was changed."));
    }

    [TestMethod]
    public void AStatusReportThatFailsIsLoggedWithItsCode()
    {
        using var h = new Harness();
        h.Host.SetStatusError = 6;

        Task<GateExitCode> run = h.Start();

        Assert.IsTrue(Harness.WaitFor(() => h.Host.Events.Any(e => e == "status:4")));
        Assert.IsTrue(h.Log.Has(LogLevel.Error, "Hand-back service: status running was not reported (Win32 6)."));
        h.Host.Send(AdvApi32.SERVICE_CONTROL_STOP);
        Harness.Finish(run);
    }

    // The service names no device, address or container in any line it logs.
    [TestMethod]
    public void NoLogLineNamesADeviceAddressOrContainer()
    {
        using var h = new Harness();
        Task<GateExitCode> run = h.Start();
        h.WaitRunning();
        h.Host.Send(AdvApi32.SERVICE_CONTROL_PRESHUTDOWN);
        Harness.Finish(run);

        foreach (LogEntry entry in h.Log.Entries)
        {
            Assert.DoesNotContain(RecordedNodes.AirPodsAddress, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(RecordedNodes.AirPodsContainer.ToString(), entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("Hand-back service: ", entry.Message);
        }
    }
}
