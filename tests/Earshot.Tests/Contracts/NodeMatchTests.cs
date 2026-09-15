using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Contracts;

// The safety test for node selection. The fixture is the device-node set recorded on the owner's
// PC during phase 0: the AirPods container (9 BTHENUM nodes, 1 BTHHFENUM child, 3 audio endpoints),
// the paired iPhone (13 BTHENUM nodes and its hands-free child, in a different container), and the
// Bluetooth radio bus node in the PC container.
[TestClass]
public sealed class NodeMatchTests
{
    private const string AirPodsAddress = "5A6B7C8D9EAF";
    private const string IPhoneAddress = "3410BE0E0ABB";

    private static readonly Guid AirPodsContainer = new("1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D");
    private static readonly Guid IPhoneContainer = new("4FB94536-5965-549C-A947-0B115F3D9B56");

    private sealed record Node(string InstanceId, Guid Container, string? Name);

    // The nine nodes a block must disable. The first four have no friendly name on this PC.
    private static readonly Node[] AirPodsTargets =
    [
        new(@"BTHENUM\{00001000-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000", AirPodsContainer, null),
        new(@"BTHENUM\{00001801-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000", AirPodsContainer, null),
        new(@"BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000", AirPodsContainer, null),
        new(@"BTHENUM\{4715650B-5E9D-4AC2-B898-A4FC0AA5DF78}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000", AirPodsContainer, null),
        new(@"BTHENUM\{0000110C-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000", AirPodsContainer, "Owner\u2019s AirPods Pro - Find My Avrcp Transport"),
        new(@"BTHENUM\DEV_5A6B7C8D9EAF\b&1a2b3c4d&0&BLUETOOTHDEVICE_5A6B7C8D9EAF", AirPodsContainer, "Owner\u2019s AirPods Pro"),
        new(@"BTHENUM\{0000110E-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000", AirPodsContainer, "Owner\u2019s AirPods Pro - Find My Avrcp Transport"),
        new(@"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000", AirPodsContainer, "Owner\u2019s AirPods Pro - Find My Hands-Free AG"),
        new(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&5A6B7C8D9EAF_C00000000", AirPodsContainer, "Owner\u2019s AirPods Pro - Find My"),
    ];

    // In the AirPods container but never disable targets.
    private static readonly Node[] AirPodsNonTargets =
    [
        new(@"BTHHFENUM\BTHHFPAUDIO\c&2b3c4d5e&1&97", AirPodsContainer, "Owner\u2019s AirPods Pro - Find My Hands-Free"),
        new(@"SWD\MMDEVAPI\{0.0.1.00000000}.{0B46D234-B82D-4B72-B995-E8E3CA2937C9}", AirPodsContainer, "Headset (Owner\u2019s AirPods Pro - Find My)"),
        new(@"SWD\MMDEVAPI\{0.0.0.00000000}.{A2901F31-DC17-41B7-B0AD-2F77A5F04490}", AirPodsContainer, "Headset (Owner\u2019s AirPods Pro - Find My Hands-Free)"),
        new(@"SWD\MMDEVAPI\{0.0.0.00000000}.{6D6E788A-3608-4EF8-8B08-08DB2F516970}", AirPodsContainer, "Headphones (Owner\u2019s AirPods Pro - Find My)"),
    ];

    // The paired iPhone, exactly as Windows spelled the ids.
    private static readonly Node[] IPhoneNodes =
    [
        new(@"BTHENUM\Dev_3410BE0E0ABB\b&1a2b3c4d&0&BluetoothDevice_3410BE0E0ABB", IPhoneContainer, "iPhone"),
        new(@"BTHENUM\{00000000-deca-fade-deca-deafdecacafe}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000", IPhoneContainer, null),
        new(@"BTHENUM\{00001000-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000", IPhoneContainer, null),
        new(@"BTHENUM\{0000110a-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000", IPhoneContainer, null),
        new(@"BTHENUM\{0000110c-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000", IPhoneContainer, null),
        new(@"BTHENUM\{0000110e-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000", IPhoneContainer, null),
        new(@"BTHENUM\{00001116-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000", IPhoneContainer, null),
        new(@"BTHENUM\{0000111f-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000", IPhoneContainer, null),
        new(@"BTHENUM\{0000112f-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000", IPhoneContainer, null),
        new(@"BTHENUM\{00001132-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000", IPhoneContainer, null),
        new(@"BTHENUM\{00001801-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000", IPhoneContainer, null),
        new(@"BTHENUM\{02030302-1d19-415f-86f2-22a2106a0a77}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000", IPhoneContainer, null),
        new(@"BTHENUM\{1ff31936-572e-4b36-a2bf-b2409b1aa6f4}_VID&0001004c_PID&0000\b&1a2b3c4d&0&3410BE0E0ABB_C00000000", IPhoneContainer, null),
        new(@"BTHHFENUM\BTHHFPAUDIO\c&4d5e6f7&0&97", IPhoneContainer, "iPhone Hands-Free HF Audio"),
    ];

    // The radio bus node matches the brief's prefix pattern but sits in the PC container with no address.
    private static readonly Node[] RadioNodes =
    [
        new(@"BTH\MS_BTHBRB\a&3c4d5e6&0&1", NodeMatch.PcContainer, null),
        new(@"BTH\MS_BTHBRB\a&3c4d5e6&0&1", NodeMatch.PcContainer, null),
    ];

    private static IEnumerable<Node> AllNodes() =>
        AirPodsTargets.Concat(AirPodsNonTargets).Concat(IPhoneNodes).Concat(RadioNodes);

    private static List<string> Select(IEnumerable<Node> nodes, Guid pinnedContainer, string address12) =>
        nodes.Where(n => NodeMatch.IsDisableTarget(n.InstanceId, n.Container, pinnedContainer, address12))
             .Select(n => n.InstanceId)
             .ToList();

    [TestMethod]
    public void FixtureMatchesTheRecordedCounts()
    {
        Assert.HasCount(9, AirPodsTargets);
        Assert.AreEqual(13, AirPodsTargets.Length + AirPodsNonTargets.Length);
        Assert.AreEqual(13, IPhoneNodes.Count(n => n.InstanceId.StartsWith(@"BTHENUM\", StringComparison.Ordinal)));
        Assert.AreEqual(4, AirPodsTargets.Count(n => n.Name is null));
    }

    [TestMethod]
    public void SelectsExactlyTheNineAirPodsBthenumNodes()
    {
        List<string> selected = Select(AllNodes(), AirPodsContainer, AirPodsAddress);

        CollectionAssert.AreEquivalent(AirPodsTargets.Select(n => n.InstanceId).ToList(), selected);
    }

    [TestMethod]
    public void SelectsTheNamelessAirPodsNodes()
    {
        List<string> selected = Select(AllNodes(), AirPodsContainer, AirPodsAddress);

        foreach (Node nameless in AirPodsTargets.Where(n => n.Name is null))
        {
            CollectionAssert.Contains(selected, nameless.InstanceId);
        }
    }

    [TestMethod]
    public void NeverSelectsTheIPhoneRadioOrAirPodsChildren()
    {
        List<string> selected = Select(AllNodes(), AirPodsContainer, AirPodsAddress);

        foreach (Node node in IPhoneNodes.Concat(RadioNodes).Concat(AirPodsNonTargets))
        {
            CollectionAssert.DoesNotContain(selected, node.InstanceId);
        }
    }

    [TestMethod]
    public void TheRadioMatchesTheBriefPrefixButIsNotSelected()
    {
        foreach (Node radio in RadioNodes)
        {
            Assert.IsTrue(radio.InstanceId.StartsWith(@"BTH\", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(NodeMatch.IsDisableTarget(radio.InstanceId, radio.Container, AirPodsContainer, AirPodsAddress));
            Assert.IsFalse(NodeMatch.IsDisableTarget(radio.InstanceId, radio.Container, NodeMatch.PcContainer, AirPodsAddress));
        }
    }

    [TestMethod]
    public void AnIPhoneNodeReportedInTheAirPodsContainerIsStillExcludedByAddress()
    {
        IEnumerable<Node> misreported = IPhoneNodes.Select(n => n with { Container = AirPodsContainer });

        Assert.IsEmpty(Select(misreported, AirPodsContainer, AirPodsAddress));
    }

    [TestMethod]
    public void AirPodsNodesReportedInAnotherContainerAreNotSelected()
    {
        IEnumerable<Node> moved = AirPodsTargets.Select(n => n with { Container = IPhoneContainer });

        Assert.IsEmpty(Select(moved, AirPodsContainer, AirPodsAddress));
    }

    [TestMethod]
    public void PinningTheIPhoneAddressInTheAirPodsContainerSelectsNothing()
    {
        Assert.IsEmpty(Select(AllNodes(), AirPodsContainer, IPhoneAddress));
    }

    [TestMethod]
    public void AnEmptyPinnedContainerSelectsNothing()
    {
        Assert.IsEmpty(Select(AllNodes(), Guid.Empty, AirPodsAddress));

        IEnumerable<Node> unreadable = AllNodes().Select(n => n with { Container = Guid.Empty });
        Assert.IsEmpty(Select(unreadable, Guid.Empty, AirPodsAddress));
        Assert.IsEmpty(Select(unreadable, AirPodsContainer, AirPodsAddress));
    }

    [TestMethod]
    public void ThePcContainerIsNeverATarget()
    {
        Assert.IsEmpty(Select(AllNodes(), NodeMatch.PcContainer, AirPodsAddress));

        // Even a Bluetooth node in the PC container that carries the address is refused.
        var planted = new Node(@"BTHENUM\DEV_5A6B7C8D9EAF\PLANTED", NodeMatch.PcContainer, null);
        Assert.IsFalse(NodeMatch.IsDisableTarget(planted.InstanceId, planted.Container, NodeMatch.PcContainer, AirPodsAddress));
    }

    [TestMethod]
    public void MatchingIgnoresInstanceIdCase()
    {
        foreach (Node node in AirPodsTargets)
        {
            Assert.IsTrue(NodeMatch.IsDisableTarget(node.InstanceId.ToLowerInvariant(), node.Container, AirPodsContainer, AirPodsAddress));
        }
    }

    // Each node is in the AirPods container and carries the AirPods address, so only the Bluetooth
    // prefix guard stands between it and a disable.
    [TestMethod]
    [DataRow(@"SWD\X\5A6B7C8D9EAF")]
    [DataRow(@"BTHHFENUM\X\5A6B7C8D9EAF")]
    [DataRow(@"BTHHFENUM\BTHHFPAUDIO\5A6B7C8D9EAF")]
    [DataRow(@"USB\VID_004C&PID_2027\5A6B7C8D9EAF")]
    [DataRow(@"SWD\MMDEVAPI\BTHENUM\5A6B7C8D9EAF")]
    [DataRow(@"XBTHENUM\DEV_5A6B7C8D9EAF")]
    [DataRow(@"BTHENUMX\DEV_5A6B7C8D9EAF")]
    [DataRow(@"BTHENUM_DEV_5A6B7C8D9EAF")]
    [DataRow(@"5A6B7C8D9EAF\BTHENUM\X")]
    public void ANonBluetoothPrefixIsRefusedEvenWithTheContainerAndAddress(string instanceId)
    {
        Assert.IsTrue(instanceId.Contains(AirPodsAddress, StringComparison.Ordinal));

        Assert.IsFalse(NodeMatch.IsDisableTarget(instanceId, AirPodsContainer, AirPodsContainer, AirPodsAddress));
    }

    [TestMethod]
    [DataRow(@"BTHENUM\DEV_5A6B7C8D9EAF\X")]
    [DataRow(@"BTHLE\DEV_5A6B7C8D9EAF\X")]
    [DataRow(@"BTHLEDEVICE\{00001800-0000-1000-8000-00805F9B34FB}_DEV_5A6B7C8D9EAF\X")]
    [DataRow(@"BTH\X\5A6B7C8D9EAF")]
    [DataRow(@"bthenum\dev_5A6b7C8d9Eaf\x")]
    public void EachBluetoothPrefixPassesThePrefixGuard(string instanceId)
    {
        Assert.IsTrue(NodeMatch.IsDisableTarget(instanceId, AirPodsContainer, AirPodsContainer, AirPodsAddress));
    }

    // An empty or partial pinned address is contained in real ids, and a lower-case one matches them
    // ignoring case. Each must select nothing rather than leave only the other two guards.
    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("5A6b7C8d9Eaf")]
    [DataRow("5A6B7C8d9EAF")]
    [DataRow("300E431D048")]
    [DataRow("00E431D048C")]
    [DataRow("5A6B7C8D9EAF0")]
    [DataRow("E431D048C")]
    [DataRow("0")]
    [DataRow("300E431D048G")]
    [DataRow("5A6B:7C8D:9EAF")]
    [DataRow(" 5A6B7C8D9EAF")]
    public void AnEmptyOrMalformedAddressSelectsNothing(string address)
    {
        Assert.IsEmpty(Select(AllNodes(), AirPodsContainer, address));
    }

    [TestMethod]
    public void IsValidTargetContainerRejectsEmptyAndPc()
    {
        Assert.IsFalse(NodeMatch.IsValidTargetContainer(Guid.Empty));
        Assert.IsFalse(NodeMatch.IsValidTargetContainer(NodeMatch.PcContainer));
        Assert.IsTrue(NodeMatch.IsValidTargetContainer(AirPodsContainer));
        Assert.IsTrue(NodeMatch.IsValidTargetContainer(IPhoneContainer));
    }
}
