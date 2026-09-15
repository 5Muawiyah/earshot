using System.Text.Json;
using Earshot.Audio;
using Earshot.Audio.Connect;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase2.EndpointFixtures;
using static Earshot.Tests.Phase3.ConnectFixtures;

namespace Earshot.Tests.Phase3;

// diag connect, disconnect and ks send live requests, so no test runs them. These tests cover their argument
// handling and the evidence they write, built from plain data.
[TestClass]
public sealed class DiagConnectTests
{
    private static readonly string[] NoArgs = [];

    [TestMethod]
    [DataRow("reconnect", "src", (int)ConnectAction.Connect, (int)FilterChoice.Src)]
    [DataRow("reconnect", "wave", (int)ConnectAction.Connect, (int)FilterChoice.Wave)]
    [DataRow("reconnect", "all", (int)ConnectAction.Connect, (int)FilterChoice.All)]
    [DataRow("disconnect", "src", (int)ConnectAction.Disconnect, (int)FilterChoice.Src)]
    [DataRow("disconnect", "wave", (int)ConnectAction.Disconnect, (int)FilterChoice.Wave)]
    [DataRow("disconnect", "all", (int)ConnectAction.Disconnect, (int)FilterChoice.All)]
    public void KsArgumentsBecomeTheRequest(string action, string filter, int expectedAction, int expectedFilters)
    {
        Assert.IsTrue(Program.TryParseDiagKs([action, filter], out Program.DiagKsArguments? parsed, out string? error), error);

        Assert.AreEqual((ConnectAction)expectedAction, parsed.Action);
        Assert.AreEqual((FilterChoice)expectedFilters, parsed.Filters);
    }

    [TestMethod]
    [DataRow()]
    [DataRow("reconnect")]
    [DataRow("Reconnect", "src")]
    [DataRow("connect", "src")]
    [DataRow("pair", "src")]
    [DataRow("reconnect", "SRC")]
    [DataRow("reconnect", "phone")]
    [DataRow("disconnect", "")]
    [DataRow("reconnect", "src", "extra")]
    public void AnythingElseIsRefused(params string[] args)
    {
        Assert.IsFalse(Program.TryParseDiagKs(args, out Program.DiagKsArguments? parsed, out string? error));

        Assert.IsNull(parsed);
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    public void EveryKsCommandLineTheDispatcherAcceptsIsUnderstood()
    {
        foreach (string action in new[] { "reconnect", "disconnect", "pair" })
        {
            foreach (string filter in new[] { "src", "wave", "all", "phone" })
            {
                bool dispatcher = Program.TryParseDiagArgs(["diag", "ks", action, filter], out Program.DiagRequest? request, out _);
                bool target = Program.TryParseDiagKs([action, filter], out _, out _);

                Assert.AreEqual(dispatcher, target, action + " " + filter);
                if (dispatcher)
                {
                    Assert.IsTrue(Program.TryParseDiagKs(request!.Args, out _, out _));
                }
            }
        }
    }

    [TestMethod]
    public void ConnectAndDisconnectTakeNoArguments()
    {
        Assert.IsTrue(Program.TryParseDiagController(NoArgs, out string? none));
        Assert.IsNull(none);
        Assert.IsFalse(Program.TryParseDiagController(["now"], out string? error));
        Assert.IsFalse(string.IsNullOrEmpty(error));
    }

    [TestMethod]
    public void TheFirstWantedStateNotificationIsTheRenderEndpoints()
    {
        DateTimeOffset at = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        RecordedNotification[] notifications =
        [
            Note(1, at, EndpointNotificationKind.StateChanged, AirPodsCaptureId, CoreAudio.DEVICE_STATE_ACTIVE),
            Note(2, at, EndpointNotificationKind.StateChanged, "{0.0.0.00000000}.{phone}", CoreAudio.DEVICE_STATE_ACTIVE),
            Note(3, at, EndpointNotificationKind.PropertyChanged, AirPodsRenderId, 0),
            Note(5, at, EndpointNotificationKind.StateChanged, AirPodsRenderId, CoreAudio.DEVICE_STATE_ACTIVE),
            Note(4, at, EndpointNotificationKind.StateChanged, AirPodsRenderId, CoreAudio.DEVICE_STATE_UNPLUGGED),
        ];

        Assert.AreEqual(5, Program.FirstWantedNotification(notifications, [AirPodsRenderId], ConnectAction.Connect)?.Sequence);
        Assert.AreEqual(4, Program.FirstWantedNotification(notifications, [AirPodsRenderId], ConnectAction.Disconnect)?.Sequence);
        Assert.IsNull(Program.FirstWantedNotification(notifications, [], ConnectAction.Connect));
    }

    [TestMethod]
    public void TheEvidenceCarriesEachFiltersHResultTimingAndTheCallbackThreads()
    {
        var time = new ManualTimeProvider();
        var monitor = new FakeDeviceMonitor(time);
        DeviceSnapshot before = monitor.Snapshot(Render(EndpointState.Unplugged), Capture(EndpointState.Unplugged));
        DateTimeOffset requested = time.GetUtcNow();
        var path = new FakeConnectPath
        {
            Filters =
            [
                new FakeFilter(SrcAdapter, Render(EndpointState.Unplugged), SOk),
                new FakeFilter(WaveAdapter, Capture(EndpointState.Unplugged), CoreAudio.E_NOTFOUND),
            ],
        };
        KsSendResult send = path.Send(AirPodsContainer, before.Target!.Endpoints, ConnectAction.Connect, FilterChoice.All);
        DeviceSnapshot after = monitor.Snapshot(Render(EndpointState.Active), Capture(EndpointState.Unplugged));
        var confirmation = new ConfirmationResult(true, ConfirmationSource.Notification, TimeSpan.FromMilliseconds(1234), after, true, 2, 0, 9, ApartmentState.STA);
        RecordedNotification[] notifications =
        [
            Note(1, requested.AddMilliseconds(1200), EndpointNotificationKind.StateChanged, AirPodsRenderId, CoreAudio.DEVICE_STATE_ACTIVE, thread: 31),
        ];
        var evidence = new Program.DiagConnectEvidence("ks", ["reconnect", "all"], ConnectAction.Connect, FilterChoice.All,
            requested.AddSeconds(-1), requested.AddSeconds(15), AirPodsContainer, before, after, null, null, send, confirmation, requested,
            notifications, [StepOutcomes.FromHResult(NotificationRecorder.RegisterStep, 0)], null);

        using JsonDocument json = JsonDocument.Parse(Program.DiagConnectJson(evidence));
        JsonElement root = json.RootElement;

        Assert.AreEqual("reconnect", root.GetProperty("action").GetString());
        Assert.AreEqual("all", root.GetProperty("filterChoice").GetString());
        Assert.IsTrue(root.GetProperty("requestedUtc").GetString()!.EndsWith("+00:00", StringComparison.Ordinal), "Timestamps are UTC.");
        Assert.AreEqual(2, root.GetProperty("filtersFound").GetArrayLength());
        Assert.AreEqual(1, root.EnumerateObject().Count(p => p.Name == "filters"), "Each property name appears once.");

        JsonElement[] sent = root.GetProperty("filters").EnumerateArray().ToArray();
        Assert.AreEqual(("src", "0x00000000", "S_OK", true), (sent[0].GetProperty("name").GetString(), sent[0].GetProperty("hr").GetString(),
            sent[0].GetProperty("hrName").GetString(), sent[0].GetProperty("accepted").GetBoolean()));
        Assert.AreEqual(("wave", "0x80070490", "E_NOTFOUND", false), (sent[1].GetProperty("name").GetString(), sent[1].GetProperty("hr").GetString(),
            sent[1].GetProperty("hrName").GetString(), sent[1].GetProperty("accepted").GetBoolean()));

        JsonElement confirmed = root.GetProperty("confirmation");
        Assert.IsTrue(confirmed.GetProperty("reached").GetBoolean());
        Assert.AreEqual(1234, confirmed.GetProperty("elapsedMilliseconds").GetInt64());
        Assert.AreEqual(9, confirmed.GetProperty("observedOnThreadId").GetInt32());
        Assert.AreEqual("STA", confirmed.GetProperty("observedOnApartment").GetString());
        Assert.AreEqual(1200, root.GetProperty("millisecondsToFirstWantedStateNotification").GetInt64());

        JsonElement note = root.GetProperty("notifications").EnumerateArray().Single();
        Assert.AreEqual(31, note.GetProperty("threadId").GetInt32());
        Assert.AreEqual("MTA", note.GetProperty("apartment").GetString());
        Assert.AreEqual("0x1", note.GetProperty("newState").GetString());
        Assert.AreEqual("Active", root.GetProperty("endpointsAfter").GetProperty("endpoints")[0].GetProperty("state").GetString());
    }

    private static RecordedNotification Note(long sequence, DateTimeOffset at, EndpointNotificationKind kind, string id, uint state, int thread = 7) =>
        new(sequence, at, kind, id, state, thread, ApartmentState.MTA);
}

// The recorder against the real audio stack, read-only: it registers a notification client and unregisters it.
// Nothing is sent, set or changed.
[TestClass]
[TestCategory("ReadOnlyHardware")]
public sealed class NotificationRecorderReadOnlyHardwareTests
{
    [TestMethod]
    public async Task TheRecorderRegistersAndUnregistersOnTheAudioWorker()
    {
        var log = new CapturingLog();
        await using var worker = new AudioWorker(log);
        var recorder = new NotificationRecorder(TimeProvider.System);

        StepOutcome registered = await worker.RunAsync(_ => recorder.Register(worker)).WaitAsync(TimeSpan.FromSeconds(30));
        if (registered.Step == AudioWorker.Steps.CreateEnumerator)
        {
            Assert.Inconclusive("No audio enumerator on this machine: " + registered.CodeName);
        }

        StepOutcome again = await worker.RunAsync(_ => recorder.Register(worker));
        StepOutcome unregistered = await worker.RunAsync(_ => recorder.Unregister(worker));
        StepOutcome twice = await worker.RunAsync(_ => recorder.Unregister(worker));

        Assert.AreEqual((NotificationRecorder.RegisterStep, "S_OK"), (registered.Step, registered.CodeName));
        Assert.AreEqual(NativeCodes.NotAttempted, again.Code, "A second registration is refused, not stacked.");
        Assert.AreEqual((NotificationRecorder.UnregisterStep, "S_OK"), (unregistered.Step, unregistered.CodeName));
        Assert.AreEqual(NativeCodes.NotAttempted, twice.Code);
        Assert.IsTrue(recorder.Entries.All(e => e.Sequence > 0));
    }
}
