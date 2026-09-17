using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;
using Earshot.Tests.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

[TestClass]
public sealed class BootProbeTests
{
    private const string InstallFolder = @"C:\Program Files\Earshot";

    public TestContext TestContext { get; set; } = null!;

    private static ServiceRegistry NoServices() => throw new AssertFailedException("Writing a report needs no services.");

    private static EarshotSettings Pinned() => new()
    {
        PinnedAddress = RecordedNodes.AirPodsAddress,
        PinnedContainerId = RecordedNodes.AirPodsContainer,
    };

    [TestMethod]
    public void TheNodeProbeListsTheWholeContainerAndMarksTheTargets()
    {
        using var temp = new TempFolder();
        FakeNodeApi table = RecordedNodes.Table();
        table[RecordedNodes.AirPodsTargets[2]].MarkDisabled(persistent: true);

        Program.NodesProbeReport report = Program.ReadNodesProbe(table, Pinned(), new GateStore(temp.Path));

        Assert.AreEqual("settings", report.UsedFrom);
        Assert.IsTrue(report.Listed);
        Assert.HasCount(13, report.Rows);
        CollectionAssert.AreEquivalent(RecordedNodes.AirPodsTargets, report.Rows.Where(r => r.IsTarget).Select(r => r.InstanceId).ToArray());
        Assert.AreEqual(BlockState.Mixed, report.NodeState);
        Assert.AreEqual(1u, report.Rows.Single(r => r.InstanceId == RecordedNodes.AirPodsTargets[2]).ConfigFlags);
        Assert.IsTrue(report.Rows.Where(r => r.InstanceId.StartsWith(@"SWD\", StringComparison.Ordinal)).All(r => !r.Present && r.DevNodeStatus is null));
        Assert.IsEmpty(table.Calls, "The probe changes nothing.");
    }

    [TestMethod]
    public void TheNodeProbePrefersTheGateIdentity()
    {
        using var temp = new TempFolder();
        var store = new GateStore(temp.Path);
        Assert.IsTrue(store.WriteDevice(RecordedNodes.IPhone()).Ok);

        Program.NodesProbeReport report = Program.ReadNodesProbe(RecordedNodes.Table(), Pinned(), store);

        Assert.AreEqual("device.json", report.UsedFrom);
        Assert.AreEqual(RecordedNodes.IPhoneContainer, report.UsedContainer);
        Assert.HasCount(13, report.Rows.Where(r => r.IsTarget));
    }

    [TestMethod]
    public void TheNodeProbeWithNothingPinnedReadsNothing()
    {
        using var temp = new TempFolder();
        FakeNodeApi table = RecordedNodes.Table();
        table.ListResult = CfgMgr32.CR_FAILURE;

        Program.NodesProbeReport report = Program.ReadNodesProbe(table, new EarshotSettings(), new GateStore(temp.Path));
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        Program.WriteNodesProbe(new ProbeContext("nodes", text, json: false, NoServices), report);

        Assert.IsNull(report.UsedContainer);
        StringAssert.Contains(text.ToString(), "No valid device is pinned");
    }

    [TestMethod]
    public void TheNodeProbeWritesOneJsonObjectAndReadableText()
    {
        using var temp = new TempFolder();
        Program.NodesProbeReport report = Program.ReadNodesProbe(RecordedNodes.Table(), Pinned(), new GateStore(temp.Path));
        using var json = new StringWriter(CultureInfo.InvariantCulture);
        using var text = new StringWriter(CultureInfo.InvariantCulture);

        Program.WriteNodesProbe(new ProbeContext("nodes", json, json: true, NoServices), report);
        Program.WriteNodesProbe(new ProbeContext("nodes", text, json: false, NoServices), report);

        using JsonDocument doc = JsonDocument.Parse(json.ToString());
        Assert.AreEqual(9, doc.RootElement.GetProperty("targets").GetInt32());
        Assert.AreEqual(13, doc.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.AreEqual("5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13", doc.RootElement.GetProperty("container").GetString());
        StringAssert.Contains(text.ToString(), "disable targets: 9");
        StringAssert.Contains(text.ToString(), "[target] " + RecordedNodes.AirPodsDeviceNode);
        StringAssert.Contains(text.ToString(), "Target node state: Allowed");
    }

    private static int NoFolder(string path, out string? sddl)
    {
        sddl = null;
        return FakeTaskRegistrar.NotFound;
    }

    [TestMethod]
    public void TheTaskProbeReportsAbsentTasks()
    {
        var tasks = new FakeScheduledTasks();

        Program.TaskProbeReport report = Program.ReadTaskProbe(tasks, NoFolder, InstallFolder, TestUsers.Sid);
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        Program.WriteTaskProbe(new ProbeContext("task", text, json: false, NoServices), report);

        Assert.IsFalse(report.SetUp);
        Assert.IsTrue(report.Tasks.All(t => t.OpenResult == FakeTaskRegistrar.NotFound));
        StringAssert.Contains(text.ToString(), @"\Earshot\Gate: absent (ERROR_FILE_NOT_FOUND)");
        StringAssert.Contains(text.ToString(), "Set up: no");
    }

    [TestMethod]
    public void TheTaskProbeRunsTheTrayChecks()
    {
        var tasks = new FakeScheduledTasks();
        tasks.InstallAll(InstallFolder);
        TaskReadback boot = tasks.Tasks[@"\Earshot\BootBlock"];
        tasks.Tasks[@"\Earshot\BootBlock"] = boot with { Sddl = boot.Sddl + "(A;;FW;;;AU)" };
        string folderSddl = Sddl.TaskFolder(TestUsers.Sid);
        int Folder(string path, out string? sddl)
        {
            sddl = folderSddl;
            return 0;
        }

        Program.TaskProbeReport report = Program.ReadTaskProbe(tasks, Folder, InstallFolder, TestUsers.Sid);
        using var json = new StringWriter(CultureInfo.InvariantCulture);
        Program.WriteTaskProbe(new ProbeContext("task", json, json: true, NoServices), report);

        Assert.IsFalse(report.SetUp);
        Assert.IsEmpty(report.FolderProblems);
        using JsonDocument doc = JsonDocument.Parse(json.ToString());
        JsonElement[] rows = doc.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
        Assert.IsTrue(rows[0].GetProperty("trayMayRun").GetBoolean());
        Assert.AreEqual("0x001200A9", rows[0].GetProperty("userMask").GetString());
        Assert.AreEqual("S-1-5-18", rows[0].GetProperty("userId").GetString());
        Assert.AreEqual("gate $(Arg0) $(Arg1) $(Arg2)", rows[0].GetProperty("arguments").GetString());
        Assert.AreEqual(0, rows[0].GetProperty("problems").GetArrayLength());
        Assert.IsFalse(rows[2].GetProperty("trayMayRun").GetBoolean());
        Assert.IsGreaterThan(0, rows[2].GetProperty("problems").GetArrayLength());
    }

    // Read-only against this machine's Task Scheduler: the probe's own reads. Earshot is not set up during
    // the build, so the folder and tasks are expected to be absent; if they exist the report must still read.
    [TestMethod]
    [TestCategory("ReadOnlySystem")]
    public void TheTaskProbeReadsThisMachine()
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        Program.TaskProbeReport? report = null;

        MtaThread.Run(() => report = Program.ReadTaskProbe(new ComScheduledTasks(), Program.ReadTaskFolder, InstallFolder, sid), TimeSpan.FromSeconds(60));

        Assert.IsNotNull(report);
        TestContext.WriteLine(@"\Earshot: " + NativeCodes.Name(report.FolderResult));
        foreach (Program.ProbeTaskRow row in report.Tasks)
        {
            TestContext.WriteLine(TaskPlan.TaskPath(row.Name) + ": " + NativeCodes.Name(row.OpenResult));
        }

        Assert.IsTrue(report.FolderResult is 0 or TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND, NativeCodes.Name(report.FolderResult));
        if (report.FolderResult == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND)
        {
            Assert.IsTrue(report.Tasks.All(t => t.OpenResult is TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND or unchecked((int)0x80070003)));
        }
    }
}
