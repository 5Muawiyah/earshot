using Earshot.Audio;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase2.EndpointFixtures;

namespace Earshot.Tests.Phase2;

[TestClass]
public sealed class EndpointModelBuilderTests
{
    private static readonly DateTimeOffset Taken = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] RenderFirstThenId = ["r-1", "r-2", "c-1", "c-2"];

    private static EndpointModel Build(IReadOnlyList<EndpointReading> readings, string? match = "AirPods", Guid pinned = default) =>
        EndpointModelBuilder.Build(readings, match, pinned, Taken);

    private static AudioEndpoint Endpoint(string id, EndpointFlow flow, EndpointState state, Guid container, string? name = "x") =>
        new(id, flow, state, name, container);

    // Grouping

    [TestMethod]
    public void GroupsEndpointsByContainer()
    {
        EndpointModel model = Build(Machine());

        DeviceSnapshot snapshot = model.Snapshot;
        Assert.HasCount(3, snapshot.AllGroups);
        DeviceModel airPods = snapshot.AllGroups.Single(g => g.ContainerId == AirPodsContainer);
        Assert.HasCount(2, airPods.Endpoints);
        Assert.IsTrue(airPods.Endpoints.All(e => e.ContainerId == AirPodsContainer));

        DeviceModel pc = snapshot.AllGroups.Single(g => g.ContainerId == PcContainer);
        Assert.HasCount(5, pc.Endpoints);
        Assert.AreEqual(Taken, snapshot.TakenUtc);
    }

    [TestMethod]
    public void EveryEndpointAppearsInExactlyOneGroup()
    {
        List<EndpointReading> machine = Machine();

        DeviceSnapshot snapshot = Build(machine).Snapshot;

        List<string> grouped = snapshot.AllGroups.SelectMany(g => g.Endpoints).Select(e => e.EndpointId).ToList();
        CollectionAssert.AreEquivalent(machine.Select(r => r.Endpoint.EndpointId).ToList(), grouped);
        foreach (DeviceModel group in snapshot.AllGroups)
        {
            Assert.IsTrue(group.Endpoints.All(e => e.ContainerId == group.ContainerId));
        }
    }

    [TestMethod]
    public void TargetCapableGroupsComeFirstThenThePcAndEmptyContainers()
    {
        var readings = new List<EndpointReading>
        {
            RealtekSpeakers(),
            EndpointReading.Of(Endpoint("{0.0.0.00000000}.{e}", EndpointFlow.Render, EndpointState.NotPresent, Guid.Empty, "A unreadable container")),
            UsbMicrophone(),
            AirPodsRender(),
        };

        DeviceSnapshot snapshot = Build(readings).Snapshot;

        Guid[] order = snapshot.AllGroups.Select(g => g.ContainerId).ToArray();
        CollectionAssert.AreEqual(new[] { AirPodsContainer, MicrophoneContainer, Guid.Empty, PcContainer }, order);
    }

    [TestMethod]
    public void NamelessGroupsFollowNamedOnes()
    {
        var nameless = new Guid("9a64ec90-3049-5710-96f2-493b2cff6b57");
        var readings = new List<EndpointReading>
        {
            new(Endpoint("{0.0.0.00000000}.{n}", EndpointFlow.Render, EndpointState.NotPresent, nameless, name: null), null),
            UsbMicrophone(),
            AirPodsRender(),
        };

        Guid[] order = Build(readings).Snapshot.AllGroups.Select(g => g.ContainerId).ToArray();

        CollectionAssert.AreEqual(new[] { AirPodsContainer, MicrophoneContainer, nameless }, order);
    }

    [TestMethod]
    public void EndpointsInAGroupAreRenderFirstThenById()
    {
        var readings = new List<EndpointReading>
        {
            EndpointReading.Of(Endpoint("c-2", EndpointFlow.Capture, EndpointState.Active, AirPodsContainer)),
            EndpointReading.Of(Endpoint("r-2", EndpointFlow.Render, EndpointState.Active, AirPodsContainer)),
            EndpointReading.Of(Endpoint("c-1", EndpointFlow.Capture, EndpointState.Active, AirPodsContainer)),
            EndpointReading.Of(Endpoint("r-1", EndpointFlow.Render, EndpointState.NotPresent, AirPodsContainer)),
        };

        DeviceModel group = Build(readings).Snapshot.AllGroups.Single();

        CollectionAssert.AreEqual(RenderFirstThenId, group.Endpoints.Select(e => e.EndpointId).ToArray());
    }

    [TestMethod]
    public void NoEndpointsGiveNoGroupsAndNoTarget()
    {
        EndpointModel model = Build(Array.Empty<EndpointReading>());

        Assert.IsEmpty(model.Snapshot.AllGroups);
        Assert.IsNull(model.Snapshot.Target);
        Assert.AreEqual(TargetResolution.NotFound, model.Resolution);
    }

    // A built snapshot is a successful read that no monitor has numbered.
    [TestMethod]
    public void ABuiltSnapshotIsAnOkReadWithTheResolutionAndNoSequence()
    {
        foreach (EndpointModel model in new[] { Build(Machine()), Build(Machine(), pinned: AirPodsContainer), Build(Machine(), match: "Nothing") })
        {
            Assert.AreEqual(SnapshotReadStatus.Ok, model.Snapshot.ReadStatus);
            Assert.AreEqual(model.Resolution, model.Snapshot.Resolution);
            Assert.AreEqual(0L, model.Snapshot.Sequence);
        }
    }

    // Display name

    [TestMethod]
    public void DisplayNamePrefersTheInterfaceNameAndKeepsTheCurlyApostrophe()
    {
        DeviceModel airPods = Build(Machine()).Snapshot.AllGroups.Single(g => g.ContainerId == AirPodsContainer);

        Assert.AreEqual(AirPodsName, airPods.DisplayName);
        Assert.IsTrue(airPods.DisplayName.Contains('’', StringComparison.Ordinal));
        Assert.IsFalse(airPods.DisplayName.Contains('\'', StringComparison.Ordinal));
    }

    [TestMethod]
    public void DisplayNameFallsBackToTheAdapterPartOfTheFriendlyName()
    {
        var readings = new List<EndpointReading>
        {
            EndpointReading.Of(AirPodsRender().Endpoint),
            EndpointReading.Of(AirPodsCapture().Endpoint),
        };

        Assert.AreEqual(AirPodsName, EndpointModelBuilder.ChooseDisplayName(readings));
    }

    [TestMethod]
    public void DisplayNameIsEmptyWhenNothingIsReadable()
    {
        var readings = new List<EndpointReading> { AmdHdmiUnreadable(1), AmdHdmiUnreadable(2) };

        Assert.AreEqual("", EndpointModelBuilder.ChooseDisplayName(readings));
    }

    [TestMethod]
    public void DisplayNameComesFromTheRenderEndpointBeforeTheCaptureEndpoint()
    {
        var readings = new List<EndpointReading>
        {
            new(Endpoint("c", EndpointFlow.Capture, EndpointState.Active, AirPodsContainer), "Capture side"),
            new(Endpoint("r", EndpointFlow.Render, EndpointState.NotPresent, AirPodsContainer), "Render side"),
        };

        Assert.AreEqual("Render side", EndpointModelBuilder.ChooseDisplayName(readings));
    }

    [TestMethod]
    public void DisplayNameSkipsBlankNames()
    {
        var readings = new List<EndpointReading>
        {
            new(Endpoint("r1", EndpointFlow.Render, EndpointState.Active, AirPodsContainer, name: "  "), "  "),
            new(Endpoint("r2", EndpointFlow.Render, EndpointState.Unplugged, AirPodsContainer, name: "Headphones (Second)"), null),
        };

        Assert.AreEqual("Second", EndpointModelBuilder.ChooseDisplayName(readings));
    }

    [TestMethod]
    [DataRow("Headphones (Jonathan’s AirPods Pro - Find My)", "Jonathan’s AirPods Pro - Find My")]
    [DataRow("Speakers (Realtek(R) Audio)", "Realtek(R) Audio")]
    [DataRow("Speakers", "Speakers")]
    [DataRow("(Odd)", "(Odd)")]
    [DataRow("Speakers ()", "Speakers ()")]
    [DataRow("Speakers (Unclosed", "Speakers (Unclosed")]
    public void AdapterPartTakesTheBracketedAdapterName(string friendly, string expected)
    {
        Assert.AreEqual(expected, EndpointModelBuilder.AdapterPart(friendly));
    }

    // Connection state

    [TestMethod]
    public void RenderActiveIsConnected()
    {
        Assert.AreEqual(ConnectionState.Connected, TargetOf(Machine(EndpointState.Active)).Connection);
    }

    [TestMethod]
    public void RenderUnpluggedIsDisconnected()
    {
        Assert.AreEqual(ConnectionState.Disconnected, TargetOf(Machine(EndpointState.Unplugged, EndpointState.Unplugged)).Connection);
    }

    [TestMethod]
    public void OnlyNotPresentEndpointsIsDisconnected()
    {
        Assert.AreEqual(ConnectionState.Disconnected, TargetOf(Machine(EndpointState.NotPresent, EndpointState.NotPresent)).Connection);
    }

    [TestMethod]
    public void NoEndpointsIsUnknown()
    {
        Assert.AreEqual(ConnectionState.Unknown, EndpointModelBuilder.DeriveConnection(Array.Empty<AudioEndpoint>()));
    }

    [TestMethod]
    public void RenderIsPreferredOverCapture()
    {
        // Render UNPLUGGED with capture ACTIVE: the render endpoint decides.
        Assert.AreEqual(ConnectionState.Disconnected, TargetOf(Machine(EndpointState.Unplugged, EndpointState.Active)).Connection);

        // Render ACTIVE with capture NOTPRESENT.
        Assert.AreEqual(ConnectionState.Connected, TargetOf(Machine(EndpointState.Active, EndpointState.NotPresent)).Connection);
    }

    [TestMethod]
    public void AVanishingCaptureEndpointDoesNotChangeARenderDerivedState()
    {
        foreach (EndpointState render in new[] { EndpointState.Active, EndpointState.Unplugged, EndpointState.NotPresent })
        {
            ConnectionState withCapture = TargetOf(Machine(render, EndpointState.Active)).Connection;
            ConnectionState captureNotPresent = TargetOf(Machine(render, EndpointState.NotPresent)).Connection;
            ConnectionState captureGone = TargetOf(Machine(render, airPodsCapture: null)).Connection;

            Assert.AreEqual(withCapture, captureNotPresent, "render " + render);
            Assert.AreEqual(withCapture, captureGone, "render " + render);
        }
    }

    [TestMethod]
    public void CaptureDecidesOnlyWhenThereIsNoRenderEndpoint()
    {
        AudioEndpoint capture = AirPodsCapture(EndpointState.Active).Endpoint;
        Assert.AreEqual(ConnectionState.Connected, EndpointModelBuilder.DeriveConnection(new[] { capture }));

        AudioEndpoint unplugged = AirPodsCapture(EndpointState.Unplugged).Endpoint;
        Assert.AreEqual(ConnectionState.Disconnected, EndpointModelBuilder.DeriveConnection(new[] { unplugged }));
    }

    [TestMethod]
    public void DisabledRenderEndpointIsUnknown()
    {
        AudioEndpoint disabled = AirPodsRender(EndpointState.Disabled).Endpoint;
        AudioEndpoint notPresent = Endpoint("r2", EndpointFlow.Render, EndpointState.NotPresent, AirPodsContainer);

        Assert.AreEqual(ConnectionState.Unknown, EndpointModelBuilder.DeriveConnection(new[] { disabled }));
        Assert.AreEqual(ConnectionState.Unknown, EndpointModelBuilder.DeriveConnection(new[] { disabled, notPresent }));
    }

    [TestMethod]
    public void UnreadableStatesAreNotEvidence()
    {
        AudioEndpoint unreadableRender = Endpoint("r", EndpointFlow.Render, 0, AirPodsContainer);
        AudioEndpoint oddRender = Endpoint("r2", EndpointFlow.Render, (EndpointState)0x10, AirPodsContainer);
        AudioEndpoint capture = AirPodsCapture(EndpointState.Active).Endpoint;

        Assert.AreEqual(ConnectionState.Unknown, EndpointModelBuilder.DeriveConnection(new[] { unreadableRender, oddRender }));
        Assert.AreEqual(ConnectionState.Connected, EndpointModelBuilder.DeriveConnection(new[] { unreadableRender, capture }));
    }

    [TestMethod]
    public void TheBuilderNeverReportsATransitionState()
    {
        foreach (EndpointState render in new[] { EndpointState.Active, EndpointState.Unplugged, EndpointState.Disabled, EndpointState.NotPresent })
        {
            ConnectionState state = TargetOf(Machine(render)).Connection;
            Assert.AreNotEqual(ConnectionState.Connecting, state);
            Assert.AreNotEqual(ConnectionState.Disconnecting, state);
        }
    }

    [TestMethod]
    public void InternalAdaptersReadAsThePcContainerGroup()
    {
        DeviceModel pc = Build(Machine()).Snapshot.AllGroups.Single(g => g.ContainerId == PcContainer);

        Assert.AreEqual("Realtek(R) Audio", pc.DisplayName);
        Assert.AreEqual(ConnectionState.Connected, pc.Connection);
    }

    // Target resolution

    [TestMethod]
    public void NameMatchFindsTheAirPods()
    {
        EndpointModel model = Build(Machine());

        Assert.IsNotNull(model.Snapshot.Target);
        Assert.AreEqual(AirPodsContainer, model.Snapshot.Target.ContainerId);
        Assert.AreEqual(TargetResolution.NameMatch, model.Resolution);
    }

    [TestMethod]
    public void NameMatchIgnoresCase()
    {
        Assert.AreEqual(AirPodsContainer, Build(Machine(), match: "airpods pro").Snapshot.Target?.ContainerId);
    }

    [TestMethod]
    public void AStraightApostropheDoesNotMatchTheCurlyOne()
    {
        Assert.IsNull(Build(Machine(), match: "Jonathan's").Snapshot.Target);
        Assert.AreEqual(AirPodsContainer, Build(Machine(), match: "Jonathan’s").Snapshot.Target?.ContainerId);
    }

    [TestMethod]
    public void WildcardCharactersAreMatchedLiterally()
    {
        Assert.IsNull(Build(Machine(), match: "*AirPods*").Snapshot.Target);
        Assert.IsNull(Build(Machine(), match: "[AirPods]").Snapshot.Target);
    }

    [TestMethod]
    public void AnEndpointFriendlyNameCanMatchWhenTheDisplayNameDoesNot()
    {
        Assert.AreEqual(AirPodsContainer, Build(Machine(), match: "Headphones (Jonathan").Snapshot.Target?.ContainerId);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void ABlankMatchStringMatchesNothing(string? match)
    {
        EndpointModel model = Build(Machine(), match: match);

        Assert.IsNull(model.Snapshot.Target);
        Assert.AreEqual(TargetResolution.NotFound, model.Resolution);
    }

    [TestMethod]
    public void PinnedContainerWinsOverTheName()
    {
        EndpointModel model = Build(Machine(), match: "Seiren", pinned: AirPodsContainer);

        Assert.AreEqual(AirPodsContainer, model.Snapshot.Target?.ContainerId);
        Assert.AreEqual(TargetResolution.Pinned, model.Resolution);
    }

    [TestMethod]
    public void PinnedContainerIsUsedWhenTheDeviceWasRenamed()
    {
        List<EndpointReading> renamed = Machine()
            .Select(r => r.Endpoint.ContainerId == AirPodsContainer
                ? new EndpointReading(r.Endpoint with { FriendlyName = "Headphones (Buds)" }, "Buds")
                : r)
            .ToList();

        Assert.IsNull(Build(renamed).Snapshot.Target);
        EndpointModel pinned = Build(renamed, pinned: AirPodsContainer);
        Assert.AreEqual(AirPodsContainer, pinned.Snapshot.Target?.ContainerId);
        Assert.AreEqual("Buds", pinned.Snapshot.Target?.DisplayName);
    }

    [TestMethod]
    public void AnAbsentPinnedContainerChoosesNoOtherDevice()
    {
        EndpointModel model = Build(Machine(), pinned: new Guid("0d5c1e2f-3a4b-4c5d-8e6f-708192a3b4c5"));

        Assert.IsNull(model.Snapshot.Target, "The AirPods match by name, but they are not the pinned device.");
        Assert.AreEqual(TargetResolution.PinnedAbsent, model.Resolution);
        Assert.HasCount(3, model.Snapshot.AllGroups);
    }

    // A container id is seeded from the Bluetooth address, so another container is another device. With the
    // pinned AirPods absent, a phone whose name contains the match string must not become the target.
    [TestMethod]
    public void AnotherMatchingDeviceIsNeverChosenWhileThePinnedOneIsAbsent()
    {
        var phone = new EndpointReading(
            new AudioEndpoint("{0.0.1.00000000}.{44444444-5555-4666-8777-888888888802}", EndpointFlow.Capture, EndpointState.Active,
                "Headset (Jonathan’s iPhone)", IPhoneContainer),
            "Jonathan’s iPhone");
        var otherBuds = new EndpointReading(
            new AudioEndpoint("{0.0.0.00000000}.{55555555-6666-4777-8888-999999999901}", EndpointFlow.Render, EndpointState.Active,
                "Headphones (Jonathan’s AirPods)", new Guid("7e2a9c40-1b3d-4f5e-8a6b-0c1d2e3f4a5b")),
            "Jonathan’s AirPods");
        List<EndpointReading> airPodsAbsent = Machine().Where(r => r.Endpoint.ContainerId != AirPodsContainer).ToList();
        airPodsAbsent.Add(phone);
        airPodsAbsent.Add(otherBuds);

        foreach (string match in new[] { "Jonathan", "AirPods", "iPhone" })
        {
            Assert.IsNotNull(Build(airPodsAbsent, match: match).Snapshot.Target, "Unpinned, the name matches another device: " + match);

            EndpointModel pinned = Build(airPodsAbsent, match: match, pinned: AirPodsContainer);
            Assert.IsNull(pinned.Snapshot.Target, match);
            Assert.AreEqual(TargetResolution.PinnedAbsent, pinned.Resolution, match);
        }

        // The pinned AirPods coming back are chosen again, ahead of the other matching devices.
        airPodsAbsent.Add(AirPodsRender(EndpointState.Unplugged));
        EndpointModel back = Build(airPodsAbsent, match: "Jonathan", pinned: AirPodsContainer);
        Assert.AreEqual(AirPodsContainer, back.Snapshot.Target?.ContainerId);
        Assert.AreEqual(TargetResolution.Pinned, back.Resolution);
    }

    [TestMethod]
    public void AnInvalidPinnedContainerFallsBackToTheName()
    {
        foreach (Guid pinned in new[] { Guid.Empty, PcContainer })
        {
            EndpointModel model = Build(Machine(), pinned: pinned);

            Assert.AreEqual(AirPodsContainer, model.Snapshot.Target?.ContainerId);
            Assert.AreEqual(TargetResolution.NameMatch, model.Resolution);
        }
    }

    [TestMethod]
    public void ThePcContainerIsNeverATargetEvenWhenPinnedOrMatched()
    {
        EndpointModel pinned = Build(Machine(), match: "no such device", pinned: PcContainer);
        Assert.IsNull(pinned.Snapshot.Target);

        EndpointModel matched = Build(Machine(), match: "Realtek");
        Assert.IsNull(matched.Snapshot.Target);
    }

    [TestMethod]
    public void AnUnreadableContainerIsNeverATarget()
    {
        var readings = new List<EndpointReading>
        {
            new(Endpoint("{0.0.0.00000000}.{u}", EndpointFlow.Render, EndpointState.Active, Guid.Empty, "Headphones (AirPods)"), "AirPods"),
        };

        EndpointModel model = Build(readings);

        Assert.HasCount(1, model.Snapshot.AllGroups);
        Assert.IsNull(model.Snapshot.Target);
        Assert.IsNull(Build(readings, pinned: Guid.Empty).Snapshot.Target);
    }

    [TestMethod]
    public void TheIPhoneIsNeverChosenForTheAirPodsMatch()
    {
        List<EndpointReading> machine = Machine();
        machine.Insert(0, IPhoneHandsFree());

        foreach (string match in new[] { "AirPods", "AirPods Pro", "Jonathan" })
        {
            EndpointModel model = Build(machine, match: match);
            Assert.AreEqual(AirPodsContainer, model.Snapshot.Target?.ContainerId, match);
        }

        // With the AirPods endpoints removed (unpaired, or the driver reinstalled), the match still never lands
        // on the phone.
        List<EndpointReading> removed = machine.Where(r => r.Endpoint.ContainerId != AirPodsContainer).ToList();
        Assert.IsNull(Build(removed).Snapshot.Target);
        EndpointModel pinned = Build(removed, pinned: AirPodsContainer);
        Assert.IsNull(pinned.Snapshot.Target);
        Assert.AreEqual(TargetResolution.PinnedAbsent, pinned.Resolution);
    }

    // The boot block disables the AirPods device nodes, and their endpoints stay enumerated as NOTPRESENT; the
    // names of a NOTPRESENT endpoint may not read (0xE000020B). A pinned device in that state is still the
    // target, disconnected, with an empty name rather than "not found".
    [TestMethod]
    public void APinnedDeviceWhoseEndpointsAreAllNotPresentAndNamelessIsStillTheTarget()
    {
        List<EndpointReading> machine = Machine().Where(r => r.Endpoint.ContainerId != AirPodsContainer).ToList();
        machine.Insert(0, IPhoneHandsFree());
        machine.Add(new EndpointReading(AirPodsRender(EndpointState.NotPresent).Endpoint with { FriendlyName = null }, InterfaceName: null));
        machine.Add(new EndpointReading(AirPodsCapture(EndpointState.NotPresent).Endpoint with { FriendlyName = null }, InterfaceName: null));

        EndpointModel pinned = Build(machine, pinned: AirPodsContainer);

        Assert.AreEqual(TargetResolution.Pinned, pinned.Resolution);
        Assert.AreEqual(AirPodsContainer, pinned.Snapshot.Target?.ContainerId);
        Assert.AreEqual(ConnectionState.Disconnected, pinned.Snapshot.Target?.Connection);
        Assert.AreEqual("", pinned.Snapshot.Target?.DisplayName);

        // Without a pin nothing names the AirPods, so the name match finds nothing, and never the phone.
        EndpointModel unpinned = Build(machine);
        Assert.IsNull(unpinned.Snapshot.Target);
        Assert.AreEqual(TargetResolution.NotFound, unpinned.Resolution);
    }

    [TestMethod]
    public void TheFirstMatchingGroupIsStableRegardlessOfEnumerationOrder()
    {
        var second = new Guid("11111111-0000-4000-8000-000000000002");
        var readings = new List<EndpointReading>
        {
            new(Endpoint("b", EndpointFlow.Render, EndpointState.Active, second, "Headphones (Zed AirPods)"), "Zed AirPods"),
            AirPodsRender(EndpointState.Unplugged),
        };

        Guid? forward = Build(readings).Snapshot.Target?.ContainerId;
        readings.Reverse();
        Guid? reversed = Build(readings).Snapshot.Target?.ContainerId;

        Assert.AreEqual(AirPodsContainer, forward);
        Assert.AreEqual(forward, reversed);
    }

    [TestMethod]
    public void TheTargetIsTheSameObjectAsItsGroup()
    {
        DeviceSnapshot snapshot = Build(Machine()).Snapshot;

        Assert.IsTrue(snapshot.AllGroups.Any(g => ReferenceEquals(g, snapshot.Target)));
    }

    // Equivalence

    [TestMethod]
    public void SnapshotsOfTheSameDevicesAreEquivalentWhateverTheTimeAndOrder()
    {
        List<EndpointReading> machine = Machine();
        DeviceSnapshot first = Build(machine).Snapshot;
        machine.Reverse();
        DeviceSnapshot second = EndpointModelBuilder.Build(machine, "AirPods", Guid.Empty, Taken.AddMinutes(5)).Snapshot;

        Assert.IsTrue(EndpointModelBuilder.AreEquivalent(first, second));
    }

    [TestMethod]
    public void AStateChangeIsAMaterialChange()
    {
        DeviceSnapshot connected = Build(Machine(EndpointState.Active)).Snapshot;
        DeviceSnapshot disconnected = Build(Machine(EndpointState.Unplugged)).Snapshot;

        Assert.IsFalse(EndpointModelBuilder.AreEquivalent(connected, disconnected));
    }

    [TestMethod]
    public void ANameOrTargetChangeIsAMaterialChange()
    {
        DeviceSnapshot named = Build(Machine()).Snapshot;
        List<EndpointReading> renamed = Machine()
            .Select(r => r.Endpoint.ContainerId == MicrophoneContainer ? r with { InterfaceName = "Seiren Mini X" } : r)
            .ToList();

        Assert.IsFalse(EndpointModelBuilder.AreEquivalent(named, Build(renamed).Snapshot));
        Assert.IsFalse(EndpointModelBuilder.AreEquivalent(named, Build(Machine(), match: "Seiren").Snapshot));
    }

    [TestMethod]
    public void ACaptureEndpointVanishingIsAMaterialChangeButKeepsTheState()
    {
        DeviceSnapshot before = Build(Machine(EndpointState.Active, EndpointState.Active)).Snapshot;
        DeviceSnapshot after = Build(Machine(EndpointState.Active, airPodsCapture: null)).Snapshot;

        Assert.IsFalse(EndpointModelBuilder.AreEquivalent(before, after));
        Assert.AreEqual(before.Target?.Connection, after.Target?.Connection);
    }

    [TestMethod]
    public void NullSnapshotsCompare()
    {
        DeviceSnapshot snapshot = Build(Machine()).Snapshot;

        Assert.IsTrue(EndpointModelBuilder.AreEquivalent(null, null));
        Assert.IsFalse(EndpointModelBuilder.AreEquivalent(snapshot, null));
        Assert.IsFalse(EndpointModelBuilder.AreEquivalent(null, snapshot));
    }

    [TestMethod]
    public void AnEmptySnapshotEqualsAnEmptyBuild()
    {
        var empty = new DeviceSnapshot(null, Array.Empty<DeviceModel>(), Taken) { ReadStatus = SnapshotReadStatus.Ok, Resolution = TargetResolution.NotFound };

        Assert.IsTrue(EndpointModelBuilder.AreEquivalent(empty, Build(Array.Empty<EndpointReading>()).Snapshot));
        Assert.IsFalse(EndpointModelBuilder.AreEquivalent(empty, Build(Machine()).Snapshot));
    }

    // The empty placeholder (nothing read) and an empty read (nothing found) look alike, but must not compare equal,
    // or the change from "not read" to "not found" would never be raised.
    [TestMethod]
    public void AReadStatusOrResolutionChangeIsAMaterialChangeButTheSequenceIsNot()
    {
        var placeholder = new DeviceSnapshot(null, Array.Empty<DeviceModel>(), Taken);
        DeviceSnapshot emptyRead = Build(Array.Empty<EndpointReading>()).Snapshot;
        DeviceSnapshot read = Build(Machine()).Snapshot;

        Assert.IsFalse(EndpointModelBuilder.AreEquivalent(placeholder, emptyRead));
        Assert.IsFalse(EndpointModelBuilder.AreEquivalent(read, read with { ReadStatus = SnapshotReadStatus.Failed, Resolution = TargetResolution.ReadFailed }));
        Assert.IsFalse(EndpointModelBuilder.AreEquivalent(read, read with { Resolution = TargetResolution.Pinned }));
        Assert.IsTrue(EndpointModelBuilder.AreEquivalent(read, read with { Sequence = 7, TakenUtc = Taken.AddMinutes(1) }));
    }

    private static DeviceModel TargetOf(IReadOnlyList<EndpointReading> readings)
    {
        DeviceModel? target = Build(readings).Snapshot.Target;
        Assert.IsNotNull(target);
        return target;
    }
}
