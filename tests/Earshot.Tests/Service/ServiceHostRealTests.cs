using Earshot.Interop;
using Earshot.Service;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Service;

// The real service host, run where it can be without the control manager starting it: from this test process, which
// is a console program, not a service. Every other test of the service runs against a fake host, so these are the
// executions that prove the imports, the table marshalling and the detection the service's own refusal depends on.
// Nothing here registers, starts or controls a service, and none needs elevation.
[TestClass]
public sealed class ServiceHostRealTests
{
    public TestContext? TestContext { get; set; }

    // StartServiceCtrlDispatcherW fails at once with ERROR_FAILED_SERVICE_CONTROLLER_CONNECT for a process the
    // control manager did not start, and never calls the service's main function. If it ever did the test would fail.
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicectrldispatcherw
    [TestMethod]
    public void DispatcherRefusesAProcessTheControlManagerDidNotStart()
    {
        bool called = false;
        var host = new WindowsServiceHost();

        uint error = host.RunDispatcher(ServicePlan.ServiceName, _ => called = true);

        TestContext?.WriteLine("StartServiceCtrlDispatcherW from a console process: Win32 " + error);
        Assert.AreEqual(AdvApi32.ERROR_FAILED_SERVICE_CONTROLLER_CONNECT, error);
        Assert.IsFalse(called);
    }

    // A control handler cannot be registered, and no status reported, by a process that is not a service. This
    // executes the two imports and the SERVICE_STATUS marshalling and shows they fail with a code rather than crash.
    [TestMethod]
    public void RegisteringAndReportingFromAConsoleProcessFailsWithACode()
    {
        var host = new WindowsServiceHost();

        nint handle = host.RegisterHandler(ServicePlan.ServiceName, (_, _, _) => 0, out uint registerError);
        TestContext?.WriteLine("RegisterServiceCtrlHandlerExW from a console process: handle " + handle + ", Win32 " + registerError);
        Assert.AreEqual(0, handle);
        Assert.AreNotEqual(0u, registerError);

        var report = new ServiceStatusReport(AdvApi32.SERVICE_RUNNING, AdvApi32.SERVICE_ACCEPT_STOP, 0, 0, 0, 0);
        bool reported = host.SetStatus(0, report, out uint statusError);
        TestContext?.WriteLine("SetServiceStatus with no handle: " + reported + ", Win32 " + statusError);
        Assert.IsFalse(reported);
        Assert.AreNotEqual(0u, statusError);
    }
}
