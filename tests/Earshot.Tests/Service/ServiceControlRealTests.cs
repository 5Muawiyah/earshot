using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using Earshot.Boot;
using Earshot.Contracts;
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

    // ---- the strings and settings of a real query, against the system's own tool ----

    // Runs a read-only sc.exe query and returns what it printed.
    private static string Sc(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, "sc.exe " + string.Join(' ', arguments) + " printed: " + output);
        return output;
    }

    private static string ScField(string output, string name)
    {
        Match match = Regex.Match(output, "^\\s*" + Regex.Escape(name) + "\\s*:\\s*(.*?)\\s*$", RegexOptions.Multiline);
        Assert.IsTrue(match.Success, "sc.exe printed no " + name + ": " + output);
        return match.Groups[1].Value;
    }

    // QueryServiceConfigW puts pointers to its strings in the buffer it fills. The strings read back must be the ones the
    // system's own tool prints, so a pointer that was read after the buffer moved (a wrong string, or none) shows here.
    // The garbage collector's move is not something a test can force, so this proves the strings are right when read, not
    // that no move could ever happen: the buffer being native memory is what rules the move out.
    [TestMethod]
    [DataRow("Schedule")]
    [DataRow("DoSvc")]
    public void TheStringsOfARealQueryEqualWhatScQcPrints(string name)
    {
        ServiceQuery read = new WindowsServiceControl().Query(name);
        string qc = Sc("qc", name);

        Assert.AreEqual(ScField(qc, "BINARY_PATH_NAME"), read.ImagePath);
        Assert.AreEqual(ScField(qc, "DISPLAY_NAME"), read.DisplayName);
        Assert.AreEqual(ScField(qc, "SERVICE_START_NAME"), read.Account);
        // sc.exe prints the service type in hexadecimal (20 for a shared process, 10 for its own), the rest in decimal.
        Assert.AreEqual(read.ServiceType, uint.Parse(ScField(qc, "TYPE").Split(' ')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        Assert.AreEqual(read.StartType, uint.Parse(ScField(qc, "START_TYPE").Split(' ')[0], CultureInfo.InvariantCulture));
        Assert.AreEqual(read.ErrorControl, uint.Parse(ScField(qc, "ERROR_CONTROL").Split(' ')[0], CultureInfo.InvariantCulture));
        TestContext?.WriteLine(name + ": " + read.ImagePath + " | " + read.DisplayName + " | " + read.Account);
    }

    // The description is read the same way, through a pointer into the buffer the control manager fills: the text of a real query
    // must be the one the system's own tool prints. Read-only; nothing is changed.
    [TestMethod]
    [DataRow("Schedule")]
    [DataRow("DoSvc")]
    public void TheDescriptionOfARealQueryEqualsWhatScQdescriptionPrints(string name)
    {
        ServiceQuery read = new WindowsServiceControl().Query(name);
        string qdescription = Sc("qdescription", name);

        Assert.IsNotNull(read.Description, "The description was read.");
        Assert.AreEqual(ScField(qdescription, "DESCRIPTION"), read.Description);
    }

    [TestMethod]
    public void TheOptionalSettingsOfARealQueryEqualWhatTheScQueriesPrint()
    {
        ServiceQuery schedule = new WindowsServiceControl().Query(SystemService);

        // The count of failure actions is read independently from the registry value the control manager keeps: sc.exe prints
        // only the actions that do something, and the value's fourth DWORD is the count of all of them.
        string failure = Sc("qfailure", SystemService);
        byte[] blob = (byte[])Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + SystemService, "FailureActions", null)!;
        Assert.AreEqual((uint?)BitConverter.ToUInt32(blob, 12), schedule.FailureActionCount, failure);
        Assert.IsTrue(Regex.Count(failure, "Delay =") <= (int)schedule.FailureActionCount!.Value, failure);
        Assert.IsTrue(string.IsNullOrWhiteSpace(schedule.FailureCommand), "sc.exe prints no command line: " + failure);

        string sid = Sc("qsidtype", SystemService);
        uint expectedSid = ScField(sid, "SERVICE_SID_TYPE").Split(' ')[0] switch { "NONE" => 0u, "UNRESTRICTED" => 1u, "RESTRICTED" => 3u, _ => uint.MaxValue };
        Assert.AreEqual(expectedSid, schedule.ServiceSidType, sid);

        string triggers = Sc("qtriggerinfo", SystemService);
        Assert.AreEqual(Regex.Count(triggers, "^\\s+(START|STOP) SERVICE", RegexOptions.Multiline), (int)schedule.TriggerCount!.Value, triggers);

        string privileges = Sc("qprivs", SystemService);
        Assert.AreEqual(Regex.Count(privileges, "Se\\w+Privilege"), schedule.RequiredPrivileges!.Count, privileges);
        Assert.IsTrue(schedule.RequiredPrivileges.All(p => privileges.Contains(p, StringComparison.Ordinal)));

        Assert.IsFalse(schedule.DelayedAutoStart);
        ServiceQuery delivery = new WindowsServiceControl().Query("DoSvc");
        Assert.AreEqual(Sc("qc", "DoSvc").Contains("(DELAYED)", StringComparison.Ordinal), delivery.DelayedAutoStart, "DoSvc starts delayed.");
        TestContext?.WriteLine("Schedule: " + schedule.FailureActionCount + " failure action(s), " + schedule.TriggerCount + " trigger(s), sid type " +
                               schedule.ServiceSidType + ", " + schedule.RequiredPrivileges.Count + " privilege(s); DoSvc delayed " + delivery.DelayedAutoStart);
        foreach (StepOutcome step in schedule.Steps)
        {
            Assert.IsTrue(step.Ok, step.Step + " " + step.CodeName);
        }
    }

    // A real service that is not Earshot's is never a match for the plan, and the read-back names what differs.
    [TestMethod]
    public void TheReadBackOfARealServiceListsTheSettingsThePlanLeavesUnset()
    {
        ServiceQuery read = new WindowsServiceControl().Query(SystemService);

        IReadOnlyList<string> problems = ServiceCheck.Verify(read, ServicePlan.Spec(@"C:\Program Files\Earshot"));

        Assert.IsTrue(problems.Any(p => p.Contains("failure action", StringComparison.Ordinal)), string.Join(" | ", problems));
        Assert.IsTrue(problems.Any(p => p.Contains("trigger", StringComparison.Ordinal)), string.Join(" | ", problems));
        Assert.IsTrue(problems.Any(p => p.Contains("privilege", StringComparison.Ordinal)), string.Join(" | ", problems));
    }

    // Queries in a loop while another thread forces compacting collections and churns the heap. It cannot make the old
    // race happen on demand, so a pass proves nothing about the old code; it is here so a regression that reads a string
    // after the buffer moved has the chance to show.
    [TestMethod]
    public void QueriesReadTheSameStringsWhileTheCollectorRuns()
    {
        var control = new WindowsServiceControl();
        ServiceQuery expected = control.Query(SystemService);
        using var stop = new CancellationTokenSource();
        Task churn = Task.Run(() =>
        {
            var keep = new List<byte[]>();
            while (!stop.IsCancellationRequested)
            {
                for (int i = 0; i < 64; i++)
                {
                    keep.Add(new byte[512]);
                }

                if (keep.Count > 512)
                {
                    keep.Clear();
                }

                GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: true);
                Thread.Sleep(1);
            }
        });
        try
        {
            for (int i = 0; i < 150; i++)
            {
                ServiceQuery read = control.Query(SystemService);
                Assert.AreEqual(expected.ImagePath, read.ImagePath, "read " + i);
                Assert.AreEqual(expected.DisplayName, read.DisplayName, "read " + i);
                Assert.AreEqual(expected.Account, read.Account, "read " + i);
                Assert.AreEqual(expected.Sddl, read.Sddl, "read " + i);
            }
        }
        finally
        {
            stop.Cancel();
            churn.Wait();
        }
    }

    [TestMethod]
    public void TheStructsThatAreWrittenToTheControlManagerHaveTheirDocumentedSizes()
    {
        Assert.AreEqual(40, Marshal.SizeOf<SERVICE_FAILURE_ACTIONSW>());
        Assert.AreEqual(8, Marshal.SizeOf<SC_ACTION>());
        Assert.AreEqual(24, Marshal.SizeOf<SERVICE_TRIGGER_INFO>());
    }

    // ---- helpers the fakes replace everywhere else, once for real and read-only ----

    [TestMethod]
    public void WaitingForAMissingServiceToStopIsDoneAndNeverToRun()
    {
        var control = new WindowsServiceControl();
        string name = "EarshotTest" + Guid.NewGuid().ToString("N");
        int waits = 0;

        StepOutcome stopped = control.WaitForState(name, AdvApi32.SERVICE_STOPPED, TimeSpan.FromSeconds(1), _ => { waits++; return true; });
        StepOutcome running = control.WaitForState(name, AdvApi32.SERVICE_RUNNING, TimeSpan.FromMilliseconds(500), _ => { waits++; return true; });

        Assert.IsTrue(stopped.Ok, stopped.Detail);
        StringAssert.Contains(stopped.Detail, "is not registered");
        Assert.IsFalse(running.Ok);
        Assert.AreEqual((int)AdvApi32.ERROR_SERVICE_REQUEST_TIMEOUT, running.Code);
        Assert.AreEqual(2, waits, "Half a second is two polls of a quarter of a second.");
        TestContext?.WriteLine("stopped: " + stopped.Detail + " | running: " + running.Detail);
    }

    [TestMethod]
    public void RemovingAServiceThatIsNotThereChangesNothingAndSaysSo()
    {
        var steps = new List<StepOutcome>();
        string name = "EarshotTest" + Guid.NewGuid().ToString("N");

        bool done = ServiceRemoval.Remove(new WindowsServiceControl(), _ => false, steps, name);

        Assert.IsTrue(done);
        Assert.HasCount(1, steps);
        Assert.AreEqual(ServiceSteps.Existing, steps[0].Step);
        Assert.AreEqual("No service.", steps[0].Detail);
        TestContext?.WriteLine(steps[0].Step + ": " + steps[0].Detail);
    }

    [TestMethod]
    public void WaitingForAProcessThatIsNotTheServiceReturnsAtOnce()
    {
        var control = new WindowsServiceControl();

        StepOutcome none = control.WaitForProcessExit(0, TimeSpan.FromSeconds(1), _ => false);
        StepOutcome gone = control.WaitForProcessExit(unchecked((uint)0x7FFFFFF0), TimeSpan.FromSeconds(1), _ => false);
        // This process is running but is not named Earshot, so its id is not taken for the service's.
        StepOutcome other = control.WaitForProcessExit((uint)Environment.ProcessId, TimeSpan.FromSeconds(1), _ => false);

        Assert.IsTrue(none.Ok, none.Detail);
        Assert.IsTrue(gone.Ok, gone.Detail);
        Assert.IsTrue(other.Ok, other.Detail);
        TestContext?.WriteLine(gone.Detail + " | " + other.Detail);
    }

    // The setup verifies the read-back of the optional settings, so a service that has none of them (no failure action, no
    // trigger, no privilege list, as a fresh registration has) must read as a clean zero, not as a read that failed. This reads
    // every service on the machine, as a standard user, and reports what each optional read did.
    [TestMethod]
    public void EveryServiceOnThisMachineReadsAllItsOptionalSettingsOrSaysWhyNot()
    {
        string[] names = Regex.Matches(Sc("query", "type=", "service", "state=", "all"), "^SERVICE_NAME:\\s*(\\S+)", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.IsTrue(names.Length > 20, "sc.exe listed " + names.Length + " services.");
        var control = new WindowsServiceControl();
        var unread = new SortedDictionary<string, SortedSet<uint>>(StringComparer.Ordinal);
        int read = 0, withNone = 0, unknown = 0;
        foreach (string name in names)
        {
            ServiceQuery query = control.Query(name);
            if (query.Presence != ServicePresence.Present)
            {
                unknown++;
                continue;
            }

            read++;
            if (query.FailureActionCount == 0 && query.TriggerCount == 0 && query.RequiredPrivileges is { Count: 0 })
            {
                withNone++;
            }

            foreach (StepOutcome step in query.Steps.Where(s => !s.Ok))
            {
                if (!unread.TryGetValue(step.Step, out SortedSet<uint>? codes))
                {
                    unread[step.Step] = codes = [];
                }

                codes.Add(unchecked((uint)step.Code));
            }
        }

        string summary = read + " services read, " + withNone + " with no failure action, trigger or privilege list, " + unknown +
                         " not readable; reads that failed: " +
                         (unread.Count == 0 ? "none" : string.Join("; ", unread.Select(k => k.Key + " codes " + string.Join(",", k.Value))));
        TestContext?.WriteLine(summary);
        Assert.IsTrue(withNone > 0, "No service without those settings was read, so the clean zero was not exercised. " + summary);
        foreach (string level in new[]
                 {
                     ServiceSteps.FailureActions, ServiceSteps.DelayedStart, ServiceSteps.Triggers, ServiceSteps.SidType, "service-privileges",
                     "service-preshutdown", "service-config", "service-status", "service-acl",
                 })
        {
            Assert.IsFalse(unread.ContainsKey(level), level + " failed to read for some service. " + summary);
        }
    }
}
