using Earshot.Contracts;
using Earshot.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Streaming;

// What the coordinator asks the tray to draw, item by item and in order. A list of paired devices is driven with
// none, then one, then many: an empty list is an ordinary answer with sentences of its own, not an error and not a
// blank menu.
[TestClass]
public sealed class StreamingMenuModelTests
{
    private static StreamingDevice Device(string id, string name) => new(id, name, Guid.NewGuid());

    private static StreamingCoordinator Make(FakeStreamingPlatform fake, StreamingSettings? settings = null) =>
        new(fake, new ManualBusyGate(), settings ?? StreamingSettings.Default, _ => false, new TestTimeProvider());

    private static string[] Describe(StreamingMenuModel menu) =>
        menu.Items.Select(i => (i.Enabled ? "[x] " : "[ ] ") + i.Text + " <" + i.Command + ">").ToArray();

    [TestMethod]
    public void BeforeAnyReadThereIsOnlyRefresh()
    {
        using StreamingCoordinator coordinator = Make(new FakeStreamingPlatform());

        Assert.IsTrue(coordinator.Menu.ParentEnabled);
        Assert.AreEqual("Play from a phone", coordinator.Menu.ParentText);
        CollectionAssert.AreEqual(Sequence.Of("[x] Refresh the list <Refresh>"), Describe(coordinator.Menu));
    }

    [TestMethod]
    public async Task WhileTheFirstReadRunsItSaysItIsLooking()
    {
        var fake = new FakeStreamingPlatform();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<StreamingDiscovery>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.NextDiscovery(_ =>
        {
            entered.TrySetResult();
            return answer.Task;
        });
        using StreamingCoordinator coordinator = Make(fake);

        Task<StreamingDiscovery> read = coordinator.RefreshAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(Sequence.Of("[ ] Looking for devices. <None>", "[x] Refresh the list <Refresh>"), Describe(coordinator.Menu));

        answer.SetResult(FakeStreamingPlatform.Found(Device("phone", "Test Phone")));
        await read.WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(Sequence.Of("[x] Test Phone <Play>", "[x] Refresh the list <Refresh>"), Describe(coordinator.Menu));
    }

    [TestMethod]
    public async Task ALaterReadKeepsShowingTheListItAlreadyHas()
    {
        var fake = new FakeStreamingPlatform();
        fake.NextDiscovery(FakeStreamingPlatform.Found(Device("phone", "Test Phone")));
        using StreamingCoordinator coordinator = Make(fake);
        await coordinator.RefreshAsync(CancellationToken.None);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<StreamingDiscovery>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.NextDiscovery(_ =>
        {
            entered.TrySetResult();
            return answer.Task;
        });
        Task<StreamingDiscovery> read = coordinator.RefreshAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        CollectionAssert.AreEqual(Sequence.Of("[x] Test Phone <Play>", "[x] Refresh the list <Refresh>"), Describe(coordinator.Menu));

        answer.SetResult(FakeStreamingPlatform.Found());
        await read.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task NoDevicesIsAnOrdinaryAnswerThatSendsTheOwnerToWindowsSettings()
    {
        var fake = new FakeStreamingPlatform();
        fake.NextDiscovery(FakeStreamingPlatform.Found());
        using StreamingCoordinator coordinator = Make(fake);

        StreamingDiscovery discovery = await coordinator.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(StreamingDiscoveryStatus.Ok, discovery.Status);
        Assert.AreEqual(0, discovery.Devices.Count);
        CollectionAssert.AreEqual(
            Sequence.Of(
                "[ ] No paired device can send audio to this PC. <None>",
                "[ ] Pair the phone in Windows Settings first. <None>",
                "[x] Refresh the list <Refresh>"),
            Describe(coordinator.Menu));
    }

    [TestMethod]
    public async Task OneDeviceAndManyDevicesKeepTheOrderWindowsGave()
    {
        var fake = new FakeStreamingPlatform();
        fake.NextDiscovery(FakeStreamingPlatform.Found(Device("one", "Only Phone")));
        fake.NextDiscovery(FakeStreamingPlatform.Found(Device("c", "Console"), Device("a", "Phone A"), Device("b", "Phone B")));
        using StreamingCoordinator coordinator = Make(fake);

        await coordinator.RefreshAsync(CancellationToken.None);
        CollectionAssert.AreEqual(Sequence.Of("[x] Only Phone <Play>", "[x] Refresh the list <Refresh>"), Describe(coordinator.Menu));

        await coordinator.RefreshAsync(CancellationToken.None);
        CollectionAssert.AreEqual(
            Sequence.Of("[x] Console <Play>", "[x] Phone A <Play>", "[x] Phone B <Play>", "[x] Refresh the list <Refresh>"),
            Describe(coordinator.Menu));
    }

    [TestMethod]
    public async Task AFailedReadSaysSoAndKeepsRefresh()
    {
        var fake = new FakeStreamingPlatform();
        fake.NextDiscovery(FakeStreamingPlatform.ListFailed(unchecked((int)0x80070005)));
        using StreamingCoordinator coordinator = Make(fake);

        await coordinator.RefreshAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            Sequence.Of("[ ] Could not read the list of paired devices. <None>", "[x] Refresh the list <Refresh>"),
            Describe(coordinator.Menu));
    }

    [TestMethod]
    public async Task TheDeviceInUseIsTickedAndStopSitsAboveRefresh()
    {
        var fake = new FakeStreamingPlatform();
        fake.NextDiscovery(FakeStreamingPlatform.Found(Device("a", "Phone A"), Device("b", "Phone B")));
        using StreamingCoordinator coordinator = Make(fake);
        await coordinator.RefreshAsync(CancellationToken.None);

        await coordinator.StartPlayingAsync("b", CancellationToken.None);

        // The device just played from is the one remembered, so it leads the list from now on.
        CollectionAssert.AreEqual(
            Sequence.Of("[x] Phone B <Play>", "[x] Phone A <Play>", "[x] Stop playing from Phone B <Stop>", "[x] Refresh the list <Refresh>"),
            Describe(coordinator.Menu));
        StreamingMenuItem ticked = coordinator.Menu.Items.Single(i => i.Checked);
        Assert.AreEqual("b", ticked.DeviceId);
        Assert.AreEqual("b", coordinator.Menu.Items.Single(i => i.Command == StreamingMenuCommand.Stop).DeviceId);
        Assert.IsNull(coordinator.Menu.Items[^1].DeviceId);
    }

    [TestMethod]
    public async Task TheDeviceLastPlayedFromComesFirst()
    {
        var fake = new FakeStreamingPlatform();
        fake.NextDiscovery(FakeStreamingPlatform.Found(Device("a", "Phone A"), Device("b", "Phone B"), Device("c", "Phone C")));
        using StreamingCoordinator remembered = Make(fake, StreamingSettings.Default with { LastDeviceKey = StreamingLog.Key("c") });
        await remembered.RefreshAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            Sequence.Of("Phone C", "Phone A", "Phone B"),
            remembered.Menu.Items.Where(i => i.Command == StreamingMenuCommand.Play).Select(i => i.Text).ToArray());

        // And a successful start is remembered by the coordinator itself, without waiting for the settings.
        await remembered.StartPlayingAsync("b", CancellationToken.None);
        remembered.StopPlaying();
        CollectionAssert.AreEqual(
            Sequence.Of("Phone B", "Phone A", "Phone C"),
            remembered.Menu.Items.Where(i => i.Command == StreamingMenuCommand.Play).Select(i => i.Text).ToArray());
    }

    // A device name is whatever the phone's owner typed. It reaches the menu and the sentences cleaned: no control
    // characters, a bounded length, and something to call a device Windows gave no name for.
    [TestMethod]
    public async Task ADeviceNameIsCleanedBeforeItIsShown()
    {
        var fake = new FakeStreamingPlatform();
        string longName = new string('n', 200);
        fake.NextDiscovery(FakeStreamingPlatform.Found(Device("bell", "Pho" + (char)7 + "ne\r\nTwo"), Device("long", longName), Device("blank", "  ")));
        using StreamingCoordinator coordinator = Make(fake);
        await coordinator.RefreshAsync(CancellationToken.None);

        string[] names = coordinator.Menu.Items.Where(i => i.Command == StreamingMenuCommand.Play).Select(i => i.Text).ToArray();

        Assert.AreEqual("PhoneTwo", names[0]);
        Assert.AreEqual(new string('n', 40) + "...", names[1]);
        Assert.AreEqual("The device", names[2]);

        await coordinator.StartPlayingAsync("long", CancellationToken.None);
        Assert.IsFalse(coordinator.StatusLine.Contains(longName, StringComparison.Ordinal));
        Assert.IsFalse(coordinator.Menu.Items.Any(i => i.Text.Any(char.IsControl)));
    }

    [TestMethod]
    public void TheTooltipLineGivesWayNeverTheFirstLine()
    {
        string tooltip = "Earshot: Test AirPods - connected";
        string line = "Test Phone is playing through this PC.";

        Assert.AreEqual(tooltip, StreamingCopy.TooltipWith(tooltip, "", 127));
        Assert.AreEqual(tooltip + "\n" + line, StreamingCopy.TooltipWith(tooltip, line, 127));

        string cut = StreamingCopy.TooltipWith(tooltip, line, 60);
        Assert.IsTrue(cut.Length <= 60, cut.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsTrue(cut.StartsWith(tooltip + "\n", StringComparison.Ordinal));
        Assert.IsTrue(cut.EndsWith("...", StringComparison.Ordinal));

        // With no useful room left the line is dropped whole rather than shown as a stub.
        Assert.AreEqual(tooltip, StreamingCopy.TooltipWith(tooltip, line, tooltip.Length + 8));

        string longest = new string('t', 127);
        Assert.AreEqual(longest, StreamingCopy.TooltipWith(longest, line, 127));
    }

    [TestMethod]
    public void AStepIsDescribedWithItsCodeAndNeverADeviceId()
    {
        string described = StreamingLog.Describe(new StepOutcome("streaming-open", false, unchecked((int)0x8007048F), "0x8007048F", "UnknownFailure"));

        Assert.AreEqual("streaming-open: 0x8007048F (0x8007048F) UnknownFailure", described);
        Assert.AreEqual(12, StreamingLog.Key(StreamingCoordinatorTests.PlaceholderId).Length);
        Assert.AreEqual(StreamingLog.Key("x"), StreamingLog.Key("x"));
        Assert.AreNotEqual(StreamingLog.Key("x"), StreamingLog.Key("y"));
        Assert.IsFalse(StreamingLog.Key(StreamingCoordinatorTests.PlaceholderId).Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase));
    }
}
