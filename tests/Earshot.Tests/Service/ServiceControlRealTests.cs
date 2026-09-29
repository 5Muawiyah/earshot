using System.Security.AccessControl;
using Earshot.Boot;
using Earshot.Interop;
using Earshot.Service;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Service;

// The real service control manager read as a standard user can read it: the reads only. Creating, configuring,
// starting, stopping or deleting a service needs an administrator, and no test does any of them. These are the
// executions that prove the query marshalling (QUERY_SERVICE_CONFIGW, SERVICE_STATUS_PROCESS, the pre-shutdown
// setting and the access list) against the real manager.
[TestClass]
public sealed class ServiceControlRealTests
{
    public TestContext? TestContext { get; set; }

    // Task Scheduler's own service, which Earshot already depends on and every Windows 11 machine has.
    private const string SystemService = "Schedule";

    private static readonly uint[] DocumentedStates =
        [AdvApi32.SERVICE_STOPPED, AdvApi32.SERVICE_START_PENDING, AdvApi32.SERVICE_STOP_PENDING, AdvApi32.SERVICE_RUNNING];

    [TestMethod]
    public void QueriesAnInstalledSystemService()
    {
        ServiceQuery read = new WindowsServiceControl().Query(SystemService);

        Assert.AreEqual(ServicePresence.Present, read.Presence);
        Assert.IsTrue(read.State is { } state && DocumentedStates.Contains(state), "state " + read.State);
        Assert.IsFalse(string.IsNullOrWhiteSpace(read.ImagePath));
        Assert.IsFalse(string.IsNullOrWhiteSpace(read.Account));
        Assert.IsFalse(string.IsNullOrWhiteSpace(read.DisplayName));
        Assert.IsNotNull(read.StartType);
        Assert.IsNotNull(read.Sddl);
        TestContext?.WriteLine("Schedule: " + ServiceSteps.StateName(read.State) + ", start type " + read.StartType + ", account " + read.Account +
                               ", pre-shutdown " + read.PreshutdownTimeoutMs + " ms, access list " + read.Sddl);
        Assert.IsNotNull(new RawSecurityDescriptor(read.Sddl).DiscretionaryAcl);
        foreach (Earshot.Contracts.StepOutcome step in read.Steps.Where(s => s.Step != "service-preshutdown"))
        {
            Assert.IsTrue(step.Ok, step.Step + " " + step.CodeName);
        }
    }

    [TestMethod]
    public void QueryOfAMissingServiceIsNotPresent()
    {
        ServiceQuery read = new WindowsServiceControl().Query("EarshotTest" + Guid.NewGuid().ToString("N"));

        Assert.AreEqual(ServicePresence.Missing, read.Presence);
        Assert.IsNull(read.State);
        Assert.IsNull(read.Sddl);
        Assert.AreEqual(1, read.Steps.Count);
        Assert.AreEqual((int)AdvApi32.ERROR_SERVICE_DOES_NOT_EXIST, read.Steps[0].Code);
        Assert.AreEqual("ERROR_SERVICE_DOES_NOT_EXIST", read.Steps[0].CodeName);
        TestContext?.WriteLine("Missing service: code " + read.Steps[0].Code + " " + read.Steps[0].CodeName);
    }

    // The default access list gives authenticated users interrogate and the user-defined controls, so the check that
    // Earshot's own service has none of that must not pass what the manager really returns for a default service.
    [TestMethod]
    public void TheServiceAclCheckDoesNotPassARealDefaultAccessList()
    {
        ServiceQuery read = new WindowsServiceControl().Query(SystemService);

        Assert.IsNotNull(read.Sddl);
        IReadOnlyList<string> problems = AclCheck.CheckService(read.Sddl);
        TestContext?.WriteLine("CheckService on the real access list: " + string.Join(" | ", problems));
        Assert.IsTrue(problems.Count >= 1, read.Sddl);
    }

    [TestMethod]
    public void TheReadBackCheckDoesNotPassAnotherServiceForEarshotsSpec()
    {
        ServiceQuery read = new WindowsServiceControl().Query(SystemService);

        IReadOnlyList<string> problems = ServiceCheck.Verify(read, ServicePlan.Spec(@"C:\Program Files\Earshot"));

        Assert.IsTrue(problems.Any(p => p.Contains("image path", StringComparison.Ordinal)), string.Join(" | ", problems));
    }
}
