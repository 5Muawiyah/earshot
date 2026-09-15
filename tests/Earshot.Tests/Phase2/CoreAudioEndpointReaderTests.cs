using Earshot.Audio;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase2.EndpointFixtures;

namespace Earshot.Tests.Phase2;

// The endpoint reads against managed fakes: per-endpoint failures are recorded and never fatal, an endpoint is
// dropped only when it cannot be identified, and every object obtained is released once. Nothing reaches Core
// Audio.
// https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-enumaudioendpoints
[TestClass]
public sealed class CoreAudioEndpointReaderTests
{
    private const string HdmiId = "{0.0.0.00000000}.{aaaaaaaa-bbbb-4ccc-8ddd-000000000001}";

    private static readonly string[] AirPodsThenHdmi = [AirPodsRenderId, HdmiId];
    private static readonly string[] ItemThenStoreSteps = [CoreAudioEndpointReader.ItemStep + ":0", CoreAudioEndpointReader.StoreStep + ":" + HdmiId];

    private ReleaseLedger _ledger = null!;

    [TestInitialize]
    public void Setup() => _ledger = new ReleaseLedger();

    [TestCleanup]
    public void Cleanup() => _ledger.AssertBalanced();

    private FakeEndpointDevice AirPodsRenderDevice(FakePropertyStore? store = null) =>
        new(_ledger, AirPodsRenderId)
        {
            State = CoreAudio.DEVICE_STATE_UNPLUGGED,
            DataFlow = CoreAudio.eRender,
            Store = store ?? new FakePropertyStore()
                .Set(CoreAudio.PKEY_Device_FriendlyName, "Headphones (" + AirPodsName + ")")
                .Set(CoreAudio.PKEY_DeviceInterface_FriendlyName, AirPodsName)
                .Set(CoreAudio.PKEY_Device_ContainerId, AirPodsContainer),
        };

    [TestMethod]
    public void AnEndpointIsReadWithItsNamesAndContainer()
    {
        FakeEndpointDevice device = AirPodsRenderDevice();
        var steps = new List<StepOutcome>();

        EndpointReading? reading = CoreAudioEndpointReader.ReadEndpoint(device, steps, _ledger.Release);

        Assert.IsNotNull(reading);
        Assert.AreEqual(new AudioEndpoint(AirPodsRenderId, EndpointFlow.Render, EndpointState.Unplugged, "Headphones (" + AirPodsName + ")", AirPodsContainer), reading.Endpoint);
        Assert.AreEqual(AirPodsName, reading.InterfaceName, "The curly apostrophe is kept.");
        Assert.IsEmpty(steps);
        Assert.AreEqual(1, _ledger.Released(device.Store!));
        Assert.AreEqual(0, _ledger.Released(device), "The device stays the caller's to release.");
    }

    [TestMethod]
    public void ANotPresentEndpointWhoseFriendlyNameCannotBeReadIsKept()
    {
        var device = new FakeEndpointDevice(_ledger, HdmiId)
        {
            State = CoreAudio.DEVICE_STATE_NOTPRESENT,
            Store = new FakePropertyStore()
                .Fail(CoreAudio.PKEY_Device_FriendlyName, CoreAudio.ERROR_NO_SUCH_DEVINST)
                .Set(CoreAudio.PKEY_DeviceInterface_FriendlyName, "AMD High Definition Audio Device")
                .Set(CoreAudio.PKEY_Device_ContainerId, PcContainer),
        };
        var steps = new List<StepOutcome>();

        EndpointReading? reading = CoreAudioEndpointReader.ReadEndpoint(device, steps, _ledger.Release);

        Assert.IsNotNull(reading);
        Assert.IsNull(reading.Endpoint.FriendlyName);
        Assert.AreEqual(EndpointState.NotPresent, reading.Endpoint.State);
        Assert.AreEqual(PcContainer, reading.Endpoint.ContainerId);
        Assert.AreEqual("AMD High Definition Audio Device", reading.InterfaceName);
        StepOutcome step = steps.Single();
        Assert.AreEqual(CoreAudioEndpointReader.FriendlyNameStep + ":" + HdmiId, step.Step);
        Assert.AreEqual("ERROR_NO_SUCH_DEVINST", step.CodeName);
        Assert.IsFalse(step.Ok);
        Assert.AreEqual(0, device.Store!.Writes);
    }

    [TestMethod]
    public void AnEndpointWhoseStateOrContainerCannotBeReadIsKeptWithSteps()
    {
        FakeEndpointDevice device = AirPodsRenderDevice(new FakePropertyStore()
            .Set(CoreAudio.PKEY_Device_FriendlyName, "Headphones (" + AirPodsName + ")")
            .Fail(CoreAudio.PKEY_Device_ContainerId, FakeHr.EFail));
        device.StateHr = FakeHr.EFail;
        var steps = new List<StepOutcome>();

        EndpointReading? reading = CoreAudioEndpointReader.ReadEndpoint(device, steps, _ledger.Release);

        Assert.IsNotNull(reading);
        Assert.AreEqual((EndpointState)0, reading.Endpoint.State);
        Assert.AreEqual(Guid.Empty, reading.Endpoint.ContainerId, "An unreadable container is Guid.Empty, which is never a target.");
        Assert.IsNull(reading.InterfaceName, "An absent interface name is not a failure.");
        CollectionAssert.AreEqual(
            new[] { (CoreAudioEndpointReader.StateStep + ":" + AirPodsRenderId, "E_FAIL"), (CoreAudioEndpointReader.ContainerStep + ":" + AirPodsRenderId, "E_FAIL") },
            steps.Select(s => (s.Step, s.CodeName)).ToArray());
    }

    [TestMethod]
    public void AnEndpointWhosePropertyStoreCannotBeOpenedIsKeptWithoutNames()
    {
        FakeEndpointDevice device = AirPodsRenderDevice();
        device.OpenStoreHr = FakeHr.EAccessDenied;
        var steps = new List<StepOutcome>();

        EndpointReading? reading = CoreAudioEndpointReader.ReadEndpoint(device, steps, _ledger.Release);

        Assert.IsNotNull(reading);
        Assert.IsNull(reading.Endpoint.FriendlyName);
        Assert.AreEqual(Guid.Empty, reading.Endpoint.ContainerId);
        Assert.AreEqual(CoreAudioEndpointReader.StoreStep + ":" + AirPodsRenderId, steps.Single().Step);
    }

    [TestMethod]
    public void AnEndpointThatCannotBeIdentifiedIsDropped()
    {
        FakeEndpointDevice noId = AirPodsRenderDevice();
        noId.IdHr = FakeHr.EFail;
        FakeEndpointDevice oddFlow = AirPodsRenderDevice();
        oddFlow.DataFlow = CoreAudio.eAll;
        FakeEndpointDevice flowFails = AirPodsRenderDevice();
        flowFails.DataFlowHr = FakeHr.EFail;
        var notAnEndpoint = new FakeDevice(_ledger, AirPodsRenderId) { Store = new FakePropertyStore() };

        var steps = new List<StepOutcome>();
        Assert.IsNull(CoreAudioEndpointReader.ReadEndpoint(noId, steps, _ledger.Release));
        Assert.IsNull(CoreAudioEndpointReader.ReadEndpoint(oddFlow, steps, _ledger.Release));
        Assert.IsNull(CoreAudioEndpointReader.ReadEndpoint(flowFails, steps, _ledger.Release));
        Assert.IsNull(CoreAudioEndpointReader.ReadEndpoint(notAnEndpoint, steps, _ledger.Release));

        CollectionAssert.AreEqual(
            new[]
            {
                (CoreAudioEndpointReader.IdStep, "E_FAIL", (string?)null),
                (CoreAudioEndpointReader.FlowStep + ":" + AirPodsRenderId, "S_OK", "unexpected data flow 2"),
                (CoreAudioEndpointReader.FlowStep + ":" + AirPodsRenderId, "E_FAIL", null),
                (CoreAudioEndpointReader.FlowStep + ":" + AirPodsRenderId, "E_NOINTERFACE", null),
            },
            steps.Select(s => (s.Step, s.CodeName, s.Detail)).ToArray());
        Assert.IsTrue(steps.All(s => !s.Ok));
        Assert.AreEqual(0, _ledger.Lent(noId.Store!) + _ledger.Lent(oddFlow.Store!) + _ledger.Lent(flowFails.Store!) + _ledger.Lent(notAnEndpoint.Store!),
            "No property store is opened for an endpoint that cannot be identified.");
    }

    [TestMethod]
    public void ReadAllReadsEveryEndpointInEveryStateAndReleasesEachObjectOnce()
    {
        var enumerator = new FakeEnumerator(_ledger);
        var collection = new FakeCollection(_ledger);
        FakeEndpointDevice airPods = AirPodsRenderDevice();
        var hdmi = new FakeEndpointDevice(_ledger, HdmiId) { State = CoreAudio.DEVICE_STATE_NOTPRESENT, OpenStoreHr = FakeHr.EFail };
        collection.Items.Add((FakeHr.EFail, null));
        collection.Items.Add((FakeHr.SOk, airPods));
        collection.Items.Add((FakeHr.SOk, hdmi));
        enumerator.Collection = collection;

        EndpointEnumeration enumeration = CoreAudioEndpointReader.ReadAll(enumerator, _ledger.Release);

        Assert.AreEqual<(int, uint)?>((CoreAudio.eAll, CoreAudio.DEVICE_STATEMASK_ALL), enumerator.EnumerateArguments);
        Assert.IsTrue(enumeration.Ok, "Per-endpoint failures are never fatal.");
        CollectionAssert.AreEqual(AirPodsThenHdmi, enumeration.Readings.Select(r => r.Endpoint.EndpointId).ToArray());
        CollectionAssert.AreEqual(ItemThenStoreSteps, enumeration.Steps.Select(s => s.Step).ToArray());
        Assert.AreEqual(1, _ledger.Released(collection));
        Assert.AreEqual(1, _ledger.Released(airPods));
        Assert.AreEqual(1, _ledger.Released(hdmi));
    }

    [TestMethod]
    public void AFailedEnumerationIsNotOk()
    {
        var enumerator = new FakeEnumerator(_ledger) { EnumerateHr = FakeHr.EOutOfMemory };

        EndpointEnumeration failed = CoreAudioEndpointReader.ReadAll(enumerator, _ledger.Release);

        Assert.IsFalse(failed.Ok);
        Assert.AreEqual(CoreAudioEndpointReader.EnumerateStep, failed.Steps.Single().Step);
        Assert.IsEmpty(failed.Readings);
    }

    [TestMethod]
    public void AFailedCountIsNotOkAndTheCollectionIsReleased()
    {
        var collection = new FakeCollection(_ledger) { CountHr = FakeHr.EFail };
        var enumerator = new FakeEnumerator(_ledger) { Collection = collection };

        EndpointEnumeration failed = CoreAudioEndpointReader.ReadAll(enumerator, _ledger.Release);

        Assert.IsFalse(failed.Ok);
        Assert.AreEqual(CoreAudioEndpointReader.CountStep, failed.Steps.Single().Step);
        Assert.AreEqual(1, _ledger.Released(collection));
    }
}
