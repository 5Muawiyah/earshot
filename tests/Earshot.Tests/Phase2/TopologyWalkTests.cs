using Earshot.Audio;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase2.EndpointFixtures;

namespace Earshot.Tests.Phase2;

// The topology walk against managed fakes: which endpoints it follows, and the guard that decides whether an
// adapter's IKsControl is activated at all (adapter ACTIVE and in the target container). The connect path
// reuses both, so every refusal is pinned here. Nothing reaches Core Audio.
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-topologies
[TestClass]
public sealed class TopologyWalkTests
{
    private const string SrcAdapter =
        "{2}.\\\\?\\bthenum#{0000110b-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&2027#b&1a2b3c4d&0&5A6b7C8d9Eaf_c00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\src";

    private const string WaveAdapter =
        "{2}.\\\\?\\bthhfenum#bthhfpaudio#c&2b3c4d5e&1&97#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\wave";

    private const string PhoneAdapter =
        "{2}.\\\\?\\bthhfenum#bthhfpaudio#c&4d5e6f7&0&97#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\wave";

    private static readonly string[] SrcThenWave = [SrcAdapter, WaveAdapter];

    private static readonly AudioEndpoint Render = AirPodsRender().Endpoint;
    private static readonly AudioEndpoint Capture = AirPodsCapture().Endpoint;
    private static readonly AudioEndpoint Phone = IPhoneHandsFree().Endpoint;

    private ReleaseLedger _ledger = null!;
    private FakeEnumerator _enumerator = null!;
    private List<FakeConnector> _connectors = null!;

    [TestInitialize]
    public void Setup()
    {
        _ledger = new ReleaseLedger();
        _enumerator = new FakeEnumerator(_ledger);
        _connectors = new List<FakeConnector>();
        AddEndpoint(Phone.EndpointId, PhoneAdapter);
        AddEndpoint(Capture.EndpointId, WaveAdapter);
        AddEndpoint(Render.EndpointId, SrcAdapter);
        AddAdapter(SrcAdapter, CoreAudio.DEVICE_STATE_ACTIVE, AirPodsContainer);
        AddAdapter(WaveAdapter, CoreAudio.DEVICE_STATE_ACTIVE, AirPodsContainer);
        AddAdapter(PhoneAdapter, CoreAudio.DEVICE_STATE_ACTIVE, IPhoneContainer);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _ledger.AssertBalanced();
        Assert.IsTrue(_connectors.All(c => c.TopologyEdits == 0), "IConnector.ConnectTo or Disconnect was called.");
        foreach (FakeDevice device in _enumerator.Devices.Values)
        {
            Assert.IsTrue(device.Store is null || device.Store.Writes == 0, "A property store was written.");
            Assert.IsTrue(device.Control is null || device.Control.MethodsAndEvents == 0, "A KS method or event was sent.");
        }
    }

    private FakeEndpointDevice AddEndpoint(string id, string? adapterId)
    {
        var connector = new FakeConnector { AdapterId = adapterId };
        _connectors.Add(connector);
        var device = new FakeEndpointDevice(_ledger, id) { Topology = new FakeTopology(_ledger) { Connector = connector } };
        _enumerator.Add(device);
        return device;
    }

    private FakeDevice AddAdapter(string id, uint state, Guid container)
    {
        var device = new FakeDevice(_ledger, id)
        {
            State = state,
            Store = new FakePropertyStore().Set(CoreAudio.PKEY_Device_ContainerId, container),
            Control = new FakeKsControl { PinCount = id == SrcAdapter ? 2u : 4u },
        };
        _enumerator.Devices[id] = device;
        return device;
    }

    private FakeDevice Adapter(string id) => _enumerator.Devices[id];

    private IReadOnlyList<FilterVisit> Visit(Guid target, params AdapterPath[] adapters) =>
        TopologyWalk.VisitFilters(_enumerator, adapters, target, (adapter, control) =>
        {
            StepOutcome read = TopologyWalk.ReadPinCount(control, adapter.AdapterId, out _);
            return new[] { read };
        }, _ledger.Release);

    private static AdapterPath Path(string adapterId, AudioEndpoint from) => new(adapterId, new[] { from });

    // Steps 1 and 2

    [TestMethod]
    public void FindAdaptersFollowsOnlyTheTargetContainersEndpoints()
    {
        AdapterDiscovery discovery = TopologyWalk.FindAdapters(_enumerator, new[] { Phone, Capture, Render }, AirPodsContainer, _ledger.Release);

        CollectionAssert.AreEqual(SrcThenWave, discovery.Adapters.Select(a => a.AdapterId).ToArray(), "Render first, and never the phone's filter.");
        Assert.IsTrue(discovery.Adapters[0].FromRender);
        Assert.IsFalse(discovery.Adapters[1].FromRender);
        Assert.IsEmpty(discovery.Steps);
        CollectionAssert.DoesNotContain(_enumerator.GetDeviceCalls, Phone.EndpointId);
        Assert.IsFalse(_enumerator.GetDeviceCalls.Contains(PhoneAdapter), "Finding adapters never opens an adapter.");
    }

    [TestMethod]
    public void FindAdaptersGroupsEndpointsThatShareAnAdapter()
    {
        var secondRender = new AudioEndpoint("{0.0.0.00000000}.{6d6e788a-3608-4ef8-8b08-08db2f516971}", EndpointFlow.Render, EndpointState.Unplugged, null, AirPodsContainer);
        AddEndpoint(secondRender.EndpointId, SrcAdapter);

        AdapterDiscovery discovery = TopologyWalk.FindAdapters(_enumerator, new[] { Render, secondRender }, AirPodsContainer, _ledger.Release);

        AdapterPath src = discovery.Adapters.Single();
        Assert.AreEqual(SrcAdapter, src.AdapterId);
        Assert.HasCount(2, src.FromEndpoints);
    }

    [TestMethod]
    public void FindAdaptersRefusesAContainerThatIsNeverATarget()
    {
        foreach (Guid target in new[] { Guid.Empty, PcContainer })
        {
            var endpoint = Render with { ContainerId = target };

            AdapterDiscovery discovery = TopologyWalk.FindAdapters(_enumerator, new[] { endpoint }, target, _ledger.Release);

            Assert.IsEmpty(discovery.Adapters);
            StepOutcome step = discovery.Steps.Single();
            Assert.AreEqual(TopologyWalk.GuardStep, step.Step);
            Assert.AreEqual(NativeCodes.NotAttempted, step.Code);
        }

        Assert.IsEmpty(_enumerator.GetDeviceCalls);
    }

    [TestMethod]
    public void FindAdaptersRecordsEachFailedStepAndCarriesOn()
    {
        var noTopology = new AudioEndpoint("{0.0.1.00000000}.{0b46d234-b82d-4b72-b995-e8e3ca2937ca}", EndpointFlow.Capture, EndpointState.Active, null, AirPodsContainer);
        var notConnected = new AudioEndpoint("{0.0.1.00000000}.{0b46d234-b82d-4b72-b995-e8e3ca2937cb}", EndpointFlow.Capture, EndpointState.Active, null, AirPodsContainer);
        _enumerator.GetDeviceFailures[Render.EndpointId] = CoreAudio.E_NOTFOUND;
        _enumerator.Add(new FakeEndpointDevice(_ledger, noTopology.EndpointId));
        AddEndpoint(notConnected.EndpointId, adapterId: null);

        AdapterDiscovery discovery = TopologyWalk.FindAdapters(_enumerator, new[] { Render, noTopology, notConnected, Capture }, AirPodsContainer, _ledger.Release);

        Assert.AreEqual(WaveAdapter, discovery.Adapters.Single().AdapterId, "The endpoint that works is still followed.");
        CollectionAssert.AreEqual(
            new[]
            {
                (TopologyWalk.GetEndpointStep + ":" + Render.EndpointId, "E_NOTFOUND"),
                (TopologyWalk.ActivateTopologyStep + ":" + noTopology.EndpointId, "E_NOINTERFACE"),
                (TopologyWalk.AdapterIdStep + ":" + notConnected.EndpointId, "E_NOTFOUND"),
            },
            discovery.Steps.Select(s => (s.Step, s.CodeName)).ToArray());
        Assert.IsTrue(discovery.Steps.All(s => !s.Ok));
    }

    // Step 3: the guard

    [TestMethod]
    public void AnActiveAdapterInTheTargetContainerIsActivatedAndReadOnly()
    {
        IReadOnlyList<FilterVisit> visits = Visit(AirPodsContainer, Path(SrcAdapter, Render), Path(WaveAdapter, Capture));

        Assert.HasCount(2, visits);
        foreach (FilterVisit visit in visits)
        {
            Assert.IsTrue(visit.GuardPassed, visit.Adapter.AdapterId);
            Assert.IsTrue(visit.ControlActivated, visit.Adapter.AdapterId);
            Assert.AreEqual(EndpointState.Active, visit.State);
            Assert.AreEqual(AirPodsContainer, visit.ContainerId);
            Assert.IsTrue(visit.Steps.All(s => s.Ok), string.Join(", ", visit.Steps.Select(CoreAudioDeviceMonitor.Describe)));
            CollectionAssert.AreEqual(new[] { typeof(IKsControl).GUID }, Adapter(visit.Adapter.AdapterId).Activations);

            KsRequest request = Adapter(visit.Adapter.AdapterId).Control!.Properties.Single();
            Assert.AreEqual(new KsRequest(KsControl.KSPROPSETID_Pin, KsControl.KSPROPERTY_PIN_CTYPES, KsControl.KSPROPERTY_TYPE_GET, 24, 4), request);
        }

        Assert.AreEqual(1, _ledger.Released(Adapter(SrcAdapter).Control!));
        Assert.AreEqual(1, _ledger.Released(Adapter(SrcAdapter)));
    }

    [TestMethod]
    [DataRow(CoreAudio.DEVICE_STATE_DISABLED)]
    [DataRow(CoreAudio.DEVICE_STATE_NOTPRESENT)]
    [DataRow(CoreAudio.DEVICE_STATE_UNPLUGGED)]
    [DataRow(0u)]
    public void AnAdapterThatIsNotActiveIsNeverActivated(uint state)
    {
        Adapter(SrcAdapter).State = state;

        FilterVisit visit = Visit(AirPodsContainer, Path(SrcAdapter, Render)).Single();

        Assert.IsFalse(visit.GuardPassed);
        Assert.IsFalse(visit.ControlActivated);
        Assert.AreEqual((EndpointState)state, visit.State);
        Assert.IsNull(visit.ContainerId, "The container is not read once the state fails the guard.");
        AssertRefusedWithoutActivation(visit, "not ACTIVE");
    }

    [TestMethod]
    public void AnAdapterInAnotherContainerIsNeverActivated()
    {
        // The phone's Hands-Free filter reached through an endpoint list that wrongly claims the AirPods container.
        FilterVisit visit = Visit(AirPodsContainer, Path(PhoneAdapter, Capture)).Single();

        Assert.IsFalse(visit.GuardPassed);
        Assert.AreEqual(EndpointState.Active, visit.State);
        Assert.AreEqual(IPhoneContainer, visit.ContainerId);
        AssertRefusedWithoutActivation(visit, "is not the target " + TopologyWalk.Format(AirPodsContainer));
    }

    [TestMethod]
    [DataRow("read fails")]
    [DataRow("absent")]
    [DataRow("wrong type")]
    [DataRow("store fails")]
    public void AnAdapterWhoseContainerCannotBeReadIsNeverActivated(string how)
    {
        FakeDevice adapter = Adapter(SrcAdapter);
        string failedStep = TopologyWalk.AdapterContainerStep + ":" + SrcAdapter;
        switch (how)
        {
            case "read fails":
                adapter.Store = new FakePropertyStore().Fail(CoreAudio.PKEY_Device_ContainerId, FakeHr.EFail);
                break;
            case "absent":
                adapter.Store = new FakePropertyStore();
                break;
            case "wrong type":
                adapter.Store = new FakePropertyStore().Set(CoreAudio.PKEY_Device_ContainerId, 7u);
                break;
            default:
                adapter.OpenStoreHr = FakeHr.EAccessDenied;
                failedStep = TopologyWalk.AdapterStoreStep + ":" + SrcAdapter;
                break;
        }

        FilterVisit visit = Visit(AirPodsContainer, Path(SrcAdapter, Render)).Single();

        Assert.IsFalse(visit.GuardPassed);
        Assert.IsNull(visit.ContainerId);
        Assert.IsTrue(visit.Steps.Any(s => s.Step == failedStep && !s.Ok), string.Join(", ", visit.Steps.Select(CoreAudioDeviceMonitor.Describe)));
        AssertRefusedWithoutActivation(visit, "could not be read");
    }

    [TestMethod]
    public void AVanishedFilterIsReportedAndSkipped()
    {
        _enumerator.GetDeviceFailures[SrcAdapter] = CoreAudio.ERROR_NO_SUCH_DEVICE_INTERFACE;

        IReadOnlyList<FilterVisit> visits = Visit(AirPodsContainer, Path(SrcAdapter, Render), Path(WaveAdapter, Capture));

        FilterVisit gone = visits[0];
        Assert.IsFalse(gone.GuardPassed);
        Assert.IsFalse(gone.ControlActivated);
        Assert.IsNull(gone.State);
        StepOutcome step = gone.Steps.Single();
        Assert.AreEqual(TopologyWalk.GetAdapterStep + ":" + SrcAdapter, step.Step);
        Assert.AreEqual("ERROR_NO_SUCH_DEVICE_INTERFACE", step.CodeName);
        Assert.IsTrue(visits[1].ControlActivated, "The other filter is still visited.");
    }

    [TestMethod]
    public void AnAdapterWhoseStateCannotBeReadIsNeverActivated()
    {
        Adapter(SrcAdapter).StateHr = FakeHr.EFail;

        FilterVisit visit = Visit(AirPodsContainer, Path(SrcAdapter, Render)).Single();

        Assert.IsFalse(visit.GuardPassed);
        Assert.IsNull(visit.State);
        Assert.AreEqual(TopologyWalk.AdapterStateStep + ":" + SrcAdapter, visit.Steps.Single().Step);
        Assert.IsEmpty(Adapter(SrcAdapter).Activations);
    }

    [TestMethod]
    public void VisitFiltersRefusesAContainerThatIsNeverATarget()
    {
        foreach (Guid target in new[] { Guid.Empty, PcContainer })
        {
            FilterVisit visit = Visit(target, Path(SrcAdapter, Render)).Single();

            Assert.IsFalse(visit.GuardPassed);
            Assert.AreEqual(NativeCodes.NotAttempted, visit.Steps.Single().Code);
        }

        Assert.IsEmpty(_enumerator.GetDeviceCalls, "No adapter is even opened.");
    }

    [TestMethod]
    public void AFailedIKsControlActivationIsRecorded()
    {
        Adapter(SrcAdapter).ActivateFailures[typeof(IKsControl).GUID] = CoreAudio.HRESULT_ERROR_FILE_NOT_FOUND;
        int uses = 0;

        FilterVisit visit = TopologyWalk.VisitFilters(_enumerator, new[] { Path(SrcAdapter, Render) }, AirPodsContainer, (_, _) =>
        {
            uses++;
            return Array.Empty<StepOutcome>();
        }, _ledger.Release).Single();

        Assert.IsTrue(visit.GuardPassed);
        Assert.IsFalse(visit.ControlActivated);
        StepOutcome step = visit.Steps.Single();
        Assert.AreEqual(TopologyWalk.ActivateControlStep + ":" + SrcAdapter, step.Step);
        Assert.AreEqual("ERROR_FILE_NOT_FOUND", step.CodeName);
        Assert.AreEqual(0, uses);
    }

    [TestMethod]
    public void TheControlAndAdapterAreReleasedWhenTheCallbackThrows()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            TopologyWalk.VisitFilters(_enumerator, new[] { Path(SrcAdapter, Render) }, AirPodsContainer,
                (_, _) => throw new InvalidOperationException("callback failed"), _ledger.Release));

        Assert.AreEqual(1, _ledger.Released(Adapter(SrcAdapter).Control!));
        Assert.AreEqual(1, _ledger.Released(Adapter(SrcAdapter)));
    }

    private void AssertRefusedWithoutActivation(FilterVisit visit, string reason)
    {
        Assert.IsFalse(visit.ControlActivated);
        StepOutcome guard = visit.Steps.Single(s => s.Step == TopologyWalk.GuardStep + ":" + visit.Adapter.AdapterId);
        Assert.IsFalse(guard.Ok);
        Assert.AreEqual(NativeCodes.NotAttempted, guard.Code);
        StringAssert.Contains(guard.Detail, reason);

        FakeDevice adapter = Adapter(visit.Adapter.AdapterId);
        Assert.IsEmpty(adapter.Activations, "IKsControl was activated on a refused adapter.");
        Assert.IsEmpty(adapter.Control!.Properties);
        Assert.AreEqual(1, _ledger.Released(adapter), "The adapter device is released.");
    }
}
