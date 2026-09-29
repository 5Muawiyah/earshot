using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;
using Earshot.Service;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Service;

// What the service run mode returns, and what it does first: the service control manager either takes the process or
// refuses it, and a process it refuses has opened nothing.
[TestClass]
public sealed class HandBackServiceProgramTests
{
    private static GateExitCode Run(FakeServiceHost host, CapturingLog log, out bool gateBuilt)
    {
        bool built = false;
        GateExitCode exit = HandBackServiceProgram.Run(
            host, () => FakeToken.System, new FakeFolderSecurity(), Path.GetTempPath(), Path.GetTempPath(), log, TimeProvider.System,
            (_, _) =>
            {
                built = true;
                return new PreshutdownResult(GateExitCode.Success, null, "x", true, []);
            });
        gateBuilt = built;
        return exit;
    }

    [TestMethod]
    public void AProcessTheControlManagerDidNotStartExitsWithNotAServiceAndRegistersNothing()
    {
        var host = new FakeServiceHost { DispatcherError = AdvApi32.ERROR_FAILED_SERVICE_CONTROLLER_CONNECT };
        var log = new CapturingLog();

        GateExitCode exit = Run(host, log, out bool gateBuilt);

        Assert.AreEqual(GateExitCode.NotAService, exit);
        Assert.AreEqual(23, (int)exit);
        Assert.AreEqual("not-a-service", GateExitCodes.ResultName(exit));
        Assert.AreEqual("dispatch", string.Join(",", host.Events), "No handler is registered and no status reported.");
        Assert.IsFalse(gateBuilt);
        Assert.IsTrue(log.Has(LogLevel.Error, "Hand-back service: refused, not started by the service control manager (Win32 1063)."));
    }

    [TestMethod]
    public void AnotherDispatcherFailureExitsWithFailedAndTheCode()
    {
        var host = new FakeServiceHost { DispatcherError = 1084 };
        var log = new CapturingLog();

        GateExitCode exit = Run(host, log, out _);

        Assert.AreEqual(GateExitCode.Failed, exit);
        Assert.IsTrue(log.Has(LogLevel.Error, "(Win32 1084)"));
        Assert.AreEqual("dispatch", string.Join(",", host.Events));
    }

    [TestMethod]
    public void AControlHandlerThatCannotBeRegisteredEndsTheProcessWithFailed()
    {
        var host = new FakeServiceHost { RegisterError = 1083 };
        var log = new CapturingLog();

        GateExitCode exit = Run(host, log, out _);

        Assert.AreEqual(GateExitCode.Failed, exit);
        Assert.IsTrue(log.Has(LogLevel.Error, "Hand-back service: the control handler was not registered (Win32 1083)."));
        Assert.IsEmpty(host.Reports, "Nothing can be reported without a handler.");
    }

    [TestMethod]
    public void TheServiceNameHandedToTheControlManagerIsThePlansName()
    {
        var host = new FakeServiceHost { DispatcherError = AdvApi32.ERROR_FAILED_SERVICE_CONTROLLER_CONNECT };

        Run(host, new CapturingLog(), out _);

        Assert.AreEqual("EarshotHandBack", host.DispatchedName);
    }

    // The whole run mode, through Main, Dispatch and the real dispatcher, started from a console: the control manager
    // refuses the process at once, nothing is opened, read or written, and the exit code says why. The real dispatcher
    // can be called only once in a process, and the host test process spends that call in ServiceHostRealTests, so this
    // runs the built Earshot.exe as a child. It is the execution of the entry point the fakes above stand in for.
    [TestMethod]
    public void TheServiceRunModeStartedFromAConsoleIsRefusedByTheControlManager()
    {
        (int exit, string output) = RunEarshot(safeMode: false, "service");

        Assert.AreEqual((int)GateExitCode.NotAService, exit, output);
    }

    // Started with the test switches set, as the gate does, it is refused before the control manager is asked.
    [TestMethod]
    public void TheServiceRunModeIsRefusedInSafeModeBeforeItReachesTheControlManager()
    {
        (int exit, string output) = RunEarshot(safeMode: true, "service");

        Assert.AreEqual(ExitCodes.Refused, exit, output);
    }

    private static (int ExitCode, string Output) RunEarshot(bool safeMode, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Earshot.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        // Only what the run mode reads: no data root, and safe mode only when asked for.
        info.Environment.Remove(Paths.DataRootVariable);
        info.Environment.Remove(Paths.SafeModeVariable);
        if (safeMode)
        {
            info.Environment[Paths.SafeModeVariable] = "1";
        }

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        Assert.IsTrue(process.WaitForExit(TimeSpan.FromSeconds(30)), "Earshot.exe service did not end.");
        return (process.ExitCode, output.Result + error.Result);
    }

    [TestMethod]
    public void TheServiceRunModeTakesNoArguments()
    {
        var log = new CapturingLog();
        Paths real = Paths.FromEnvironment(_ => null);

        Assert.AreEqual((int)GateExitCode.Rejected, Program.Dispatch(["service", "block"], real, log));
        Assert.AreEqual((int)GateExitCode.Rejected, Program.Dispatch(["service", ""], real, log));
    }
}
