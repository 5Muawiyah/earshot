using System.Security.AccessControl;
using Earshot.Interop;
using Earshot.Service;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Service;

[TestClass]
public sealed class ServicePlanTests
{
    private const string Install = @"C:\Program Files\Earshot";

    private static readonly string[] ExpectedAces =
        ["AccessAllowed S-1-5-18 0xF01FF", "AccessAllowed S-1-5-32-544 0xF01FF", "AccessAllowed S-1-5-11 0x2000D"];

    private static ServiceQuery Registered(
        uint? startType = null, string? image = null, string? account = null, string? display = null,
        uint? preshutdown = null, string? sddl = null, uint? type = null, uint? errorControl = null, bool nullSddl = false)
    {
        ServiceSpec spec = ServicePlan.Spec(Install);
        return new ServiceQuery(
            ServicePresence.Present, [], AdvApi32.SERVICE_RUNNING, 4242, type ?? spec.ServiceType, startType ?? spec.StartType,
            errorControl ?? spec.ErrorControl, image ?? spec.ImagePath, account ?? spec.Account, display ?? spec.DisplayName,
            preshutdown ?? spec.PreshutdownTimeoutMs, nullSddl ? null : sddl ?? spec.Sddl)
        {
            FailureActionCount = 0,
            FailureCommand = null,
            DelayedAutoStart = false,
            TriggerCount = 0,
            RequiredPrivileges = [],
            ServiceSidType = ServicePlan.ServiceSidTypeNone,
            Description = spec.Description,
        };
    }

    [TestMethod]
    public void TheSpecHoldsEveryRegisteredValue()
    {
        ServiceSpec spec = ServicePlan.Spec(Install);

        Assert.AreEqual("EarshotHandBack", spec.Name);
        Assert.AreEqual("Earshot hand-back", spec.DisplayName, "What the Services list and Task Manager show: not the raw name.");
        Assert.AreEqual(
            "When Block at boot and Hand back are on, blocks the AirPods on this PC as Windows shuts down, so they can go back to your phone, even if Earshot is not running. Does nothing while the PC is in use.",
            spec.Description, "A plain sentence that says when the service acts (only with Block at boot and Hand back on) and what it does.");
        Assert.AreEqual("\"C:\\Program Files\\Earshot\\Earshot.exe\" service", spec.ImagePath);
        Assert.AreEqual(0x10u, spec.ServiceType, "Its own process, never interactive.");
        Assert.AreEqual(0x2u, spec.StartType, "Automatic, so it is running when the shut down starts.");
        Assert.AreEqual(0x1u, spec.ErrorControl);
        Assert.AreEqual("LocalSystem", spec.Account);
        Assert.AreEqual(10_000u, spec.PreshutdownTimeoutMs);
        Assert.IsFalse(spec.Description.Contains('\u2014'));
        Assert.IsFalse(spec.Name.Contains('\\') || spec.Name.Contains('/'));
    }

    [TestMethod]
    public void TheImagePathIsQuotedAndSelectsTheServiceRunModeWithNothingElse()
    {
        string path = ServicePlan.ImagePath(@"C:\Program Files\Earshot");

        Assert.StartsWith("\"C:\\Program Files\\Earshot\\Earshot.exe\"", path);
        Assert.EndsWith(" service", path);
        Assert.AreEqual(1, path.Split('"').Length - 1 - 1, "Exactly one quoted section (two quotes).");
        Assert.AreEqual("service", path[(path.LastIndexOf('"') + 2)..]);
    }

    // The exact string install applies. Any other list would give a standard user a right on the service.
    [TestMethod]
    public void TheServiceAccessListIsTheExactString()
    {
        Assert.AreEqual("D:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x2000D;;;AU)", ServicePlan.Spec(Install).Sddl);
    }

    [TestMethod]
    public void TheServiceAccessListGivesSystemAndAdministratorsEverythingAndUsersOnlyQuery()
    {
        var parsed = new RawSecurityDescriptor(ServicePlan.Sddl);

        Assert.AreNotEqual(0, (int)(parsed.ControlFlags & ControlFlags.DiscretionaryAclProtected));
        Assert.IsNotNull(parsed.DiscretionaryAcl);
        string[] aces = parsed.DiscretionaryAcl.Cast<CommonAce>()
            .Select(a => a.AceQualifier + " " + a.SecurityIdentifier.Value + " 0x" + ((uint)a.AccessMask).ToString("X", System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        CollectionAssert.AreEqual(ExpectedAces, aces);

        uint users = (uint)parsed.DiscretionaryAcl.Cast<CommonAce>().Single(a => a.SecurityIdentifier.Value == "S-1-5-11").AccessMask;
        Assert.AreEqual(0u, users & (AdvApi32.SERVICE_START | AdvApi32.SERVICE_STOP | AdvApi32.SERVICE_PAUSE_CONTINUE | AdvApi32.SERVICE_INTERROGATE |
                                     AdvApi32.SERVICE_USER_DEFINED_CONTROL | AdvApi32.SERVICE_CHANGE_CONFIG | AdvApi32.DELETE | AdvApi32.WRITE_DAC |
                                     AdvApi32.WRITE_OWNER));
        Assert.AreEqual(AdvApi32.SERVICE_QUERY_ONLY, users);
    }

    [TestMethod]
    public void AServiceRegisteredToTheSpecPassesTheReadBack()
    {
        Assert.IsEmpty(ServiceCheck.Verify(Registered(), ServicePlan.Spec(Install)));
    }

    [TestMethod]
    public void AMissingOrUnreadServiceFailsTheReadBack()
    {
        Assert.HasCount(1, ServiceCheck.Verify(new ServiceQuery(ServicePresence.Missing, []), ServicePlan.Spec(Install)));
        Assert.HasCount(1, ServiceCheck.Verify(new ServiceQuery(ServicePresence.Unknown, []), ServicePlan.Spec(Install)));
    }

    [TestMethod]
    public void EachDifferentValueFailsTheReadBackAndNamesIt()
    {
        ServiceSpec spec = ServicePlan.Spec(Install);
        (ServiceQuery Read, string Names)[] cases =
        [
            (Registered(startType: 3), "start type"),
            (Registered(type: 0x110), "service type"),
            (Registered(errorControl: 0), "error control"),
            (Registered(image: @"C:\Windows\Temp\Earshot.exe service"), "image path"),
            (Registered(image: @"C:\Program Files\Earshot\Earshot.exe service"), "image path"),
            (Registered(image: "\"C:\\Program Files\\Earshot\\Earshot.exe\""), "image path"),
            (Registered(account: @"NT AUTHORITY\LocalService"), "account"),
            (Registered(display: "Something else"), "display name"),
            (Registered(preshutdown: 180_000), "pre-shutdown time-out"),
        ];

        foreach ((ServiceQuery read, string names) in cases)
        {
            IReadOnlyList<string> problems = ServiceCheck.Verify(read, spec);
            Assert.HasCount(1, problems, names + ": " + string.Join(" | ", problems));
            StringAssert.Contains(problems[0], names);
        }
    }

    // What the plan leaves unset is checked, not assumed: a registration from an earlier install with a restart, a command
    // run at failure, a delayed start, a trigger, a service security identifier or a cut-down token is not the plan's.
    [TestMethod]
    public void EachSettingThePlanLeavesUnsetFailsTheReadBackAndNamesIt()
    {
        ServiceSpec spec = ServicePlan.Spec(Install);
        (ServiceQuery Read, string Names)[] cases =
        [
            (Registered() with { FailureActionCount = 1 }, "failure action"),
            (Registered() with { FailureCommand = @"C:\Temp\run.exe" }, "command when it fails"),
            (Registered() with { DelayedAutoStart = true }, "delayed start"),
            (Registered() with { TriggerCount = 2 }, "trigger count"),
            (Registered() with { ServiceSidType = 1 }, "service security identifier type"),
            (Registered() with { RequiredPrivileges = ["SeChangeNotifyPrivilege"] }, "SeChangeNotifyPrivilege"),
        ];

        foreach ((ServiceQuery read, string names) in cases)
        {
            IReadOnlyList<string> problems = ServiceCheck.Verify(read, spec);
            Assert.HasCount(1, problems, names + ": " + string.Join(" | ", problems));
            StringAssert.Contains(problems[0], names);
        }
    }

    [TestMethod]
    public void AnAccessListGrantingAnAuthenticatedUserStartFailsTheReadBack()
    {
        IReadOnlyList<string> problems = ServiceCheck.Verify(
            Registered(sddl: "D:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x2001D;;;AU)"), ServicePlan.Spec(Install));

        Assert.HasCount(1, problems);
        StringAssert.Contains(problems[0], "S-1-5-11");
    }

    // A registration from an earlier install keeps the raw name as its display name and whatever description it had, until install
    // brings it to the plan: the read-back says so for each, so an update or a repair that did not reach them is not passed.
    [TestMethod]
    public void ADisplayNameOrDescriptionThatIsNotThePlansFailsTheReadBack()
    {
        ServiceSpec spec = ServicePlan.Spec(Install);

        IReadOnlyList<string> raw = ServiceCheck.Verify(Registered(display: "EarshotHandBack"), spec);
        Assert.HasCount(1, raw);
        StringAssert.Contains(raw[0], "display name");

        IReadOnlyList<string> old = ServiceCheck.Verify(Registered() with { Description = "Hands the AirPods back when this computer shuts down, if the Earshot tray icon did not." }, spec);
        Assert.HasCount(1, old);
        StringAssert.Contains(old[0], "description");

        IReadOnlyList<string> none = ServiceCheck.Verify(Registered() with { Description = "" }, spec);
        Assert.HasCount(1, none);
        StringAssert.Contains(none[0], "description");
    }

    [TestMethod]
    public void AValueThatCouldNotBeReadFailsTheReadBackRatherThanPassing()
    {
        ServiceSpec spec = ServicePlan.Spec(Install);
        ServiceQuery read = Registered() with
        {
            StartType = null, ImagePath = null, Account = null, DisplayName = null, PreshutdownTimeoutMs = null, Sddl = null,
            ServiceType = null, ErrorControl = null, FailureActionCount = null, DelayedAutoStart = null, TriggerCount = null,
            RequiredPrivileges = null, ServiceSidType = null, Description = null,
        };

        IReadOnlyList<string> problems = ServiceCheck.Verify(read, spec);

        Assert.HasCount(14, problems);
        Assert.IsTrue(problems.All(p => p.Contains("could not be read", StringComparison.Ordinal)), string.Join(" | ", problems));
    }
}
