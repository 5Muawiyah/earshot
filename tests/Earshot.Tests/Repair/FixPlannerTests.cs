using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

[TestClass]
public sealed class FixPlannerTests
{
    private static readonly string EmDash = ((char)0x2014).ToString();

    private static FixPlanResult Plan(FixRig rig) => FixPlanner.Build(rig.Nodes, rig.Records, rig.Leftovers);

    [TestMethod]
    public void ThePlanListsWhatEarshotsRecordsShowItChangedAndNothingElse()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();

        FixPlan plan = Plan(rig).Plan;

        Assert.HasCount(1, plan.Devices);
        FixDevice device = plan.Devices[0];
        Assert.IsTrue(device.Pinned);
        Assert.IsTrue(device.Nodes.All(n => n.FromRecords));
        // Present and disabled: enable. Out of the tree and flagged: clear the flag.
        FixNodeStep device0 = device.Nodes.Single(n => n.InstanceId == RecordedNodes.AirPodsDeviceNode);
        Assert.AreEqual(FixNodeAction.Enable, device0.Action);
        FixNodeStep absent = device.Nodes.Single(n => n.InstanceId == RecordedNodes.AirPodsTargets[8]);
        Assert.AreEqual(FixNodeAction.ClearFlag, absent.Action);
        Assert.HasCount(RecordedNodes.AirPodsTargets.Length, device.Nodes);
    }

    [TestMethod]
    public void TheIPhoneAndAHeadphonesFixtureAreNeverSelected()
    {
        using var rig = new FixRig(RecordedNodes.TableWithHeadphones(), pinned: false);
        rig.Gate.Pin(RecordedNodes.IPhone());
        rig.Gate.BlockAirPodsAtRest();
        foreach (string id in RecordedNodes.IPhoneNodes.Concat(RecordedNodes.HeadphonesNodes))
        {
            rig.Gate.Nodes[id].MarkDisabled(persistent: true);
        }

        FixPlan plan = Plan(rig).Plan;

        // Only the AirPods container can be in the plan, even with the phone pinned in the records.
        Assert.IsTrue(plan.Devices.All(d => d.ContainerId == RecordedNodes.AirPodsContainer), "A device other than the AirPods is in the plan.");
        IEnumerable<string> ids = plan.Devices.SelectMany(d => d.Nodes).Select(n => n.InstanceId);
        Assert.IsFalse(ids.Any(i => RecordedNodes.IPhoneNodes.Contains(i, StringComparer.OrdinalIgnoreCase)), "An iPhone node is in the plan.");
        Assert.IsFalse(ids.Any(i => RecordedNodes.HeadphonesNodes.Contains(i, StringComparer.OrdinalIgnoreCase)), "A headphones node is in the plan.");
        Assert.IsFalse(plan.DeviceNames.Any(n => n is "iPhone" or "Headphones"));
    }

    [TestMethod]
    public void NoNodeOutsideTheAirPodsContainerIsSelectedEvenWithTheAddress()
    {
        FakeNodeApi table = RecordedNodes.Table();
        var stray = new FakeNode(@"BTHENUM\{00001108-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&0000\b&1a2b3c4d&0&" + RecordedNodes.AirPodsAddress + "_C00000000", RecordedNodes.IPhoneContainer);
        stray.MarkDisabled(persistent: true);
        using var rig = new FixRig(new FakeNodeApi(table.Nodes.Append(stray)));
        rig.Gate.BlockAirPodsAtRest();
        foreach (string id in RecordedNodes.AirPodsNonTargets)
        {
            rig.Gate.Nodes[id].MarkDisabled(persistent: true);
        }

        FixPlan plan = Plan(rig).Plan;

        string[] ids = plan.Devices.SelectMany(d => d.Nodes).Select(n => n.InstanceId).ToArray();
        CollectionAssert.DoesNotContain(ids, stray.InstanceId);
        Assert.IsFalse(ids.Any(i => i.StartsWith(@"SWD\", StringComparison.Ordinal) || i.StartsWith("BTHHFENUM", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void WithoutAPinAnEarshotFootprintLetsEarshotsOwnChoiceBeOfferedAsMaybeEarshots()
    {
        using var rig = new FixRig(pinned: false);
        rig.Gate.BlockAirPodsAtRest();
        File.WriteAllText(rig.SettingsFile, "{}");
        rig.Records = new StoreFixRecords(new GateStore(rig.Gate.Machine + "-none"), () => File.Exists(rig.SettingsFile));

        FixPlan plan = Plan(rig).Plan;

        Assert.HasCount(1, plan.Devices);
        Assert.IsFalse(plan.Devices[0].Nodes.Any(n => n.FromRecords));
        Assert.IsNotEmpty(plan.Devices[0].Nodes);
    }

    [TestMethod]
    public void WithoutAPinAndWithoutAFootprintNothingIsChangedAndThePlanSaysSo()
    {
        using var rig = new FixRig(pinned: false);
        rig.Gate.BlockAirPodsAtRest();
        rig.Records = new StoreFixRecords(new GateStore(rig.Gate.Machine + "-none"), () => false);

        FixPlan plan = Plan(rig).Plan;

        Assert.IsTrue(plan.IsEmpty);
        Assert.IsFalse(plan.HasFootprint);
        Assert.IsTrue(plan.Notes.Any(n => n.Contains("no mark", StringComparison.OrdinalIgnoreCase)));
        // The device is still known, so it can be paired again.
        Assert.HasCount(1, plan.Devices);
    }

    [TestMethod]
    public void SeveralAirPodsWithoutAPinAskTheOwnerToPickOne()
    {
        FakeNodeApi table = RecordedNodes.Table();
        using var rig = new FixRig(new FakeNodeApi(table.Nodes.Concat(FixRig.SecondAirPodsNodes())), pinned: false);
        rig.Gate.BlockAirPodsAtRest();
        foreach (FakeNode n in rig.Gate.Nodes.Nodes.Where(n => n.Container == FixRig.SecondAirPodsContainer))
        {
            n.MarkDisabled(persistent: true);
        }

        File.WriteAllText(rig.SettingsFile, "{}");
        rig.Records = new StoreFixRecords(new GateStore(rig.Gate.Machine + "-none"), () => true);

        FixPlan plan = Plan(rig).Plan;

        Assert.IsTrue(plan.NeedsPick);
        Assert.HasCount(2, plan.Devices);
        FixPlan one = plan.OnlyDevice(FixRig.SecondAirPodsAddress);
        Assert.HasCount(1, one.Devices);
        Assert.IsFalse(one.NeedsPick);
    }

    [TestMethod]
    public void AnotherPairOfAirPodsIsLeftAloneWhenARecordNamesOne()
    {
        FakeNodeApi table = RecordedNodes.Table();
        using var rig = new FixRig(new FakeNodeApi(table.Nodes.Concat(FixRig.SecondAirPodsNodes())));
        rig.Gate.BlockAirPodsAtRest();
        foreach (FakeNode n in rig.Gate.Nodes.Nodes.Where(n => n.Container == FixRig.SecondAirPodsContainer))
        {
            n.MarkDisabled(persistent: true);
        }

        FixPlan plan = Plan(rig).Plan;

        Assert.HasCount(1, plan.Devices);
        Assert.AreEqual(RecordedNodes.AirPodsAddress, plan.Devices[0].Address);
    }

    [TestMethod]
    public void ServicesTurnedOffAreListedFromTheProtectionRecord()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        var record = new ProtectionRecord();
        record.DisabledServices.Add(new Guid("0000111E-0000-1000-8000-00805F9B34FB"));
        Assert.IsTrue(rig.Gate.Store.WriteProtection(record).Ok);

        FixPlan plan = Plan(rig).Plan;

        Assert.HasCount(1, plan.Services);
        Assert.AreEqual("Hands-Free", plan.Services[0].Label);
    }

    [TestMethod]
    public void ThePlanTextShowsNamesAndNoIdentifiers()
    {
        using var rig = new FixRig();
        rig.LeaveAirPodsBlocked();
        rig.Leftovers.Items.Add("The hand-back service is still installed");

        string text = FixPlanText.Describe(Plan(rig).Plan);

        Assert.Contains("AirPods", text, StringComparison.Ordinal);
        Assert.DoesNotContain(RecordedNodes.AirPodsAddress, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RecordedNodes.AirPodsContainer.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BTHENUM", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(EmDash, text, StringComparison.Ordinal);
        Assert.Contains("hand-back service", text, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AnUnreadableDeviceListGivesAnEmptyPlanWithTheCode()
    {
        using var rig = new FixRig();
        rig.Gate.Nodes.ListResult = Earshot.Interop.CfgMgr32.CR_FAILURE;

        FixPlanResult result = Plan(rig);

        Assert.IsFalse(result.Listed);
        Assert.IsTrue(result.Plan.IsEmpty);
        Assert.IsTrue(result.Steps.Any(s => !s.Ok && s.Step == "cm-list"));
    }
}
