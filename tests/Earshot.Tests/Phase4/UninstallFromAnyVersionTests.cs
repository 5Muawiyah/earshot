using System.Security;
using Earshot.AudioProtection;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Service;
using Earshot.Tests.Phase6;
using Earshot.Tests.Service;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// Uninstall against what each earlier version of Earshot may have left on a PC: a fake node tree, task store and
// service control, temp folders, and no device. Nothing here touches a real node, task, service or folder.
[TestClass]
public sealed class UninstallFromAnyVersionTests
{
    private const string SecondAddress = "0A1B2C3D4E9D";

    private static readonly Guid Handsfree = ProtectedServices.Handsfree;

    private static readonly string[] DeviceRecordOnly = ["device.json"];

    // The two pairs by address order: the table's own, then the spare one.
    private static readonly string[] BothPairsByName = ["Jonathan\u2019s AirPods Pro", "Spare AirPods"];

    private static void AssertOnly(string expected, IReadOnlyList<string> actual)
    {
        Assert.HasCount(1, actual, string.Join(" | ", actual));
        Assert.AreEqual(expected, actual[0]);
    }

    // A task store with a root folder as well, which the shared fake does not model: the old test task sits in the root.
    private sealed class RootAwareTasks(FakeTaskRegistrar inner) : ITaskRegistrar
    {
        public FakeTaskRegistrar Inner { get; } = inner;

        public Dictionary<string, string> RootTasks { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int ReadFolderSddl(string folderPath, out string? sddl) => Inner.ReadFolderSddl(folderPath, out sddl);

        public int ListTasks(string folderPath, out IReadOnlyList<string> names) => Inner.ListTasks(folderPath, out names);

        public int DeleteTask(string folderPath, string name)
        {
            if (folderPath == "\\")
            {
                return RootTasks.Remove("\\" + name) ? 0 : FakeTaskRegistrar.NotFound;
            }

            return Inner.DeleteTask(folderPath, name);
        }

        public int DeleteFolder(string parentPath, string name) => Inner.DeleteFolder(parentPath, name);

        public int CreateFolder(string parentPath, string name, string sddl) => Inner.CreateFolder(parentPath, name, sddl);

        public int Register(string folderPath, TaskSpec spec, IList<StepOutcome> steps) => Inner.Register(folderPath, spec, steps);

        public int ReadTask(string taskPath, out string? sddl, out string? xml)
        {
            if (RootTasks.TryGetValue(taskPath, out string? stored))
            {
                sddl = "";
                xml = stored;
                return 0;
            }

            return Inner.ReadTask(taskPath, out sddl, out xml);
        }
    }

    private static string RootTaskXml(string command) =>
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
        "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(command) + "</Command><Arguments>-File gate.ps1</Arguments></Exec></Actions></Task>";

    private sealed class Harness : IDisposable
    {
        private readonly TempFolder _temp = new();

        public Harness(FakeNodeApi? nodes = null)
        {
            Nodes = nodes ?? RecordedNodes.Table();
            Source = Path.Combine(_temp.Path, "unzip", "Earshot");
            Install = Path.Combine(_temp.Path, "ProgramFiles", "Earshot");
            Machine = Path.Combine(_temp.Path, "ProgramData", "Earshot");
            Directory.CreateDirectory(Source);
            Tasks = new RootAwareTasks(new FakeTaskRegistrar());
        }

        public string Source { get; }

        public string Install { get; }

        public string Machine { get; }

        public FakeFolderSecurity Folders { get; } = new();

        public RootAwareTasks Tasks { get; }

        public FakeNodeApi Nodes { get; }

        public RebootDeleteRecorder Reboot { get; } = new();

        public CapturingLog Log { get; } = new();

        public FakeBluetoothServices Bluetooth { get; } = FakeBluetoothServices.AirPods();

        public FakeServiceControl? Service { get; set; }

        public UninstallOptions Options { get; set; } = new();

        public GateStore Store => new(Machine);

        public InstallLayout Layout => new(Source, Install, Machine);

        // The machine folder as an install leaves it: config.json, and device.json unless asked otherwise, and
        // protection.json when services are named.
        public void MachineRecords(bool device = true, bool config = true, params Guid[] servicesOff)
        {
            Directory.CreateDirectory(Machine);
            if (config)
            {
                Assert.IsTrue(Store.WriteConfig(new GateConfig()).Ok);
            }

            if (device)
            {
                Assert.IsTrue(Store.WriteDevice(RecordedNodes.AirPods()).Ok);
            }

            if (servicesOff.Length > 0)
            {
                Assert.IsTrue(Store.WriteProtection(new ProtectionRecord { DisabledServices = servicesOff.ToList() }).Ok);
                foreach (Guid service in servicesOff)
                {
                    Bluetooth[RecordedNodes.AirPodsAddress].TurnOffOutside(service);
                }
            }
        }

        public void InstallFolder()
        {
            Directory.CreateDirectory(Install);
            File.WriteAllText(Path.Combine(Install, TaskPlan.ExecutableName), "program");
        }

        public void EarshotTasks(params string[] names)
        {
            Tasks.Inner.FolderSddl = "D:";
            foreach (string name in names)
            {
                Tasks.Inner.Tasks[TaskPlan.FolderPath + "\\" + name] = ("", "<Task/>");
            }
        }

        public UninstallRun Run(bool withBluetooth = true) =>
            new UninstallActions(Layout, Folders, Nodes, Tasks, Reboot, Log, runLock: null, withBluetooth ? Bluetooth : null, Service)
            {
                Options = Options,
            }.RunWithReport();

        public void Dispose() => _temp.Dispose();
    }

    private static string Fail(InstallResult result) =>
        string.Join(Environment.NewLine, result.Steps.Where(s => !s.Ok).Select(GateActions.Describe));

    private static IEnumerable<string> ServiceNodes() => RecordedNodes.AirPodsTargets.Where(id => id != RecordedNodes.AirPodsDeviceNode);

    private static void Disable(FakeNodeApi nodes, IEnumerable<string> ids)
    {
        foreach (string id in ids)
        {
            nodes[id].MarkDisabled(persistent: true);
        }
    }

    // Out of the tree with the persistent flag still set: what the audio nodes do while their device node is disabled.
    private static void FlagAbsent(FakeNodeApi nodes, IEnumerable<string> ids)
    {
        foreach (string id in ids)
        {
            FakeNode node = nodes[id];
            node.Present = false;
            node.ConfigFlags |= CfgMgr32.CONFIGFLAG_DISABLED;
        }
    }

    private static bool IsRestored(FakeNode node) =>
        (node.ConfigFlags & CfgMgr32.CONFIGFLAG_DISABLED) == 0 && (!node.Present || !node.IsDisabled);

    private static string SecondOf(string id) => id.Replace(RecordedNodes.AirPodsAddress, SecondAddress, StringComparison.OrdinalIgnoreCase);

    private static FakeNodeApi TableWithSecondPair()
    {
        List<FakeNode> nodes = RecordedNodes.Table().Nodes.ToList();
        nodes.AddRange(RecordedNodes.AirPodsTargets.Select(id => new FakeNode(
            SecondOf(id), RecordedNodes.HeadphonesContainer, id == RecordedNodes.AirPodsDeviceNode ? "Spare AirPods" : null)));
        return new FakeNodeApi(nodes);
    }

    private static void AssertPlain(UninstallReport report)
    {
        foreach (string line in report.Lines)
        {
            foreach (string forbidden in new[]
                     {
                         "BTHENUM", RecordedNodes.AirPodsAddress, SecondAddress, RecordedNodes.AirPodsContainer.ToString("D"),
                         RecordedNodes.HeadphonesContainer.ToString("D"), "CR_", "0x",
                     })
            {
                Assert.DoesNotContain(forbidden, line, StringComparison.OrdinalIgnoreCase, "The report shows no id or code: " + line);
            }
        }
    }

    // ---- one fixture per row of the version table ----

    // 1.0 and 1.1: nodes disabled and flagged where they stand, services off, three tasks, no service.
    // 1.2 to 1.4: the same, with the hand-back service, and the audio nodes out of the tree with the flag still set;
    // 1.3 and 1.4 also register a one-shot task that starts the tray.
    [TestMethod]
    [DataRow("1.0.0", false, false, false)]
    [DataRow("1.1.0", false, false, false)]
    [DataRow("1.2.0", true, true, false)]
    [DataRow("1.3.0", true, true, true)]
    [DataRow("1.4.0", true, true, true)]
    public void EveryEarlierVersionsLeftoversAreRestoredAndRemoved(string version, bool service, bool absentServiceNodes, bool startTray)
    {
        using var h = new Harness();
        h.MachineRecords(servicesOff: Handsfree);
        h.InstallFolder();
        h.EarshotTasks(startTray ? ["Gate", "Protect", "BootBlock", TaskPlan.TrayStartTaskName] : ["Gate", "Protect", "BootBlock"]);
        if (service)
        {
            h.Service = new FakeServiceControl();
            h.Service.Install(AdvApi32.SERVICE_RUNNING, ServicePlan.Spec(h.Install));
        }

        Disable(h.Nodes, RecordedNodes.AirPodsTargets);
        if (absentServiceNodes)
        {
            FlagAbsent(h.Nodes, ServiceNodes());
        }

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Success, run.Result.Outcome, version + ": " + Fail(run.Result));
        Assert.IsTrue(RecordedNodes.AirPodsTargets.All(id => IsRestored(h.Nodes[id])), version + ": every AirPods node is back.");
        Assert.IsFalse(h.Nodes[RecordedNodes.AirPodsDeviceNode].IsDisabled);
        Assert.Contains(new BluetoothCall("set", Handsfree, true), h.Bluetooth.SetCalls.ToList(), version + ": Hands-Free is turned back on.");
        Assert.IsEmpty(h.Tasks.Inner.Tasks, version + ": every task in the Earshot folder is gone.");
        Assert.IsFalse(Directory.Exists(h.Machine));
        Assert.IsFalse(Directory.Exists(h.Install));
        if (service)
        {
            Assert.IsFalse(h.Service!.Exists, version + ": the hand-back service is deleted.");
        }

        Assert.IsEmpty(run.Report.NotPutBack, version);
        Assert.IsNotEmpty(run.Report.PutBack, version);
        AssertPlain(run.Report);
    }

    // The flag on a node that has left the tree can only be cleared through SetupAPI; enabling needs a node that is there.
    [TestMethod]
    public void AbsentNodesThatStillCarryTheDisabledFlagEndUnflagged()
    {
        using var h = new Harness();
        h.MachineRecords();
        Disable(h.Nodes, [RecordedNodes.AirPodsDeviceNode]);
        FlagAbsent(h.Nodes, ServiceNodes());

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Success, run.Result.Outcome, Fail(run.Result));
        foreach (string id in ServiceNodes())
        {
            FakeNode node = h.Nodes[id];
            Assert.IsFalse(node.Present, id + " is still away.");
            Assert.AreEqual(0u, node.ConfigFlags & CfgMgr32.CONFIGFLAG_DISABLED, id + " must not keep the flag.");
        }

        Assert.HasCount(ServiceNodes().Count(), h.Nodes.Calls.Where(c => c.Kind == "clear-flag"));
        Assert.IsEmpty(h.Nodes.Calls.Where(c => c.Kind is "enable-phantom" or "disable" or "disable-phantom" or "set-flag"));
        Assert.IsFalse(h.Nodes[RecordedNodes.AirPodsDeviceNode].IsDisabled);
    }

    [TestMethod]
    public void AnAbsentNodeWithoutTheFlagIsLeftAlone()
    {
        using var h = new Harness();
        h.MachineRecords();
        foreach (string id in ServiceNodes())
        {
            h.Nodes[id].Present = false;
        }

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Success, run.Result.Outcome, Fail(run.Result));
        Assert.IsEmpty(h.Nodes.Calls, "Nothing to clear, so no write.");
    }

    // ---- every failure keeps its raw code ----

    [TestMethod]
    public void AFlagThatCannotBeClearedKeepsItsWin32CodeAndTheRecordsAndIsNamedWithoutIds()
    {
        using var h = new Harness();
        h.MachineRecords();
        Disable(h.Nodes, [RecordedNodes.AirPodsDeviceNode]);
        FlagAbsent(h.Nodes, ServiceNodes());
        string stuck = ServiceNodes().First();
        h.Nodes[stuck].ClearFlagError = BluetoothApis.ERROR_ACCESS_DENIED;

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Partial, run.Result.Outcome);
        StepOutcome step = run.Result.Steps.Single(s => s.Step == "node-flag-clear:" + stuck);
        Assert.IsFalse(step.Ok);
        Assert.AreEqual((int)BluetoothApis.ERROR_ACCESS_DENIED, step.Code);
        Assert.AreEqual("ERROR_ACCESS_DENIED", step.CodeName);
        Assert.IsTrue(Directory.Exists(h.Machine), "The records stay for another run.");
        CollectionAssert.AreEquivalent(DeviceRecordOnly, Directory.GetFiles(h.Machine).Select(Path.GetFileName).ToArray());
        AssertOnly("Part of the AirPods could not be turned back on.", run.Report.NotPutBack);
        AssertPlain(run.Report);
    }

    [TestMethod]
    public void AFlagWriteThatDoesNotShowWhenReadBackIsAFailure()
    {
        using var h = new Harness();
        h.MachineRecords();
        FlagAbsent(h.Nodes, ServiceNodes());
        string stuck = ServiceNodes().First();
        h.Nodes[stuck].FlagWriteDoesNotStick = true;

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Partial, run.Result.Outcome);
        Assert.IsFalse(run.Result.Steps.Single(s => s.Step == "node-flag-clear:" + stuck).Ok);
        Assert.HasCount(1, run.Report.NotPutBack);
    }

    [TestMethod]
    public void AFlagThatCannotBeReadKeepsItsConfigRetCode()
    {
        using var h = new Harness();
        h.MachineRecords();
        FlagAbsent(h.Nodes, ServiceNodes());
        string unread = ServiceNodes().First();
        h.Nodes[unread].ConfigFlagsReadResult = CfgMgr32.CR_ACCESS_DENIED;

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Partial, run.Result.Outcome);
        StepOutcome step = run.Result.Steps.First(s => s.Step == "cm-configflags:" + unread && !s.Ok);
        Assert.AreEqual("CR_ACCESS_DENIED", step.CodeName);
        Assert.IsNotEmpty(run.Report.NotPutBack);
    }

    [TestMethod]
    public void ADeviceNodeThatWillNotEnableKeepsItsCodeAndTheReportNamesTheAirPods()
    {
        using var h = new Harness();
        h.MachineRecords();
        Disable(h.Nodes, [RecordedNodes.AirPodsDeviceNode]);
        h.Nodes[RecordedNodes.AirPodsDeviceNode].EnableResult = CfgMgr32.CR_ACCESS_DENIED;

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Partial, run.Result.Outcome);
        Assert.AreEqual("CR_ACCESS_DENIED", run.Result.Steps.Single(s => s.Step == "cm-enable:" + RecordedNodes.AirPodsDeviceNode).CodeName);
        AssertOnly("The AirPods could not be turned back on.", run.Report.NotPutBack);
        AssertPlain(run.Report);
    }

    [TestMethod]
    public void AServiceCallThatFailsKeepsItsWin32CodeAndTheRecord()
    {
        using var h = new Harness();
        h.MachineRecords(servicesOff: Handsfree);
        h.Bluetooth.SetResult = (_, _) => BluetoothApis.ERROR_ACCESS_DENIED;

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Partial, run.Result.Outcome);
        Assert.AreEqual("ERROR_ACCESS_DENIED", run.Result.Steps.Single(s => s.Step == "bt-service-enable:Handsfree").CodeName);
        Assert.IsTrue(File.Exists(h.Store.ProtectionFile), "protection.json is the only record of what to turn back on.");
        AssertOnly("Call audio could not be turned back on.", run.Report.NotPutBack);
    }

    // ---- services of a device that is no longer paired ----

    [TestMethod]
    public void AServiceOfAnUnpairedDeviceIsNothingToRestoreAndNotAFailure()
    {
        using var h = new Harness();
        h.MachineRecords(servicesOff: Handsfree);
        h.Bluetooth.Remove(RecordedNodes.AirPodsAddress);
        Disable(h.Nodes, RecordedNodes.AirPodsTargets);
        FlagAbsent(h.Nodes, ServiceNodes());

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Success, run.Result.Outcome, Fail(run.Result));
        StepOutcome gone = run.Result.Steps.Single(s => s.Step == "protection-restore");
        Assert.IsTrue(gone.Ok);
        Assert.AreEqual((int)BluetoothApis.ERROR_NOT_FOUND, gone.Code);
        Assert.IsEmpty(h.Bluetooth.SetCalls, "No call is made for a device that is not paired.");
        Assert.IsFalse(Directory.Exists(h.Machine), "Nothing is kept for a restore that cannot happen.");
        Assert.IsEmpty(run.Report.NotPutBack);
        Assert.IsTrue(run.Report.Notes.Any(n => n.Contains("no longer paired", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AServiceCallThatAnswersNotFoundIsRecordedNotFailed()
    {
        using var h = new Harness();
        h.MachineRecords(servicesOff: Handsfree);
        h.Bluetooth.SetResult = (_, _) => BluetoothApis.ERROR_NOT_FOUND;

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Success, run.Result.Outcome, Fail(run.Result));
        StepOutcome call = run.Result.Steps.Single(s => s.Step == "bt-service-enable:Handsfree");
        Assert.AreEqual((int)BluetoothApis.ERROR_NOT_FOUND, call.Code, "The raw code is still in the step.");
        Assert.IsTrue(call.Ok);
        Assert.IsFalse(Directory.Exists(h.Machine));
    }

    // ---- no device.json: the identity rule, and only with a footprint ----

    [TestMethod]
    public void WithoutDeviceJsonTheIdentityRuleRestoresTheAirPodsAndNeverTouchesAnythingElse()
    {
        using var h = new Harness(RecordedNodes.TableWithHeadphones());
        h.MachineRecords(device: false);
        Disable(h.Nodes, RecordedNodes.AirPodsTargets);
        FlagAbsent(h.Nodes, ServiceNodes());

        // The owner's other devices are disabled and flagged too, and must stay that way.
        string[] others = [.. RecordedNodes.IPhoneNodes, .. RecordedNodes.HeadphonesNodes, .. RecordedNodes.AirPodsNonTargets];
        foreach (string id in others)
        {
            h.Nodes[id].MarkDisabled(persistent: true);
        }

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Success, run.Result.Outcome, Fail(run.Result));
        Assert.IsTrue(RecordedNodes.AirPodsTargets.All(id => IsRestored(h.Nodes[id])));
        Assert.IsTrue(others.All(id => h.Nodes[id].IsDisabled && (h.Nodes[id].ConfigFlags & CfgMgr32.CONFIGFLAG_DISABLED) != 0),
            "The iPhone, the headphones and every node outside the AirPods are as they were.");
        Assert.IsEmpty(h.Nodes.Calls.Where(c => others.Contains(c.InstanceId, StringComparer.OrdinalIgnoreCase)));
        Assert.IsEmpty(h.Nodes.Calls.Where(c => c.Kind is "disable" or "disable-phantom" or "set-flag"), "Discovered nodes are only ever restored.");
        AssertPlain(run.Report);
    }

    [TestMethod]
    public void WithoutAnyFootprintNothingIsRestoredAndTheReportSaysSo()
    {
        using var h = new Harness();
        Disable(h.Nodes, RecordedNodes.AirPodsTargets);
        FlagAbsent(h.Nodes, ServiceNodes());

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Success, run.Result.Outcome, Fail(run.Result));
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.IsTrue(h.Nodes[RecordedNodes.AirPodsDeviceNode].IsDisabled);
        Assert.IsEmpty(run.Report.PutBack);
        Assert.IsEmpty(run.Report.NotPutBack);
        AssertOnly("Nothing shows that Earshot was set up on this PC, so no AirPods setting was changed.", run.Report.Notes);
    }

    [TestMethod]
    [DataRow("config.json")]
    [DataRow("protection.json")]
    [DataRow("status-file")]
    [DataRow("task")]
    [DataRow("service")]
    [DataRow("install-folder")]
    [DataRow("settings-pin")]
    [DataRow("prototype-task-run-by-earshot")]
    public void EachKindOfFootprintAllowsTheRestoreOfASingleAirPods(string evidence)
    {
        using var h = new Harness();
        Disable(h.Nodes, RecordedNodes.AirPodsTargets);
        switch (evidence)
        {
            case "config.json":
                h.MachineRecords(device: false);
                break;
            case "protection.json":
                Directory.CreateDirectory(h.Machine);
                Assert.IsTrue(h.Store.WriteProtection(new ProtectionRecord()).Ok);
                break;
            case "status-file":
                Directory.CreateDirectory(h.Machine);
                File.WriteAllText(Path.Combine(h.Machine, "status-" + new string('a', 32) + ".json"), "{}");
                break;
            case "task":
                h.EarshotTasks("Gate");
                break;
            case "service":
                h.Service = new FakeServiceControl();
                h.Service.Install(AdvApi32.SERVICE_STOPPED, ServicePlan.Spec(h.Install));
                break;
            case "install-folder":
                h.InstallFolder();
                break;
            case "settings-pin":
                h.Options = new UninstallOptions { SettingsPinAirPods = true };
                break;
            default:
                h.Tasks.RootTasks["\\AirPodsGate"] = RootTaskXml(Path.Combine(h.Install, TaskPlan.ExecutableName));
                break;
        }

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Success, run.Result.Outcome, evidence + ": " + Fail(run.Result));
        Assert.IsTrue(RecordedNodes.AirPodsTargets.All(id => IsRestored(h.Nodes[id])), evidence);
    }

    // ---- several AirPods and no record of which one Earshot blocked ----

    [TestMethod]
    public void SeveralAirPodsAndNoRecordsRestoreNoneAndListTheirNamesWithoutIds()
    {
        using var h = new Harness(TableWithSecondPair());
        h.MachineRecords(device: false);
        Disable(h.Nodes, RecordedNodes.AirPodsTargets);
        Disable(h.Nodes, RecordedNodes.AirPodsTargets.Select(SecondOf));

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Partial, run.Result.Outcome, "Something is left for the owner to choose.");
        Assert.IsEmpty(h.Nodes.Calls, "Neither pair is changed without a choice.");
        Assert.IsTrue(run.Report.NeedsChoice);
        CollectionAssert.AreEqual(BothPairsByName, run.Report.ChooseFrom.ToArray());
        AssertPlain(run.Report);
        Assert.IsTrue(h.Nodes[RecordedNodes.AirPodsDeviceNode].IsDisabled);
    }

    [TestMethod]
    public void AChosenAddressRestoresOnlyThatPair()
    {
        using var h = new Harness(TableWithSecondPair());
        h.MachineRecords(device: false);
        Disable(h.Nodes, RecordedNodes.AirPodsTargets);
        Disable(h.Nodes, RecordedNodes.AirPodsTargets.Select(SecondOf));
        h.Options = new UninstallOptions { ChosenAddress = SecondAddress };

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Success, run.Result.Outcome, Fail(run.Result));
        Assert.IsTrue(RecordedNodes.AirPodsTargets.Select(SecondOf).All(id => IsRestored(h.Nodes[id])), "The chosen pair is back.");
        Assert.IsTrue(RecordedNodes.AirPodsTargets.All(id => h.Nodes[id].IsDisabled), "The other pair is as it was.");
        Assert.IsFalse(run.Report.NeedsChoice);
    }

    [TestMethod]
    public void AChosenAddressThatIsNotAmongTheAirPodsFoundRestoresNone()
    {
        using var h = new Harness(TableWithSecondPair());
        h.MachineRecords(device: false);
        Disable(h.Nodes, RecordedNodes.AirPodsTargets);
        Disable(h.Nodes, RecordedNodes.AirPodsTargets.Select(SecondOf));
        h.Options = new UninstallOptions { ChosenAddress = RecordedNodes.HeadphonesAddress };

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Partial, run.Result.Outcome);
        Assert.IsEmpty(h.Nodes.Calls);
        Assert.IsTrue(run.Report.NeedsChoice);
    }

    [TestMethod]
    public void ARecordedDeviceNeedsNoChoiceAndLeavesTheOtherPairAlone()
    {
        using var h = new Harness(TableWithSecondPair());
        h.MachineRecords();
        Disable(h.Nodes, RecordedNodes.AirPodsTargets);
        Disable(h.Nodes, RecordedNodes.AirPodsTargets.Select(SecondOf));

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Success, run.Result.Outcome, Fail(run.Result));
        Assert.IsTrue(RecordedNodes.AirPodsTargets.All(id => IsRestored(h.Nodes[id])));
        Assert.IsTrue(RecordedNodes.AirPodsTargets.Select(SecondOf).All(id => h.Nodes[id].IsDisabled));
    }

    // ---- the old test task ----

    [TestMethod]
    public void TheOldGateTaskIsRemovedOnlyWhenItRunsExactlyTheInstalledProgram()
    {
        using var h = new Harness();
        h.InstallFolder();
        h.Tasks.RootTasks["\\AirPodsGate"] = RootTaskXml("\"" + Path.Combine(h.Install, TaskPlan.ExecutableName) + "\"");

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Success, run.Result.Outcome, Fail(run.Result));
        Assert.IsEmpty(h.Tasks.RootTasks);
        Assert.IsTrue(run.Result.Steps.Any(s => s.Step == "task-delete:AirPodsGate" && s.Ok));
    }

    [TestMethod]
    [DataRow(@"C:\Users\someone\source\gate\Run-Gate.ps1")]
    [DataRow(@"C:\Elsewhere\Earshot\Earshot.exe")]
    public void TheOldGateTaskIsLeftAndReportedWhenItRunsAnythingElse(string command)
    {
        using var h = new Harness();
        h.InstallFolder();
        h.Tasks.RootTasks["\\AirPodsGate"] = RootTaskXml(command);

        UninstallRun run = h.Run();

        Assert.AreEqual(GateExitCode.Success, run.Result.Outcome, Fail(run.Result));
        Assert.HasCount(1, h.Tasks.RootTasks, "Someone else's task is not Earshot's to delete.");
        Assert.IsTrue(run.Report.Notes.Any(n => n.Contains("AirPodsGate", StringComparison.Ordinal) && n.Contains("left", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AnOldGateTaskThatIsNotEarshotsIsNoFootprint()
    {
        using var h = new Harness();
        Disable(h.Nodes, RecordedNodes.AirPodsTargets);
        h.Tasks.RootTasks["\\AirPodsGate"] = RootTaskXml(@"C:\Users\someone\source\gate\Run-Gate.ps1");

        UninstallRun run = h.Run();

        Assert.IsEmpty(h.Nodes.Calls);
        Assert.IsTrue(h.Nodes[RecordedNodes.AirPodsDeviceNode].IsDisabled);
        Assert.IsTrue(run.Report.Notes.Any(n => n.StartsWith("Nothing shows", StringComparison.Ordinal)));
    }

    // ---- what stays as before ----

    [TestMethod]
    public void RunReturnsTheSameResultAsRunWithReport()
    {
        using var h = new Harness();
        h.MachineRecords();
        Disable(h.Nodes, RecordedNodes.AirPodsTargets);
        var actions = new UninstallActions(h.Layout, h.Folders, h.Nodes, h.Tasks, h.Reboot, h.Log);

        InstallResult result = actions.Run();

        Assert.AreEqual(GateExitCode.Success, result.Outcome, Fail(result));
        Assert.IsTrue(RecordedNodes.AirPodsTargets.All(id => IsRestored(h.Nodes[id])));
    }
}
