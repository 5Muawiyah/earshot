using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.Audio;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase2.EndpointFixtures;

namespace Earshot.Tests.Phase2;

// probe audio and probe topology output, from synthetic readings. No Core Audio call is made.
[TestClass]
public sealed class AudioProbeTests
{
    private const string SrcAdapter =
        "{2}.\\\\?\\bthenum#{0000110b-0000-1000-8000-00805f9b34fb}_vid&0001004c_pid&2027#b&1a2b3c4d&0&5A6b7C8d9Eaf_c00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\src";

    private const string WaveAdapter =
        "{2}.\\\\?\\bthhfenum#bthhfpaudio#c&2b3c4d5e&1&97#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\wave";

    private static readonly string[] EnumerateOnly = ["enumerate"];

    private static MonitorRefresh Refresh(IReadOnlyList<EndpointReading> readings, IReadOnlyList<StepOutcome>? steps = null, bool ok = true)
    {
        EndpointModel model = EndpointModelBuilder.Build(readings, "AirPods", Guid.Empty, new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));
        return new MonitorRefresh(model.Snapshot, ok, readings, steps ?? Array.Empty<StepOutcome>(), model.Resolution);
    }

    private static StepOutcome UnreadableName() =>
        StepOutcomes.FromHResult(CoreAudioEndpointReader.FriendlyNameStep + ":" + AmdHdmiUnreadable(1).Endpoint.EndpointId, CoreAudio.ERROR_NO_SUCH_DEVINST);

    private static (ProbeContext Context, StringWriter Output) Context(string target, bool json, Func<ServiceRegistry>? services = null)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        Func<ServiceRegistry> registry = services ?? (() => throw new AssertFailedException("The writer must not build services."));
        return (new ProbeContext(target, output, json, registry), output);
    }

    [TestMethod]
    public void AudioTextListsEveryEndpointGroupedWithStateAndTarget()
    {
        (ProbeContext ctx, StringWriter output) = Context("audio", json: false);
        using (output)
        {
            Program.WriteAudioProbe(ctx, Refresh(Machine(), new[] { UnreadableName() }), new EarshotSettings());

            string text = output.ToString();
            StringAssert.Contains(text, "Endpoints: 8 (EnumAudioEndpoints eAll, DEVICE_STATEMASK_ALL)");
            StringAssert.Contains(text, "Group {1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D} \"" + AirPodsName + "\", Connected");
            StringAssert.Contains(text, "Group {00000000-0000-0000-FFFF-FFFFFFFFFFFF} (this PC, never a target)");
            StringAssert.Contains(text, "Render  Active     \"Headphones (" + AirPodsName + ")\"  interface \"" + AirPodsName + "\"");
            StringAssert.Contains(text, "id " + AirPodsRenderId + "  container {1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D}");
            StringAssert.Contains(text, "Capture Disabled   \"Stereo Mix (Realtek(R) Audio)\"");
            StringAssert.Contains(text, "(name not readable)");
            StringAssert.Contains(text, "Target: {1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D} \"" + AirPodsName + "\", Connected, matched by name");
            StringAssert.Contains(text, "Failed reads: 1");
            StringAssert.Contains(text, "ERROR_NO_SUCH_DEVINST");
            Assert.IsFalse(text.Contains((char)0x2014, StringComparison.Ordinal), "No em-dash in probe output.");
        }
    }

    [TestMethod]
    public void AudioTextSaysWhenNoTargetWasFound()
    {
        (ProbeContext ctx, StringWriter output) = Context("audio", json: false);
        using (output)
        {
            List<EndpointReading> withoutAirPods = Machine().Where(r => r.Endpoint.ContainerId != AirPodsContainer).ToList();

            Program.WriteAudioProbe(ctx, Refresh(withoutAirPods), new EarshotSettings());

            StringAssert.Contains(output.ToString(), "Target: none found");
        }
    }

    // A failed read is never reported as "none found".
    [TestMethod]
    public void TheDeviceLineSaysWhenTheDevicesCouldNotBeRead()
    {
        DeviceModel target = EndpointModelBuilder.Build(Machine(), "AirPods", Guid.Empty, DateTimeOffset.UnixEpoch).Snapshot.Target!;

        Assert.AreEqual("Target: unknown, the audio devices could not be read", Program.DeviceLine(null, TargetResolution.ReadFailed));
        Assert.AreEqual("Target: none found", Program.DeviceLine(null, TargetResolution.NotFound));
        StringAssert.EndsWith(Program.DeviceLine(target, TargetResolution.ReadFailed), ", last known, the audio devices could not be read");
        StringAssert.EndsWith(Program.DeviceLine(target, TargetResolution.NameMatch), ", matched by name");
    }

    [TestMethod]
    public void AudioProbeSaysWhenThePinnedDeviceHasNoEndpoints()
    {
        List<EndpointReading> withoutAirPods = Machine().Where(r => r.Endpoint.ContainerId != AirPodsContainer).ToList();
        EndpointModel model = EndpointModelBuilder.Build(withoutAirPods, "Seiren", AirPodsContainer, DateTimeOffset.UnixEpoch);
        var refresh = new MonitorRefresh(model.Snapshot, true, withoutAirPods, Array.Empty<StepOutcome>(), model.Resolution);
        var settings = new EarshotSettings { DeviceMatch = "Seiren", PinnedContainerId = AirPodsContainer };

        (ProbeContext text, StringWriter textOutput) = Context("audio", json: false);
        using (textOutput)
        {
            Program.WriteAudioProbe(text, refresh, settings);

            StringAssert.Contains(textOutput.ToString(), "Pinned container: {1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D}");
            StringAssert.Contains(textOutput.ToString(), "Target: none found, the pinned container has no endpoints");
        }

        (ProbeContext json, StringWriter jsonOutput) = Context("audio", json: true);
        using (jsonOutput)
        {
            Program.WriteAudioProbe(json, refresh, settings);

            using JsonDocument doc = JsonDocument.Parse(jsonOutput.ToString());
            Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("device").ValueKind);
            Assert.AreEqual("PinnedAbsent", doc.RootElement.GetProperty("resolution").GetString());
        }
    }

    [TestMethod]
    public void AudioJsonIsOneObjectWithEveryEndpoint()
    {
        (ProbeContext ctx, StringWriter output) = Context("audio", json: true);
        using (output)
        {
            var settings = new EarshotSettings { PinnedContainerId = AirPodsContainer };
            MonitorRefresh refresh = Refresh(Machine(), new[] { UnreadableName() });

            Program.WriteAudioProbe(ctx, refresh, settings);

            using JsonDocument doc = JsonDocument.Parse(output.ToString());
            JsonElement root = doc.RootElement;
            Assert.AreEqual("audio", root.GetProperty("target").GetString());
            Assert.IsTrue(root.GetProperty("enumerationOk").GetBoolean());
            Assert.AreEqual(8, root.GetProperty("endpointCount").GetInt32());
            Assert.AreEqual(AirPodsContainer, root.GetProperty("pinnedContainerId").GetGuid());
            Assert.AreEqual(3, root.GetProperty("groups").GetArrayLength());

            JsonElement airPods = root.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("containerId").GetGuid() == AirPodsContainer);
            Assert.IsTrue(airPods.GetProperty("targetCapable").GetBoolean());
            Assert.AreEqual(AirPodsName, airPods.GetProperty("displayName").GetString());
            JsonElement render = airPods.GetProperty("endpoints")[0];
            Assert.AreEqual(AirPodsRenderId, render.GetProperty("id").GetString());
            Assert.AreEqual("Render", render.GetProperty("flow").GetString());
            Assert.AreEqual("Active", render.GetProperty("state").GetString());
            Assert.AreEqual(AirPodsName, render.GetProperty("interfaceName").GetString());

            JsonElement pc = root.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("containerId").GetGuid() == PcContainer);
            Assert.IsFalse(pc.GetProperty("targetCapable").GetBoolean());
            Assert.IsTrue(pc.GetProperty("endpoints").EnumerateArray().Any(e => e.GetProperty("friendlyName").ValueKind == JsonValueKind.Null));

            JsonElement device = root.GetProperty("device");
            Assert.AreEqual(AirPodsContainer, device.GetProperty("containerId").GetGuid());
            Assert.AreEqual("Connected", device.GetProperty("connection").GetString());
            Assert.AreEqual("NameMatch", device.GetProperty("chosenBy").GetString());
            Assert.AreEqual("NameMatch", root.GetProperty("resolution").GetString());

            JsonElement step = root.GetProperty("failedSteps")[0];
            Assert.AreEqual("ERROR_NO_SUCH_DEVINST", step.GetProperty("codeName").GetString());
            Assert.AreEqual("0xE000020B", step.GetProperty("code").GetString());
        }
    }

    [TestMethod]
    public async Task ProbeAudioRefreshesThroughTheRegistryMonitorWithoutSubscribing()
    {
        var log = new CapturingLog();
        var settings = new FakeSettingsStore();
        var source = new FakeEndpointSource();
        source.SetReadings(Machine(EndpointState.Unplugged, EndpointState.Unplugged));
        await using var worker = new AudioWorker(log);
        using var monitor = new CoreAudioDeviceMonitor(worker, source, settings, log, a => a());
        var registry = new ServiceRegistry(log, settings, a => a(), safeMode: true) { Worker = worker, Monitor = monitor };
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exit = Program.RunProbe(new Program.ProbeRequest(["audio"], Json: false, OutPath: null), output, () => registry);

        Assert.AreEqual(ExitCodes.Ok, exit);
        CollectionAssert.AreEqual(EnumerateOnly, source.Calls.ToArray());
        StringAssert.Contains(output.ToString(), "== audio ==");
        StringAssert.Contains(output.ToString(), "\"" + AirPodsName + "\", Disconnected, matched by name");
    }

    [TestMethod]
    public async Task ProbeAudioReportsAFailedEnumerationWithANonZeroExitCode()
    {
        var log = new CapturingLog();
        var settings = new FakeSettingsStore();
        var source = new FakeEndpointSource { EnumerationFailure = StepOutcomes.FromHResult(AudioWorker.Steps.CreateEnumerator, unchecked((int)0x80040154)) };
        await using var worker = new AudioWorker(log);
        using var monitor = new CoreAudioDeviceMonitor(worker, source, settings, log, a => a());
        var registry = new ServiceRegistry(log, settings, a => a(), safeMode: true) { Worker = worker, Monitor = monitor };
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exit = Program.RunProbe(new Program.ProbeRequest(["audio"], Json: true, OutPath: null), output, () => registry);

        Assert.AreEqual(ExitCodes.OsError, exit);
        using JsonDocument doc = JsonDocument.Parse(output.ToString());
        Assert.IsFalse(doc.RootElement.GetProperty("enumerationOk").GetBoolean());
        Assert.AreEqual("REGDB_E_CLASSNOTREG", doc.RootElement.GetProperty("failedSteps")[0].GetProperty("codeName").GetString());
    }

    [TestMethod]
    public void ProbeAudioAndTopologyWithoutTheAudioServicesAreUnavailable()
    {
        var log = new CapturingLog();
        var registry = new ServiceRegistry(log, new FakeSettingsStore(), a => a(), safeMode: true);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exit = Program.RunProbe(new Program.ProbeRequest(["audio", "topology"], Json: false, OutPath: null), output, () => registry);

        Assert.AreEqual(ExitCodes.Unavailable, exit);
        StringAssert.Contains(output.ToString(), "The audio device monitor is not part of this build.");
        StringAssert.Contains(output.ToString(), "The audio worker is not part of this build.");
    }

    [TestMethod]
    public async Task ProbeTopologyWithNoTargetWalksNothingAndSaysSoInItsExitCode()
    {
        var log = new CapturingLog();
        var settings = new FakeSettingsStore();
        settings.Update(s => s.DeviceMatch = "No such device");
        var source = new FakeEndpointSource();
        source.SetReadings(Machine());
        await using var worker = new AudioWorker(log);
        using var monitor = new CoreAudioDeviceMonitor(worker, source, settings, log, a => a());
        var registry = new ServiceRegistry(log, settings, a => a(), safeMode: true) { Worker = worker, Monitor = monitor };
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        int exit = Program.RunProbe(new Program.ProbeRequest(["topology"], Json: true, OutPath: null), output, () => registry);

        Assert.AreEqual(ExitCodes.Unavailable, exit, "Nothing was proved, so the exit code is not a pass.");
        using JsonDocument doc = JsonDocument.Parse(output.ToString());
        Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("device").ValueKind);
        Assert.AreEqual(0, doc.RootElement.GetProperty("adapters").GetArrayLength());
        Assert.AreEqual(0, doc.RootElement.GetProperty("requestsSent").GetArrayLength());
    }

    [TestMethod]
    public void TopologyTextShowsEachAdapterItsGuardAndThePinCount()
    {
        (ProbeContext ctx, StringWriter output) = Context("topology", json: false);
        using (output)
        {
            Program.WriteTopologyProbe(ctx, SyntheticTopology());

            string text = output.ToString();
            StringAssert.Contains(text, "Adapters (kernel streaming filters): 3");
            StringAssert.Contains(text, "Adapter " + SrcAdapter);
            StringAssert.Contains(text, "from Render endpoint " + AirPodsRenderId + " (Active)");
            StringAssert.Contains(text, "KSPROPERTY_PIN_CTYPES: 2 pins");
            StringAssert.Contains(text, "Adapter " + WaveAdapter);
            StringAssert.Contains(text, "KSPROPERTY_PIN_CTYPES: 4 pins");
            StringAssert.Contains(text, "guard: not passed");
            StringAssert.Contains(text, "IKsControl: not activated");
            StringAssert.Contains(text, "Requests sent: KSPROPSETID_Pin KSPROPERTY_PIN_CTYPES Get only");
            StringAssert.Contains(text, "is not the target");
        }
    }

    [TestMethod]
    public void TopologyJsonIsOneObjectPerAdapter()
    {
        (ProbeContext ctx, StringWriter output) = Context("topology", json: true);
        using (output)
        {
            Program.WriteTopologyProbe(ctx, SyntheticTopology());

            using JsonDocument doc = JsonDocument.Parse(output.ToString());
            JsonElement root = doc.RootElement;
            Assert.AreEqual("topology", root.GetProperty("target").GetString());
            JsonElement[] adapters = root.GetProperty("adapters").EnumerateArray().ToArray();
            Assert.HasCount(3, adapters);
            Assert.AreEqual(SrcAdapter, adapters[0].GetProperty("adapterId").GetString());
            Assert.IsTrue(adapters[0].GetProperty("guardPassed").GetBoolean());
            Assert.IsTrue(adapters[0].GetProperty("ksControlActivated").GetBoolean());
            Assert.AreEqual(2u, adapters[0].GetProperty("pinCount").GetUInt32());
            Assert.AreEqual("Render", adapters[0].GetProperty("fromEndpoints")[0].GetProperty("flow").GetString());
            Assert.AreEqual(4u, adapters[1].GetProperty("pinCount").GetUInt32());
            Assert.IsFalse(adapters[2].GetProperty("guardPassed").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, adapters[2].GetProperty("pinCount").ValueKind);
            Assert.AreEqual(IPhoneContainer, adapters[2].GetProperty("containerId").GetGuid());
            Assert.AreEqual("KSPROPSETID_Pin KSPROPERTY_PIN_CTYPES Get", root.GetProperty("requestsSent")[0].GetString());
        }
    }

    // The A2DP and Hands-Free filters of the AirPods, plus a filter whose container is the phone's, which the guard refuses.
    private static Program.TopologyReport SyntheticTopology()
    {
        DeviceSnapshot snapshot = EndpointModelBuilder.Build(Machine(), "AirPods", Guid.Empty, DateTimeOffset.UnixEpoch).Snapshot;
        DeviceModel target = snapshot.Target!;
        AudioEndpoint render = target.Endpoints.Single(e => e.Flow == EndpointFlow.Render);
        AudioEndpoint capture = target.Endpoints.Single(e => e.Flow == EndpointFlow.Capture);

        var src = new FilterVisit(new AdapterPath(SrcAdapter, new[] { render }), EndpointState.Active, AirPodsContainer, true, true,
            new[] { StepOutcomes.FromHResult(TopologyWalk.PinCountStep + ":" + SrcAdapter, 0) });
        var wave = new FilterVisit(new AdapterPath(WaveAdapter, new[] { capture }), EndpointState.Active, AirPodsContainer, true, true,
            new[] { StepOutcomes.FromHResult(TopologyWalk.PinCountStep + ":" + WaveAdapter, 0) });
        StepOutcome refused = StepOutcomes.NotAttempted(TopologyWalk.GuardStep + ":phone",
            "The adapter container " + TopologyWalk.Format(IPhoneContainer) + " is not the target " + TopologyWalk.Format(AirPodsContainer) + ".");
        var phone = new FilterVisit(new AdapterPath("{2}.\\\\?\\bthhfenum#bthhfpaudio#c&4d5e6f7&0&97#{6994ad04-93ef-11d0-a3cc-00a0c9223196}\\wave", new[] { capture }),
            EndpointState.Active, IPhoneContainer, false, false, new[] { refused });

        return new Program.TopologyReport(true, target, TargetResolution.NameMatch,
            new[] { new Program.TopologyAdapterReport(src, 2), new Program.TopologyAdapterReport(wave, 4), new Program.TopologyAdapterReport(phone, null) },
            new[] { refused });
    }
}
