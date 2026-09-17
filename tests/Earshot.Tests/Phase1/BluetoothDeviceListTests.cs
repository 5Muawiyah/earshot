using Earshot.Contracts;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;

namespace Earshot.Tests.Phase1;

// Instance ids exactly as Windows spelled them on the owner's PC during phase 0.
[TestClass]
public sealed class BluetoothDeviceListTests
{
    [TestMethod]
    [DataRow(@"BTHENUM\DEV_0A1B2C3D4E8C\b&1a2b3c4d&0&BLUETOOTHDEVICE_0A1B2C3D4E8C", "0A1B2C3D4E8C")]
    [DataRow(@"BTHENUM\Dev_1A2B3C4D5E6F\b&1a2b3c4d&0&BluetoothDevice_1A2B3C4D5E6F", "1A2B3C4D5E6F")]
    [DataRow(@"bthenum\dev_0a1b2c3d4e8c\b&1a2b3c4d&0&bluetoothdevice_0a1b2c3d4e8c", "0A1B2C3D4E8C")]
    public void TheAddressComesFromTheDeviceNode(string instanceId, string address)
    {
        Assert.IsTrue(BluetoothDeviceList.TryParseAddress(instanceId, out string parsed));
        Assert.AreEqual(address, parsed);
        Assert.IsTrue(BoundaryValidation.IsAddress12(parsed));
    }

    [TestMethod]
    // The AirPods service nodes carry the address but are not device nodes.
    [DataRow(@"BTHENUM\{00001000-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&0A1B2C3D4E8C_C00000000")]
    [DataRow(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&0A1B2C3D4E8C_C00000000")]
    [DataRow(@"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2027\b&1a2b3c4d&0&0A1B2C3D4E8C_C00000000")]
    [DataRow(@"BTHENUM\{74EC2172-0BAD-4D01-8F77-997B2BE0722A}_VID&0001004C_PID&2027\b&1a2b3c4d&0&0A1B2C3D4E8C_C00000000")]
    [DataRow(@"BTHENUM\{0000110a-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0000\b&1a2b3c4d&0&1A2B3C4D5E6F_C00000000")]
    // The radio, the hands-free child and an audio endpoint.
    [DataRow(@"BTH\MS_BTHBRB\a&3c4d5e6&0&1")]
    [DataRow(@"BTHHFENUM\BTHHFPAUDIO\c&2b3c4d5e&1&97")]
    [DataRow(@"SWD\MMDEVAPI\{0.0.0.00000000}.{6D6E788A-3608-4EF8-8B08-08DB2F516970}")]
    // Malformed and look-alike ids.
    [DataRow(@"BTHENUM\DEV_0A1B2C3D4E8\b&1a2b3c4d&0&BLUETOOTHDEVICE_0A1B2C3D4E8")]
    [DataRow(@"BTHENUM\DEV_0A1B2C3D4E8CA\b&1a2b3c4d&0&BLUETOOTHDEVICE_0A1B2C3D4E8CA")]
    [DataRow(@"BTHENUM\DEV_0A1B2C3D4E8G\b&1a2b3c4d&0&BLUETOOTHDEVICE_0A1B2C3D4E8G")]
    [DataRow(@"BTHENUM\DEV_0A1B2C3D4E8C")]
    [DataRow(@"XBTHENUM\DEV_0A1B2C3D4E8C\b&1a2b3c4d&0&BLUETOOTHDEVICE_0A1B2C3D4E8C")]
    [DataRow(@"BTHLE\DEV_0A1B2C3D4E8C\b&1a2b3c4d&0&BLUETOOTHDEVICE_0A1B2C3D4E8C")]
    [DataRow(@" BTHENUM\DEV_0A1B2C3D4E8C\b&1a2b3c4d&0&BLUETOOTHDEVICE_0A1B2C3D4E8C")]
    [DataRow("")]
    [DataRow(null)]
    public void OtherNodesHaveNoDeviceAddress(string? instanceId)
    {
        Assert.IsFalse(BluetoothDeviceList.TryParseAddress(instanceId, out string parsed));
        Assert.AreEqual("", parsed);
    }

    [TestMethod]
    public void TheAddressForAContainerIsTheSingleDeviceNodeInIt()
    {
        PairedDevice[] devices =
        [
            new(@"BTHENUM\DEV_0A1B2C3D4E8C\b&1a2b3c4d&0&BLUETOOTHDEVICE_0A1B2C3D4E8C", AirPodsName, AirPodsAddress, AirPodsContainer, true),
            new(@"BTHENUM\Dev_1A2B3C4D5E6F\b&1a2b3c4d&0&BluetoothDevice_1A2B3C4D5E6F", "iPhone", IPhoneAddress, IPhoneContainer, true),
        ];

        Assert.AreEqual(AirPodsAddress, BluetoothDeviceList.AddressForContainer(devices, AirPodsContainer));
        Assert.AreEqual(IPhoneAddress, BluetoothDeviceList.AddressForContainer(devices, IPhoneContainer));
        Assert.IsNull(BluetoothDeviceList.AddressForContainer(devices, Guid.NewGuid()));
    }

    [TestMethod]
    public void NoAddressIsGuessedForTheWrongKindOfContainer()
    {
        PairedDevice[] devices =
        [
            new("a", "Radio", AirPodsAddress, NodeMatch.PcContainer, true),
            new("b", "Nothing", IPhoneAddress, Guid.Empty, true),
            new("c", "One", AirPodsAddress, AirPodsContainer, true),
            new("d", "Two", IPhoneAddress, AirPodsContainer, false),
        ];

        Assert.IsNull(BluetoothDeviceList.AddressForContainer(devices, NodeMatch.PcContainer));
        Assert.IsNull(BluetoothDeviceList.AddressForContainer(devices, Guid.Empty));
        Assert.IsNull(BluetoothDeviceList.AddressForContainer(devices, AirPodsContainer), "Two addresses in one container are ambiguous.");
    }
}
