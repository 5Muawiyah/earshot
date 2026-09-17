using Earshot.AudioProtection;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase6;

// The non-elevated service read, the protection classification and the BluetoothSetServiceState result table.
[TestClass]
public sealed class ServiceStateTests
{
    private static readonly Guid Handsfree = ProtectedServices.Handsfree;
    private static readonly Guid Headset = ProtectedServices.Headset;
    private static readonly Guid Sink = ProtectedServices.AudioSink;
    private static readonly string[] FindAndCloseFailures = ["ERROR_ACCESS_DENIED", "ERROR_REVISION_MISMATCH"];

    public static IEnumerable<object[]> ClassificationTable =>
    [
        // listed, complete, services, expected
        [false, false, Array.Empty<Guid>(), AudioProtectionState.Unknown],
        [false, true, new[] { Sink }, AudioProtectionState.Unknown],
        [true, true, FakeBluetoothServices.RecordedAirPodsServices, AudioProtectionState.NotProtected],
        [true, true, FakeBluetoothServices.RecordedAirPodsServices.Where(g => g != Handsfree).ToArray(), AudioProtectionState.Protected],
        [true, true, new[] { Sink, Handsfree, Headset }, AudioProtectionState.NotProtected],
        [true, true, new[] { Sink, Headset }, AudioProtectionState.Partial],
        [true, true, Array.Empty<Guid>(), AudioProtectionState.Protected],
        [true, false, new[] { Handsfree }, AudioProtectionState.NotProtected],
        [true, false, new[] { Sink }, AudioProtectionState.Unknown],
        [true, false, new[] { Sink, Headset }, AudioProtectionState.Unknown],
    ];

    [TestMethod]
    [DynamicData(nameof(ClassificationTable))]
    public void ClassificationFollowsTheInstalledServices(bool listed, bool complete, Guid[] services, AudioProtectionState expected)
    {
        Assert.AreEqual(expected, ProtectionClassifier.Classify(listed, complete, services));
    }

    [TestMethod]
    public void TheRecordedAirPodsReadAsNotProtectedWithHeadsetAbsent()
    {
        var reader = new ServiceStateReader(FakeBluetoothServices.AirPods());

        ServiceReadResult read = reader.Read(RecordedNodes.AirPodsAddress);
        AudioProtectionSnapshot snapshot = ProtectionClassifier.Snapshot(read);

        Assert.IsTrue(read.DeviceFound);
        Assert.IsTrue(read.Listed);
        Assert.IsTrue(read.Complete);
        Assert.AreEqual("Jonathan\u2019s AirPods Pro", read.DeviceName);
        Assert.HasCount(8, read.Services);
        Assert.AreEqual(AudioProtectionState.NotProtected, snapshot.State);
        Assert.IsTrue(snapshot.HandsfreeInstalled);
        Assert.IsFalse(snapshot.HeadsetInstalled);
        Assert.IsTrue(read.Steps.All(s => s.Ok));
    }

    [TestMethod]
    public void AbsentHeadsetAndAbsentHandsfreeIsProtected()
    {
        FakeBluetoothServices fake = FakeBluetoothServices.AirPods();
        fake[RecordedNodes.AirPodsAddress].TurnOffOutside(Handsfree);

        AudioProtectionSnapshot snapshot = ProtectionClassifier.Snapshot(new ServiceStateReader(fake).Read(RecordedNodes.AirPodsAddress));

        Assert.AreEqual(AudioProtectionState.Protected, snapshot.State);
        Assert.IsFalse(snapshot.HandsfreeInstalled);
        Assert.IsFalse(snapshot.HeadsetInstalled);
    }

    [TestMethod]
    public void AnUnpairedDeviceIsUnknownNeverGuessed()
    {
        FakeBluetoothServices fake = FakeBluetoothServices.AirPods();
        fake.Remove(RecordedNodes.AirPodsAddress);

        ServiceReadResult read = new ServiceStateReader(fake).Read(RecordedNodes.AirPodsAddress);

        Assert.IsFalse(read.DeviceFound);
        Assert.AreEqual(AudioProtectionState.Unknown, ProtectionClassifier.Snapshot(read).State);
        StepOutcome step = read.Steps.Single();
        Assert.AreEqual(BluetoothDeviceLookup.FindStep, step.Step);
        Assert.AreEqual("ERROR_NOT_FOUND", step.CodeName);
    }

    [TestMethod]
    public void AFailedDeviceListIsRecordedWithItsCode()
    {
        FakeBluetoothServices fake = FakeBluetoothServices.AirPods();
        fake.FindResult = BluetoothApis.ERROR_REVISION_MISMATCH;
        fake.CloseError = BluetoothApis.ERROR_ACCESS_DENIED;

        ServiceReadResult read = new ServiceStateReader(fake).Read(RecordedNodes.AirPodsAddress);

        Assert.AreEqual(AudioProtectionState.Unknown, ProtectionClassifier.Snapshot(read).State);
        CollectionAssert.AreEquivalent(FindAndCloseFailures, read.Steps.Select(s => s.CodeName).ToArray());
        Assert.IsTrue(read.Steps.All(s => !s.Ok));
    }

    [TestMethod]
    public void MoreDataIsASuccessfulButIncompleteRead()
    {
        FakeBluetoothServices fake = FakeBluetoothServices.AirPods();
        fake.ServicesResult = BluetoothApis.ERROR_MORE_DATA;
        fake.IncompleteList = [Sink];

        ServiceReadResult read = new ServiceStateReader(fake).Read(RecordedNodes.AirPodsAddress);

        Assert.IsTrue(read.Listed);
        Assert.IsFalse(read.Complete);
        Assert.IsTrue(read.Steps.Single().Ok);
        Assert.AreEqual("ERROR_MORE_DATA", read.Steps.Single().CodeName);
        Assert.AreEqual(AudioProtectionState.Unknown, ProtectionClassifier.Snapshot(read).State);
    }

    [TestMethod]
    public void AFailedServiceReadIsUnknown()
    {
        FakeBluetoothServices fake = FakeBluetoothServices.AirPods();
        fake.ServicesResult = BluetoothApis.ERROR_ACCESS_DENIED;

        ServiceReadResult read = new ServiceStateReader(fake).Read(RecordedNodes.AirPodsAddress);

        Assert.IsTrue(read.DeviceFound);
        Assert.IsFalse(read.Listed);
        Assert.IsFalse(read.Steps.Single().Ok);
        Assert.AreEqual(AudioProtectionState.Unknown, ProtectionClassifier.Snapshot(read).State);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("0a1b2c3d4e8c")]
    [DataRow("0A1B2C3D4E")]
    public void AMalformedAddressIsNeverLookedUp(string address)
    {
        FakeBluetoothServices fake = FakeBluetoothServices.AirPods();

        ServiceReadResult read = new ServiceStateReader(fake).Read(address);

        Assert.IsEmpty(fake.Calls);
        Assert.AreEqual(NativeCodes.NotAttempted, read.Steps.Single().Code);
    }

    [TestMethod]
    [DataRow(0x00000000u, nameof(ServiceChange.Changed), true, "ERROR_SUCCESS")]
    [DataRow(0x80070057u, nameof(ServiceChange.AlreadyInState), true, "E_INVALIDARG")]
    [DataRow(0x00000424u, nameof(ServiceChange.NotSupported), true, "ERROR_SERVICE_DOES_NOT_EXIST")]
    [DataRow(0x00000057u, nameof(ServiceChange.BadFlags), false, "ERROR_INVALID_PARAMETER")]
    [DataRow(0x00000005u, nameof(ServiceChange.Failed), false, "ERROR_ACCESS_DENIED")]
    [DataRow(0x0000048Fu, nameof(ServiceChange.Failed), false, "ERROR_DEVICE_NOT_CONNECTED")]
    [DataRow(0x80004005u, nameof(ServiceChange.Failed), false, "E_FAIL")]
    public void EverySetServiceStateResultHasItsMeaning(uint rc, string meaning, bool ok, string name)
    {
        ServiceChange change = Enum.Parse<ServiceChange>(meaning);
        Assert.AreEqual(change, ServiceStateResults.Map(rc));
        Assert.AreEqual(ok, ServiceStateResults.IsOk(change));

        StepOutcome step = ServiceStateResults.Step(Handsfree, enable: false, rc, TimeSpan.FromMilliseconds(12));
        Assert.AreEqual("bt-service-disable:Handsfree", step.Step);
        Assert.AreEqual(ok, step.Ok);
        Assert.AreEqual(unchecked((int)rc), step.Code);
        Assert.AreEqual(name, step.CodeName);
        Assert.EndsWith("Took 12 ms.", step.Detail!);
    }

    [TestMethod]
    public void BoolFieldsAreTestedAgainstZero()
    {
        var connected = new BLUETOOTH_DEVICE_INFO { Address = 0x0A1B2C3D4E8CUL, fConnected = 32 };
        var away = new BLUETOOTH_DEVICE_INFO { Address = 0x0A1B2C3D4E8CUL, fConnected = 0 };

        BluetoothDeviceEntry on = BluetoothDeviceEntry.FromInfo(connected);

        Assert.IsTrue(on.Connected, "fConnected read 32 on this hardware, never 1.");
        Assert.AreEqual("0A1B2C3D4E8C", on.Address12);
        Assert.IsFalse(BluetoothDeviceEntry.FromInfo(away).Connected);
    }

    [TestMethod]
    public void A2dpSinkIsNeverAServiceProtectionTurnsOff()
    {
        CollectionAssert.AreEqual(new[] { Handsfree, Headset }, ProtectedServices.TurnedOff.ToArray());
        Assert.IsFalse(ProtectedServices.IsTurnedOff(Sink));
        Assert.AreEqual(new Guid("0000111E-0000-1000-8000-00805F9B34FB"), Handsfree);
        Assert.AreEqual(new Guid("00001108-0000-1000-8000-00805F9B34FB"), Headset);
        Assert.AreEqual(new Guid("0000110B-0000-1000-8000-00805F9B34FB"), Sink);
    }
}
