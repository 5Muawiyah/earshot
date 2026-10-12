using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Earshot.Tests.Phase6;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

[TestClass]
public sealed class FixApplyTests
{
    private static FixApplyDeps Deps(FixRig rig, FakeBluetoothServices? bluetooth = null) => new()
    {
        Nodes = rig.Gate.Nodes,
        Store = rig.Gate.Store,
        Folders = rig.Gate.Folders,
        RunLock = NoGateRunLock.Instance,
        Bluetooth = bluetooth,
        Log = rig.Gate.Log,
        LeftoversReader = rig.Leftovers,
        Wait = _ => true,
        LockWait = TimeSpan.FromSeconds(1),
    };

    private static (FixApplyRequest Request, byte[] Bytes) Write(FixRig rig, FixPlan plan, string? name = null)
    {
        FixPlanFileInfo info = rig.PlanFiles.Write(plan);
        return (new FixApplyRequest(info.Path, info.Sha256), File.ReadAllBytes(info.Path));
    }

    private static FixPlan Planned(FixRig rig) => FixPlanner.Build(rig.Gate.Nodes, rig.Records, rig.Leftovers).Plan;

    [TestMethod]
    public void ItEnablesPresentDisabledNodesAndClearsTheFlagOnAbsentOnes()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        (FixApplyRequest request, _) = Write(rig, Planned(rig));

        FixApplyResult result = FixApply.Run(request, Deps(rig));

        Assert.AreEqual(GateExitCode.Success, result.Outcome, string.Join("; ", result.Steps.Where(s => !s.Ok).Select(s => s.Step + " " + s.CodeName)));
        Assert.IsNotEmpty(rig.Gate.Calls("enable").ToList());
        Assert.IsNotEmpty(rig.Gate.Calls("clear-flag").ToList());
        Assert.IsTrue(rig.Gate.Nodes.Nodes.Where(n => n.Container == RecordedNodes.AirPodsContainer && n.Present && n.InstanceId.StartsWith("BTHENUM", StringComparison.Ordinal)).All(n => !n.IsDisabled));
        Assert.IsTrue(RecordedNodes.AirPodsTargets.All(id => (rig.Gate.Nodes[id].ConfigFlags & CfgMgr32.CONFIGFLAG_DISABLED) == 0));
        // Never a disable or a set.
        Assert.IsEmpty(rig.Gate.Calls("disable").ToList());
        Assert.IsEmpty(rig.Gate.Calls("set-flag").ToList());
    }

    [TestMethod]
    public void ARecordedStepIsAppliedWithItsRawCode()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        (FixApplyRequest request, _) = Write(rig, Planned(rig));

        FixApplyResult result = FixApply.Run(request, Deps(rig));

        Assert.IsTrue(result.Steps.Any(s => s.Step.StartsWith("fix-enable:", StringComparison.Ordinal) && s.Code == 0));
        Assert.IsTrue(result.Steps.Any(s => s.Step.StartsWith("fix-clear-flag:", StringComparison.Ordinal) && s.Code == 0));
        Assert.IsTrue(result.Changes.Count >= 2);
    }

    [TestMethod]
    public void AFileThatAddsANodeNeverGetsItChanged()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        // Beside the real plan, the file names the iPhone's device node, the radio, an AirPods node that is fine, and a
        // node with the wrong action for how it stands.
        FixPlan real = Planned(rig);
        FixDevice device = real.Devices[0];
        var extra = new List<FixNodeStep>(device.Nodes)
        {
            new(RecordedNodes.IPhoneDeviceNode, "x", FixNodeAction.Enable, true),
            new(RecordedNodes.RadioNode, "x", FixNodeAction.ClearFlag, true),
            new(RecordedNodes.AirPodsNonTargets[0], "x", FixNodeAction.Enable, true),
        };
        rig.Gate.Nodes[RecordedNodes.IPhoneDeviceNode].MarkDisabled(persistent: true);
        FixPlan tampered = real with { Devices = [device with { Nodes = extra }] };
        (FixApplyRequest request, _) = Write(rig, tampered);

        FixApplyResult result = FixApply.Run(request, Deps(rig));

        string[] touched = rig.Gate.Nodes.Calls.Select(c => c.InstanceId).ToArray();
        CollectionAssert.DoesNotContain(touched, RecordedNodes.IPhoneDeviceNode);
        CollectionAssert.DoesNotContain(touched, RecordedNodes.RadioNode);
        CollectionAssert.DoesNotContain(touched, RecordedNodes.AirPodsNonTargets[0]);
        Assert.IsTrue(rig.Gate.Nodes[RecordedNodes.IPhoneDeviceNode].IsDisabled, "The iPhone's node was changed.");
        Assert.AreEqual(GateExitCode.Success, result.Outcome);
    }

    [TestMethod]
    public void AFileThatNamesFewerNodesNarrowsTheRun()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        FixPlan real = Planned(rig);
        FixDevice device = real.Devices[0];
        FixNodeStep only = device.Nodes.Single(n => n.InstanceId == RecordedNodes.AirPodsDeviceNode);
        (FixApplyRequest request, _) = Write(rig, real with { Devices = [device with { Nodes = [only] }] });

        FixApplyResult result = FixApply.Run(request, Deps(rig));

        Assert.AreEqual(GateExitCode.Success, result.Outcome);
        string[] touched = rig.Gate.Nodes.Calls.Select(c => c.InstanceId).Distinct().ToArray();
        CollectionAssert.AreEquivalent(new[] { RecordedNodes.AirPodsDeviceNode }, touched);
    }

    [TestMethod]
    public void ANodeThatIsNotBlockedAnyMoreIsNotTouchedEvenIfTheFileNamesIt()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        FixPlan real = Planned(rig);
        (FixApplyRequest request, _) = Write(rig, real);
        // Between the plan and the apply the owner enabled the device node by hand.
        rig.Gate.Nodes[RecordedNodes.AirPodsDeviceNode].Status &= ~CfgMgr32.DN_HAS_PROBLEM;
        rig.Gate.Nodes[RecordedNodes.AirPodsDeviceNode].Problem = 0;
        rig.Gate.Nodes[RecordedNodes.AirPodsDeviceNode].ConfigFlags = 0;

        FixApply.Run(request, Deps(rig));

        CollectionAssert.DoesNotContain(rig.Gate.Nodes.Calls.Select(c => c.InstanceId).ToArray(), RecordedNodes.AirPodsDeviceNode);
    }

    [TestMethod]
    public void AWrongHashOnTheCommandLineAppliesNothing()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        (FixApplyRequest request, _) = Write(rig, Planned(rig));

        FixApplyResult result = FixApply.Run(request with { PlanSha256 = new string('A', 64) }, Deps(rig));

        Assert.AreEqual(GateExitCode.Rejected, result.Outcome);
        Assert.IsEmpty(rig.Gate.Nodes.Calls);
    }

    [TestMethod]
    public void AFileChangedAfterItWasHashedAppliesNothing()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        (FixApplyRequest request, byte[] bytes) = Write(rig, Planned(rig));
        File.WriteAllBytes(request.PlanPath, bytes.Concat("\n"u8.ToArray()).ToArray());

        FixApplyResult result = FixApply.Run(request, Deps(rig));

        Assert.AreEqual(GateExitCode.Rejected, result.Outcome);
        Assert.IsEmpty(rig.Gate.Nodes.Calls);
    }

    [TestMethod]
    public void AMissingPlanFileAppliesNothing()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();

        FixApplyResult result = FixApply.Run(new FixApplyRequest(Path.Combine(Path.GetTempPath(), "no-such-folder", FixPlanFiles.FileName), new string('A', 64)), Deps(rig));

        Assert.AreEqual(GateExitCode.Rejected, result.Outcome);
        Assert.IsEmpty(rig.Gate.Nodes.Calls);
    }

    [TestMethod]
    public void AnEmptyPlanChangesNothing()
    {
        using var rig = new FixRig();
        (FixApplyRequest request, _) = Write(rig, Planned(rig));

        FixApplyResult result = FixApply.Run(request, Deps(rig));

        Assert.AreEqual(GateExitCode.Success, result.Outcome);
        Assert.IsEmpty(rig.Gate.Nodes.Calls);
        Assert.IsEmpty(result.Changes);
    }

    [TestMethod]
    public void AMachineFolderThatFailsTheSecurityCheckIsNotTrusted()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        (FixApplyRequest request, _) = Write(rig, Planned(rig));
        rig.Gate.Folders.SddlFor = _ => "O:BAG:SYD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;BU)";

        FixApplyResult result = FixApply.Run(request, Deps(rig));

        Assert.AreEqual(GateExitCode.FolderNotSecure, result.Outcome);
        Assert.IsEmpty(rig.Gate.Nodes.Calls);
    }

    [TestMethod]
    public void ServicesAreTurnedBackOnOnlyWhenTheFileNamesTheSameSet()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        var record = new ProtectionRecord();
        record.DisabledServices.Add(Earshot.AudioProtection.ProtectedServices.Handsfree);
        Assert.IsTrue(rig.Gate.Store.WriteProtection(record).Ok);
        FakeBluetoothServices bluetooth = FakeBluetoothServices.AirPods();
        bluetooth[RecordedNodes.AirPodsAddress].Enabled.Remove(Earshot.AudioProtection.ProtectedServices.Handsfree);
        (FixApplyRequest request, _) = Write(rig, Planned(rig));

        FixApplyResult result = FixApply.Run(request, Deps(rig, bluetooth));

        Assert.IsTrue(bluetooth.SetCalls.Any(c => c.Service == Earshot.AudioProtection.ProtectedServices.Handsfree && c.Enable), "Hands-Free was not turned back on.");
        Assert.IsTrue(result.Steps.Any(s => s.Step.Contains("111E", StringComparison.OrdinalIgnoreCase) || s.Step.Contains("handsfree", StringComparison.OrdinalIgnoreCase) || s.Step.StartsWith("bt-", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AFileWithoutTheServicesLeavesThemAlone()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        var record = new ProtectionRecord();
        record.DisabledServices.Add(Earshot.AudioProtection.ProtectedServices.Handsfree);
        Assert.IsTrue(rig.Gate.Store.WriteProtection(record).Ok);
        FakeBluetoothServices bluetooth = FakeBluetoothServices.AirPods();
        FixPlan real = Planned(rig);
        (FixApplyRequest request, _) = Write(rig, real with { Services = [] });

        FixApply.Run(request, Deps(rig, bluetooth));

        Assert.IsEmpty(bluetooth.SetCalls.ToList());
    }

    [TestMethod]
    public void TheResultIsKeptInTheMachineFolderForTheTrayToRead()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        (FixApplyRequest request, _) = Write(rig, Planned(rig));

        FixApplyResult result = FixApply.Run(request, Deps(rig));

        Assert.IsTrue(FixResultFile.Write(rig.Gate.Machine, request.PlanSha256, result).Ok);
        FixResultRead read = FixResultFile.Read(rig.Gate.Machine, request.PlanSha256);
        Assert.IsTrue(read.Ok, read.Problem);
        Assert.AreEqual((int)result.Outcome, read.ExitCode);
        Assert.AreEqual(result.Steps.Count, read.Steps.Count);
    }
}
