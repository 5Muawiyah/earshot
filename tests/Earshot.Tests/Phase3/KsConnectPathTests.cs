using System.Runtime.InteropServices;
using Earshot.Audio;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Tests.Phase2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase2.EndpointFixtures;
using static Earshot.Tests.Phase3.ConnectFixtures;

namespace Earshot.Tests.Phase3;

// The connect path against Core Audio fakes: the order requests reach the filters in, the step for each filter,
// the guard that keeps every other device's filter out, and that every COM object is released once. The request
// itself goes through a fake sender, except in the tests of KsPropertySender, which call a recording control
// directly. Nothing reaches a driver.
// https://learn.microsoft.com/en-us/windows/win32/coreaudio/using-the-ikscontrol-interface-to-access-audio-properties
[TestClass]
public sealed class KsConnectPathTests
{
    private static readonly string[] SrcThenWave = ["src", "wave"];
    private static readonly string[] DisconnectSteps = ["ks-disconnect:src", "ks-disconnect:wave"];
    private static readonly string[] FailFailNames = ["E_FAIL", "AUDCLNT_E_DEVICE_INVALIDATED"];
    private static readonly string[] SrcSrcWave = ["src", "src#2", "wave"];

    private FakeAudioMachine _machine = null!;
    private FakeKsSender _sender = null!;
    private bool _realSender;

    [TestInitialize]
    public void Setup()
    {
        _machine = FakeAudioMachine.Owner();
        _sender = new FakeKsSender();
        _realSender = false;
    }

    [TestCleanup]
    public void Cleanup() => _machine.AssertNothingChanged(requestsReachControls: _realSender);

    private FakeKsControl Control(string adapterId) => _machine.Adapter(adapterId).Control!;

    private KsSendResult Send(ConnectAction action, FilterChoice choice = FilterChoice.All, params AudioEndpoint[] endpoints)
    {
        AudioEndpoint[] from = endpoints.Length > 0 ? endpoints : [Capture(EndpointState.Unplugged), Render(EndpointState.Unplugged), Phone()];
        return _machine.Path(_sender).Send(AirPodsContainer, from, action, choice);
    }

    // Order and naming

    [TestMethod]
    public void ConnectReachesTheA2dpFilterBeforeTheHandsFreeFilter()
    {
        KsSendResult result = Send(ConnectAction.Connect);

        CollectionAssert.AreEqual(new[] { Control(SrcAdapter), Control(WaveAdapter) }, _sender.Calls.Select(c => c.Control).ToArray(),
            "The capture endpoint is listed first, yet the filter reached from the render endpoint gets the request first.");
        Assert.IsTrue(_sender.Calls.All(c => c.Action == ConnectAction.Connect));
        CollectionAssert.AreEqual(SrcThenWave, result.Filters.Select(f => f.Name).ToArray());
        Assert.IsTrue(result.Filters[0].SentUtc <= result.Filters[1].SentUtc);
    }

    [TestMethod]
    public void EachFilterGetsOneNamedStepWithItsRawHResult()
    {
        _sender.Answer(Control(SrcAdapter), CoreAudio.E_NOTFOUND);
        _sender.Answer(Control(WaveAdapter), SOk);

        KsSendResult result = Send(ConnectAction.Connect);

        CollectionAssert.AreEqual(
            new[]
            {
                ("ks-reconnect:src", false, CoreAudio.E_NOTFOUND, "E_NOTFOUND", (string?)("a2dp: " + SrcAdapter)),
                ("ks-reconnect:wave", true, SOk, "S_OK", (string?)("hands-free: " + WaveAdapter)),
            },
            result.Steps.Select(s => (s.Step, s.Ok, s.Code, s.CodeName, s.Detail)).ToArray());
        Assert.IsFalse(result.Filters[0].Accepted);
        Assert.IsTrue(result.Filters[1].Accepted);
        Assert.IsTrue(result.AnyAccepted, "One filter accepting is an attempt made.");
        CollectionAssert.AreEqual(new FilterRole?[] { FilterRole.A2dp, FilterRole.HandsFree }, result.Steps.Select(KsConnectPath.RoleOfStep).ToArray());
        Assert.IsNull(result.Fault);
    }

    [TestMethod]
    public void TheRoleComesFromTheEndpointFlowNotTheReferenceString()
    {
        // A driver that named its filters the other way round: the render endpoint leads to "\wave".
        const string RenamedA2dp = "{2}.\\\\?\\bthenum#renamed#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\wave";
        _machine = new FakeAudioMachine();
        _machine.AddEndpoint(Render(EndpointState.Unplugged), RenamedA2dp);
        _machine.AddAdapter(RenamedA2dp, AirPodsContainer);

        KsSendResult result = Send(ConnectAction.Connect, FilterChoice.Src, Render(EndpointState.Unplugged));

        FilterSend filter = result.Filters.Single();
        Assert.AreEqual(("wave", FilterRole.A2dp), (filter.Name, filter.Role));
        Assert.AreEqual(FilterRole.A2dp, KsConnectPath.RoleOfStep(filter.Step));
        Assert.HasCount(1, _sender.Calls, "diag ks src picks the filter reached from the render endpoint.");
    }

    [TestMethod]
    public void DisconnectGoesToEveryDistinctFilter()
    {
        var secondRender = new AudioEndpoint("{0.0.0.00000000}.{6d6e788a-3608-4ef8-8b08-08db2f516971}", EndpointFlow.Render, EndpointState.Active, null, AirPodsContainer);
        _machine.AddEndpoint(secondRender, SrcAdapter);

        KsSendResult result = Send(ConnectAction.Disconnect, FilterChoice.All, Render(EndpointState.Active), secondRender, Capture(EndpointState.Active));

        CollectionAssert.AreEqual(new[] { Control(SrcAdapter), Control(WaveAdapter) }, _sender.Calls.Select(c => c.Control).ToArray(),
            "Two endpoints on one filter still send one request to it.");
        Assert.IsTrue(_sender.Calls.All(c => c.Action == ConnectAction.Disconnect));
        CollectionAssert.AreEqual(DisconnectSteps, result.Steps.Select(s => s.Step).ToArray());
    }

    [TestMethod]
    public void EveryFilterFailingKeepsEveryHResult()
    {
        _sender.Answer(Control(SrcAdapter), EFail);
        _sender.Answer(Control(WaveAdapter), CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED);

        KsSendResult result = Send(ConnectAction.Connect);

        Assert.IsFalse(result.AnyAccepted);
        CollectionAssert.AreEqual(FailFailNames, result.Steps.Select(s => s.CodeName).ToArray());
        Assert.IsTrue(result.Filters.All(f => f.Sent), "A refused request was still sent.");
    }

    [TestMethod]
    public void OnlySOkCountsAsAnAttempt()
    {
        _sender.Answer(Control(SrcAdapter), SFalse);
        _sender.Answer(Control(WaveAdapter), SFalse);

        KsSendResult result = Send(ConnectAction.Connect);

        Assert.IsFalse(result.AnyAccepted);
        Assert.IsTrue(result.Steps.All(s => !s.Ok && s.Code == SFalse), "S_FALSE is a success HRESULT but not S_OK.");
    }

    // The guard

    [TestMethod]
    public void AFilterInThePhonesContainerIsRefusedEvenWhenAnAirPodsEndpointLeadsToIt()
    {
        // An upstream slip: the AirPods capture endpoint's connector points at the phone's Hands-Free filter.
        _machine.Connectors[1].AdapterId = PhoneAdapter;

        KsSendResult result = Send(ConnectAction.Connect);

        CollectionAssert.AreEqual(new[] { Control(SrcAdapter) }, _sender.Calls.Select(c => c.Control).ToArray());
        FilterSend phone = result.Filters.Single(f => f.Adapter.AdapterId == PhoneAdapter);
        Assert.IsFalse(phone.Sent);
        Assert.IsFalse(phone.Visit.GuardPassed);
        Assert.AreEqual(IPhoneContainer, phone.Visit.ContainerId);
        Assert.AreEqual(NativeCodes.NotAttempted, phone.Step.Code);
        Assert.AreEqual("ks-reconnect:wave", phone.Step.Step);
        Assert.IsTrue(result.Steps.Any(s => s.Step == TopologyWalk.GuardStep + ":" + PhoneAdapter), "The guard refusal is recorded.");
        CollectionAssert.DoesNotContain(_machine.Adapter(PhoneAdapter).Activations, typeof(IKsControl).GUID);
    }

    [TestMethod]
    public void ThePhonesEndpointIsNeverFollowed()
    {
        Send(ConnectAction.Disconnect);

        CollectionAssert.DoesNotContain(_machine.Enumerator.GetDeviceCalls, Phone().EndpointId);
        CollectionAssert.DoesNotContain(_machine.Enumerator.GetDeviceCalls, PhoneAdapter);
        Assert.IsFalse(_sender.Calls.Any(c => ReferenceEquals(c.Control, Control(PhoneAdapter))));
    }

    [TestMethod]
    public void AContainerThatIsNeverATargetSendsNothing()
    {
        foreach (Guid container in new[] { Guid.Empty, PcContainer })
        {
            KsSendResult result = _machine.Path(_sender).Send(container, [Render(EndpointState.Unplugged) with { ContainerId = container }],
                ConnectAction.Connect, FilterChoice.All);

            Assert.IsEmpty(result.Filters);
            Assert.AreEqual(TopologyWalk.GuardStep, result.Steps.Single().Step);
        }

        Assert.IsEmpty(_sender.Calls);
        Assert.IsEmpty(_machine.Enumerator.GetDeviceCalls);
    }

    [TestMethod]
    [DataRow(CoreAudio.DEVICE_STATE_NOTPRESENT)]
    [DataRow(CoreAudio.DEVICE_STATE_UNPLUGGED)]
    [DataRow(CoreAudio.DEVICE_STATE_DISABLED)]
    public void AFilterThatIsNotActiveGetsNoRequest(uint state)
    {
        _machine.Adapter(SrcAdapter).State = state;

        KsSendResult result = Send(ConnectAction.Connect);

        CollectionAssert.AreEqual(new[] { Control(WaveAdapter) }, _sender.Calls.Select(c => c.Control).ToArray());
        StepOutcome src = result.Filters[0].Step;
        Assert.AreEqual("ks-reconnect:src", src.Step);
        Assert.AreEqual(NativeCodes.NotAttempted, src.Code);
        Assert.IsTrue(src.Detail!.Contains("the guard did not pass", StringComparison.Ordinal));
        Assert.AreEqual(FilterRole.A2dp, KsConnectPath.RoleOfStep(src), "A request that was not sent still names its role.");
    }

    [TestMethod]
    public void ADeviceInvalidatedWhileActivatingIsRecordedAndNotSent()
    {
        _machine.Adapter(WaveAdapter).ActivateFailures[typeof(IKsControl).GUID] = CoreAudio.AUDCLNT_E_DEVICE_INVALIDATED;

        KsSendResult result = Send(ConnectAction.Connect);

        CollectionAssert.AreEqual(new[] { Control(SrcAdapter) }, _sender.Calls.Select(c => c.Control).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ("ks-reconnect:src", "S_OK"),
                (TopologyWalk.ActivateControlStep + ":" + WaveAdapter, "AUDCLNT_E_DEVICE_INVALIDATED"),
                ("ks-reconnect:wave", "NOT_ATTEMPTED"),
            },
            result.Steps.Select(s => (s.Step, s.CodeName)).ToArray());
        Assert.IsTrue(result.Filters[1].Visit.GuardPassed);
        Assert.IsTrue(result.Filters[1].Step.Detail!.Contains("could not be activated", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AFilterThatHasGoneIsRecorded()
    {
        _machine.Enumerator.GetDeviceFailures[SrcAdapter] = CoreAudio.ERROR_NO_SUCH_DEVICE_INTERFACE;

        KsSendResult result = Send(ConnectAction.Disconnect);

        Assert.AreEqual("ERROR_NO_SUCH_DEVICE_INTERFACE", result.Steps.Single(s => s.Step == TopologyWalk.GetAdapterStep + ":" + SrcAdapter).CodeName);
        Assert.AreEqual(NativeCodes.NotAttempted, result.Filters[0].Step.Code);
        Assert.AreEqual("the filter could not be opened.", result.Filters[0].NotSentReason);
        Assert.IsTrue(result.Filters[0].Step.Detail!.EndsWith("the filter could not be opened.", StringComparison.Ordinal));
        Assert.IsFalse(result.Filters[0].Step.Detail!.Contains("guard", StringComparison.Ordinal), "A filter that has gone was not refused by the guard.");
        Assert.HasCount(1, _sender.Calls);
    }

    [TestMethod]
    public void AFilterWhoseStateCannotBeReadSaysSo()
    {
        _machine.Adapter(SrcAdapter).StateHr = EFail;

        KsSendResult result = Send(ConnectAction.Connect);

        Assert.AreEqual("the filter state could not be read.", result.Filters[0].NotSentReason);
        Assert.HasCount(1, _sender.Calls);
    }

    // An exception in the walk

    [TestMethod]
    public void AnExceptionAfterARequestWasSentKeepsThatRequestAndIsRecorded()
    {
        var error = new InvalidComObjectException("The wrapper was released.");
        _sender.Throw(Control(WaveAdapter), error);

        KsSendResult result = Send(ConnectAction.Connect);

        Assert.AreSame(error, result.Fault);
        CollectionAssert.AreEqual(
            new[]
            {
                ("ks-reconnect:src", "S_OK"),
                (KsConnectPath.VisitFailedStep + ":" + WaveAdapter, NativeCodes.Name(error.HResult)),
                ("ks-reconnect:wave", "NOT_ATTEMPTED"),
            },
            result.Steps.Select(s => (s.Step, s.CodeName)).ToArray(),
            "The request the A2DP filter accepted is still on the result.");
        StepOutcome thrown = result.Steps[1];
        Assert.IsFalse(thrown.Ok);
        Assert.IsTrue(thrown.Detail!.StartsWith(nameof(InvalidComObjectException) + ": ", StringComparison.Ordinal));
        Assert.IsTrue(result.AnyAccepted);
        Assert.IsTrue(result.Filters[0].Accepted);
        Assert.IsFalse(result.Filters[1].Sent);
    }

    [TestMethod]
    public void AWalkThatThrowsSendsNothingToTheFiltersAfterIt()
    {
        var error = new InvalidComObjectException("The wrapper was released.");
        var throwing = new ThrowingStateDevice(_machine.Ledger, SrcAdapter, error);
        _machine.Enumerator.Devices[SrcAdapter] = throwing;

        KsSendResult result = Send(ConnectAction.Disconnect);

        Assert.AreSame(error, result.Fault);
        Assert.IsEmpty(_sender.Calls, "Nothing is sent once the walk has thrown.");
        CollectionAssert.DoesNotContain(_machine.Enumerator.GetDeviceCalls, WaveAdapter, "The filter after it is not even opened.");
        CollectionAssert.AreEqual(
            new[]
            {
                (KsConnectPath.VisitFailedStep + ":" + SrcAdapter, false),
                ("ks-disconnect:src", false),
                ("ks-disconnect:wave", false),
            },
            result.Steps.Select(s => (s.Step, s.Ok)).ToArray());
        Assert.AreEqual("the walk stopped at an earlier filter.", result.Filters[1].NotSentReason);
        Assert.AreEqual(1, _machine.Ledger.Lent(throwing));
        Assert.AreEqual(1, _machine.Ledger.Released(throwing), "The adapter is released as the walk unwinds.");
    }

    [TestMethod]
    public void EachSentRequestRecordsHowLongTheCallTook()
    {
        var time = new ManualTimeProvider();
        _sender.OnSend = () => time.Advance(TimeSpan.FromMilliseconds(250));
        _machine.Adapter(WaveAdapter).ActivateFailures[typeof(IKsControl).GUID] = CoreAudio.HRESULT_ERROR_FILE_NOT_FOUND;

        KsSendResult result = _machine.Path(_sender, time).Send(AirPodsContainer,
            [Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged)], ConnectAction.Connect, FilterChoice.All);

        Assert.AreEqual(TimeSpan.FromMilliseconds(250), result.Filters[0].CallDuration);
        Assert.IsNotNull(result.Filters[0].SentUtc);
        Assert.IsNull(result.Filters[1].CallDuration, "No request, no duration.");
        Assert.IsNull(result.Filters[1].SentUtc);
    }

    [TestMethod]
    public void TwoFiltersWithOneReferenceStringGetDistinctStepNames()
    {
        const string SecondSrc = "{2}.\\\\?\\bthenum#second#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\src";
        var secondRender = new AudioEndpoint("{0.0.0.00000000}.{6d6e788a-3608-4ef8-8b08-08db2f516972}", EndpointFlow.Render, EndpointState.Unplugged, null, AirPodsContainer);
        _machine.AddEndpoint(secondRender, SecondSrc);
        _machine.AddAdapter(SecondSrc, AirPodsContainer);

        KsSendResult result = Send(ConnectAction.Connect, FilterChoice.All, Render(EndpointState.Unplugged), secondRender, Capture(EndpointState.Unplugged));

        CollectionAssert.AreEqual(SrcSrcWave, result.Filters.Select(f => f.Name).ToArray());
        Assert.AreEqual(3, result.Steps.Select(s => s.Step).Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void OnlyAKsStepWithARoleDetailHasARole()
    {
        Assert.IsNull(KsConnectPath.RoleOfStep(StepOutcomes.FromHResult(TopologyWalk.ActivateControlStep + ":" + SrcAdapter, SOk, detail: "a2dp: x")));
        Assert.IsNull(KsConnectPath.RoleOfStep(StepOutcomes.FromHResult("ks-reconnect:src", SOk, detail: SrcAdapter)));
        Assert.IsNull(KsConnectPath.RoleOfStep(StepOutcomes.FromHResult("ks-reconnect:src", SOk)));
        Assert.AreEqual(FilterRole.HandsFree, KsConnectPath.RoleOfStep(StepOutcomes.FromHResult("ks-disconnect:wave", SOk, detail: "hands-free: " + WaveAdapter)));
    }

    [TestMethod]
    [DataRow((int)FilterChoice.Src, SrcAdapter)]
    [DataRow((int)FilterChoice.Wave, WaveAdapter)]
    public void AChosenFilterIsTheOnlyOneOpened(int choice, string adapterId)
    {
        KsSendResult result = Send(ConnectAction.Connect, (FilterChoice)choice);

        CollectionAssert.AreEqual(new[] { Control(adapterId) }, _sender.Calls.Select(c => c.Control).ToArray());
        Assert.AreEqual(adapterId, result.Filters.Single().Adapter.AdapterId);
        Assert.IsTrue(_sender.Calls.All(c => c.Payload == KsPayload.None), "The path sends the documented request by default.");
        Assert.HasCount(2, result.Adapters, "Both filters are still reported as found.");
        string other = adapterId == SrcAdapter ? WaveAdapter : SrcAdapter;
        CollectionAssert.DoesNotContain(_machine.Enumerator.GetDeviceCalls, other);
    }

    [TestMethod]
    public void AnEnumeratorThatCannotBeCreatedIsRecorded()
    {
        _machine.EnumeratorHr = unchecked((int)0x80040154);

        KsSendResult send = Send(ConnectAction.Connect);
        EndpointRead read = _machine.Path(_sender).ReadEndpoints(AirPodsContainer);

        Assert.AreEqual((AudioWorker.Steps.CreateEnumerator, "REGDB_E_CLASSNOTREG"), (send.Steps.Single().Step, send.Steps.Single().CodeName));
        Assert.IsFalse(read.Ok);
        Assert.AreEqual("REGDB_E_CLASSNOTREG", read.FailedSteps.Single().CodeName);
        Assert.IsEmpty(_sender.Calls);
    }

    // Reading the endpoints

    [TestMethod]
    public void ReadEndpointsKeepsOnlyTheContainersEndpointsRenderFirst()
    {
        const string HdmiId = "{0.0.0.00000000}.{aaaaaaaa-bbbb-4ccc-8ddd-000000000001}";
        FakeEndpointDevice hdmi = _machine.AddEndpoint(
            new AudioEndpoint(HdmiId, EndpointFlow.Render, EndpointState.NotPresent, null, PcContainer), adapterId: null);
        hdmi.Store!.Fail(CoreAudio.PKEY_Device_FriendlyName, CoreAudio.ERROR_NO_SUCH_DEVINST);
        FakeEndpointDevice capture = (FakeEndpointDevice)_machine.Enumerator.Devices[AirPodsCaptureId];
        capture.Store!.Fail(CoreAudio.PKEY_DeviceInterface_FriendlyName, EFail);

        EndpointRead read = _machine.Path(_sender).ReadEndpoints(AirPodsContainer);

        Assert.IsTrue(read.Ok);
        CollectionAssert.AreEqual(new[] { AirPodsRenderId, AirPodsCaptureId }, read.Endpoints.Select(e => e.EndpointId).ToArray());
        Assert.HasCount(2, read.FailedSteps);
        Assert.AreEqual(CoreAudioEndpointReader.InterfaceNameStep + ":" + AirPodsCaptureId, read.ContainerSteps.Single().Step,
            "Another device's failed read is not this container's.");
        Assert.IsEmpty(_sender.Calls, "Reading sends nothing.");
    }

    [TestMethod]
    public void AFullReadAndSendReleasesEveryObjectOnce()
    {
        _machine.Adapter(WaveAdapter).ActivateFailures[typeof(IKsControl).GUID] = CoreAudio.HRESULT_ERROR_FILE_NOT_FOUND;
        KsConnectPath path = _machine.Path(_sender);

        EndpointRead read = path.ReadEndpoints(AirPodsContainer);
        path.Send(AirPodsContainer, read.Endpoints, ConnectAction.Connect, FilterChoice.All);
        path.Send(AirPodsContainer, read.Endpoints, ConnectAction.Disconnect, FilterChoice.All);

        _machine.Ledger.AssertBalanced();
        FakeDevice src = _machine.Adapter(SrcAdapter);
        Assert.AreEqual(2, _machine.Ledger.Lent(src.Control!), "Each click activates IKsControl afresh; nothing is cached.");
        Assert.AreEqual(2, _machine.Ledger.Released(src.Control!));
    }

    // The request

    [TestMethod]
    [DataRow((int)ConnectAction.Connect, KsControl.KSPROPERTY_ONESHOT_RECONNECT)]
    [DataRow((int)ConnectAction.Disconnect, KsControl.KSPROPERTY_ONESHOT_DISCONNECT)]
    public void TheSenderBuildsTheDocumentedOneShotRequest(int action, uint id)
    {
        var control = new RecordingKsControl { Hr = CoreAudio.E_NOTFOUND };

        int hr = new KsPropertySender().Send(control, (ConnectAction)action, KsPayload.None, out uint bytes);

        Assert.AreEqual(CoreAudio.E_NOTFOUND, hr, "The HRESULT comes back untouched.");
        Assert.AreEqual(0u, bytes);
        KsCall call = control.Properties.Single();
        Assert.AreEqual(new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D"), call.Set);
        Assert.AreEqual(id, call.Id);
        Assert.AreEqual(0x00000001u, call.Flags, "KSPROPERTY_TYPE_GET");
        Assert.AreEqual(24u, call.PropertyLength);
        Assert.AreEqual((nint)0, call.PropertyData, "No property value is sent.");
        Assert.AreEqual(0u, call.DataLength);
        Assert.AreEqual(0, control.MethodsAndEvents);
    }

    [TestMethod]
    public void TheBufferVariantSendsFourZeroedBytesToTheSameRequest()
    {
        var control = new RecordingKsControl { Hr = SOk };

        int hr = new KsPropertySender().Send(control, ConnectAction.Connect, KsPayload.ZeroedFourBytes, out _);

        Assert.AreEqual(SOk, hr);
        KsCall call = control.Properties.Single();
        Assert.AreEqual((KsControl.KSPROPSETID_BtAudio, KsControl.KSPROPERTY_ONESHOT_RECONNECT, KsControl.KSPROPERTY_TYPE_GET, 24u),
            (call.Set, call.Id, call.Flags, call.PropertyLength));
        Assert.AreNotEqual((nint)0, call.PropertyData);
        Assert.AreEqual((4u, (int?)0), (call.DataLength, call.BufferValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new KsPropertySender().Send(control, ConnectAction.Connect, (KsPayload)9, out _));
        Assert.HasCount(1, control.Properties, "An unknown payload sends nothing.");
    }

    [TestMethod]
    public void ThePathPassesTheChosenPayloadToTheSender()
    {
        _machine.Path(_sender).Send(AirPodsContainer, [Render(EndpointState.Unplugged)], ConnectAction.Connect, FilterChoice.Src, KsPayload.ZeroedFourBytes);

        Assert.AreEqual(KsPayload.ZeroedFourBytes, _sender.Calls.Single().Payload);
    }

    [TestMethod]
    public void ReconnectIsZeroAndDisconnectIsOne()
    {
        Assert.AreEqual(0u, KsPropertySender.PropertyId(ConnectAction.Connect));
        Assert.AreEqual(1u, KsPropertySender.PropertyId(ConnectAction.Disconnect));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => KsPropertySender.PropertyId((ConnectAction)7));
    }

    [TestMethod]
    public void TheRealSenderGoesThroughTheWalkToTheLentControl()
    {
        _realSender = true;
        KsSendResult result = _machine.Path(new KsPropertySender()).Send(AirPodsContainer,
            [Render(EndpointState.Unplugged)], ConnectAction.Connect, FilterChoice.All);

        // The fake control answers anything but a pin count with E_NOTIMPL, and records the request.
        FakeKsControl src = _machine.Adapter(SrcAdapter).Control!;
        KsRequest request = src.Properties.Single();
        Assert.AreEqual(new KsRequest(KsControl.KSPROPSETID_BtAudio, 0, KsControl.KSPROPERTY_TYPE_GET, 24, 0), request);
        Assert.AreEqual("E_NOTIMPL", result.Filters.Single().Step.CodeName);
        Assert.IsEmpty(_machine.Adapter(WaveAdapter).Control!.Properties, "Only the render endpoint was given, so only its filter is reached.");
    }

    [TestMethod]
    [DataRow(SrcAdapter, "src")]
    [DataRow(WaveAdapter, "wave")]
    [DataRow("no-backslash", "no-backslash")]
    [DataRow("ends-with\\", "ends-with\\")]
    public void AFilterIsNamedByItsReferenceString(string adapterId, string name) =>
        Assert.AreEqual(name, KsConnectPath.FilterName(adapterId));

    [TestMethod]
    public void FilterChoiceFollowsTheRole()
    {
        var fromRender = new AdapterPath("anything\\wave", [Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged)]);
        var fromCapture = new AdapterPath("anything\\src", [Capture(EndpointState.Unplugged)]);

        Assert.IsTrue(KsConnectPath.IsChosen(FilterChoice.All, fromRender));
        Assert.IsTrue(KsConnectPath.IsChosen(FilterChoice.All, fromCapture));
        Assert.IsTrue(KsConnectPath.IsChosen(FilterChoice.Src, fromRender), "A filter reached from any render endpoint is the A2DP one.");
        Assert.IsFalse(KsConnectPath.IsChosen(FilterChoice.Src, fromCapture));
        Assert.IsTrue(KsConnectPath.IsChosen(FilterChoice.Wave, fromCapture));
        Assert.IsFalse(KsConnectPath.IsChosen(FilterChoice.Wave, fromRender));
        Assert.AreEqual(("a2dp", "hands-free"), (KsConnectPath.RoleName(FilterRole.A2dp), KsConnectPath.RoleName(FilterRole.HandsFree)));
    }
}
