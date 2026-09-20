using Earshot.Contracts;
using Earshot.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Streaming;

// A release Windows does not confirm. Every way the coordinator lets go of a connection is driven here with a release
// that fails, because that is exactly where "nothing stays enabled that the menu does not show" can break: the raw
// code must reach the log, the owner must be told once that the phone may still be connected, and the menu must go on
// showing a way to let go rather than claim it happened.
[TestClass]
public sealed class StreamingReleaseFailureTests
{
    private const string Name = "Test Phone";
    private const string MayStill = "Test Phone may still be connected to this PC. See the log.";

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static StreamingDevice Device(string id, string name = Name) => new(id, name, Guid.NewGuid());

    private static StreamingCoordinator Make(FakeStreamingPlatform fake, CapturingLog log) =>
        new(fake, new ManualBusyGate(), StreamingSettings.Default, _ => false, new TestTimeProvider(), log);

    private static async Task<StreamingCoordinator> MakeWithListAsync(FakeStreamingPlatform fake, CapturingLog log, params StreamingDevice[] devices)
    {
        fake.NextDiscovery(FakeStreamingPlatform.Found(devices));
        StreamingCoordinator coordinator = Make(fake, log);
        await coordinator.RefreshAsync(CancellationToken.None);
        return coordinator;
    }

    // The raw HRESULT by number and by name, the exception type, and the device by its key and never by its id.
    private static void AssertTheFailedReleaseIsInTheLog(CapturingLog log, string deviceId)
    {
        LogEntry[] warnings = log.Entries.Where(e => e.Level == LogLevel.Warn && e.Message.Contains("0x8000FFFF", StringComparison.Ordinal)).ToArray();
        Assert.IsTrue(warnings.Length > 0, "The failed release is not in the log: " + string.Join(" | ", log.Entries.Select(e => e.Message)));
        StringAssert.Contains(warnings[0].Message, "E_UNEXPECTED");
        StringAssert.Contains(warnings[0].Message, "COMException");
        StringAssert.Contains(warnings[0].Message, StreamingLog.Key(deviceId));

        // Only an id that is unlike any ordinary word can be searched for: "phone" is in every line of this log.
        if (deviceId == StreamingCoordinatorTests.PlaceholderId)
        {
            foreach (string part in Sequence.Of(deviceId, "BTHENUM", "PLACEHOLDER"))
            {
                Assert.IsFalse(log.Entries.Any(e => e.Message.Contains(part, StringComparison.OrdinalIgnoreCase)), "The log carries part of the device id: " + part);
            }
        }
    }

    private static void AssertTheMenuStillOffersToLetGo(StreamingCoordinator coordinator, string deviceId)
    {
        StreamingMenuItem stop = coordinator.Menu.Items.Single(i => i.Command == StreamingMenuCommand.Stop);
        Assert.AreEqual(deviceId, stop.DeviceId);
        Assert.IsTrue(stop.Enabled);
        Assert.AreEqual("Stop playing from Test Phone", stop.Text);
        Assert.AreEqual(MayStill, coordinator.TooltipLine, "While a release is unconfirmed the tooltip says so.");
    }

    // The reviewer's probe. An open that timed out, then a release that fails: the owner used to be told only that
    // the phone did not answer, and the failed release was in no log.
    [TestMethod]
    public async Task AFailedOpenWhoseReleaseAlsoFailsIsLoggedSaidOnceAndStaysInTheMenu()
    {
        var fake = new FakeStreamingPlatform();
        var log = new CapturingLog();
        fake.OpenAnswers(StreamingCoordinatorTests.PlaceholderId, StreamingOpenStatus.TimedOut);
        fake.ReleaseFails(StreamingCoordinatorTests.PlaceholderId);
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, log, Device(StreamingCoordinatorTests.PlaceholderId));

        StreamingOpenOutcome outcome = await coordinator.StartPlayingAsync(StreamingCoordinatorTests.PlaceholderId, CancellationToken.None);

        Assert.AreEqual(StreamingOpenStatus.TimedOut, outcome.Status);
        Assert.AreEqual(1, fake.CallsNamed("Release(").Count);
        AssertTheFailedReleaseIsInTheLog(log, StreamingCoordinatorTests.PlaceholderId);
        Assert.AreEqual(MayStill, coordinator.StatusLine, "What the owner most needs to know wins over the reason the open failed.");
        Assert.AreEqual(MayStill, coordinator.TakeReleaseFailureNotice());
        Assert.IsNull(coordinator.TakeReleaseFailureNotice(), "Told once.");
        AssertTheMenuStillOffersToLetGo(coordinator, StreamingCoordinatorTests.PlaceholderId);
        Assert.IsFalse(coordinator.Menu.Items.Any(i => i.Checked), "It is not in use, so it is not ticked.");
    }

    [TestMethod]
    public async Task StoppingWhenWindowsWillNotLetGoSaysSoAndCanBeTriedAgain()
    {
        var fake = new FakeStreamingPlatform();
        var log = new CapturingLog();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, log, Device("phone"));
        await coordinator.StartPlayingAsync("phone", CancellationToken.None);
        fake.ReleaseFails("phone");

        StreamingReleaseOutcome outcome = coordinator.StopPlaying();

        Assert.IsFalse(outcome.Released);
        Assert.IsTrue(outcome.Failed);
        Assert.AreEqual(unchecked((int)0x8000FFFF), outcome.Step.Code);
        AssertTheFailedReleaseIsInTheLog(log, "phone");
        Assert.AreEqual(MayStill, coordinator.StatusLine, "The line must not claim this PC stopped accepting audio.");
        Assert.AreEqual(MayStill, coordinator.TakeReleaseFailureNotice());
        AssertTheMenuStillOffersToLetGo(coordinator, "phone");

        // The owner tries again from the same menu item, and this time Windows lets go.
        fake.ReleaseSucceedsAgain("phone");
        StreamingReleaseOutcome again = coordinator.StopPlaying("phone");

        Assert.IsTrue(again.Released);
        Assert.AreEqual("This PC is no longer accepting audio from Test Phone.", coordinator.StatusLine);
        Assert.IsFalse(coordinator.Menu.Items.Any(i => i.Command == StreamingMenuCommand.Stop));
        Assert.AreEqual("", coordinator.TooltipLine);
        Assert.IsNull(coordinator.TakeReleaseFailureNotice());
        Assert.IsTrue(log.Has(LogLevel.Info, "released device " + StreamingLog.Key("phone")), "A release that worked is in the log too.");
    }

    // One device at a time only holds if the first was really let go.
    [TestMethod]
    public async Task ASecondDeviceIsNotStartedWhileTheFirstWouldNotLetGo()
    {
        var fake = new FakeStreamingPlatform();
        var log = new CapturingLog();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, log, Device("A"), Device("B", "Phone B"));
        await coordinator.StartPlayingAsync("A", CancellationToken.None);
        fake.ReleaseFails("A");

        StreamingOpenOutcome refused = await coordinator.StartPlayingAsync("B", CancellationToken.None);

        Assert.AreEqual(StreamingOpenStatus.NotStarted, refused.Status);
        Assert.AreEqual("release failed", refused.Step.Detail);
        Assert.IsEmpty(fake.CallsNamed("Enable(B"), "B must not be enabled while A may still be.");
        AssertTheFailedReleaseIsInTheLog(log, "A");
        Assert.AreEqual(MayStill, coordinator.StatusLine);
        Assert.AreEqual(MayStill, coordinator.TakeReleaseFailureNotice());
        AssertTheMenuStillOffersToLetGo(coordinator, "A");

        // Once Windows lets go of A, the next start lets go of it first and then carries on.
        fake.ReleaseSucceedsAgain("A");
        int before = fake.Calls.Count;
        StreamingOpenOutcome started = await coordinator.StartPlayingAsync("B", CancellationToken.None);

        Assert.AreEqual(StreamingOpenStatus.Open, started.Status);
        CollectionAssert.AreEqual(Sequence.Of("Release(A)", "Enable(B)", "Open(B)"), fake.Calls.Skip(before).ToArray());
        Assert.AreEqual("B", coordinator.Menu.Items.Single(i => i.Command == StreamingMenuCommand.Stop).DeviceId);
    }

    // The other half of "one device at a time only holds if the last one was really let go": A here is not the
    // device being replaced (nothing is in use at all, StopPlaying already cleared that), only a name still sitting
    // in _unreleased from an earlier, unrelated stop. Starting B must retry A's release from that list first
    // (StartPlayingCoreAsync's unconfirmed loop), not just from swapping out the device in use, which
    // ASecondDeviceIsNotStartedWhileTheFirstWouldNotLetGo above already covers.
    [TestMethod]
    public async Task AThirdDeviceIsRefusedWhileAnEarlierStoppedOneIsStillUnreleased()
    {
        var fake = new FakeStreamingPlatform();
        var log = new CapturingLog();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, log, Device("A"), Device("B", "Phone B"));
        await coordinator.StartPlayingAsync("A", CancellationToken.None);
        fake.ReleaseFails("A");
        StreamingReleaseOutcome stopped = coordinator.StopPlaying("A");
        Assert.IsTrue(stopped.Failed, "Set-up: A must be unconfirmed and no longer the device in use.");
        Assert.IsFalse(coordinator.Menu.Items.Any(i => i.Command == StreamingMenuCommand.Play && i.DeviceId == "A" && i.Checked));

        StreamingOpenOutcome refused = await coordinator.StartPlayingAsync("B", CancellationToken.None);

        Assert.AreEqual(StreamingOpenStatus.NotStarted, refused.Status);
        Assert.AreEqual("release failed", refused.Step.Detail);
        Assert.IsEmpty(fake.CallsNamed("Enable(B"), "B must not be enabled while A may still be.");
        AssertTheMenuStillOffersToLetGo(coordinator, "A");

        // Once Windows lets go of A, the next start for B lets it go first and then carries on.
        fake.ReleaseSucceedsAgain("A");
        int before = fake.Calls.Count;
        StreamingOpenOutcome started = await coordinator.StartPlayingAsync("B", CancellationToken.None);

        Assert.AreEqual(StreamingOpenStatus.Open, started.Status);
        CollectionAssert.AreEqual(Sequence.Of("Release(A)", "Enable(B)", "Open(B)"), fake.Calls.Skip(before).ToArray());
    }

    [TestMethod]
    public async Task ADeviceTheListNoLongerHoldsWhoseReleaseFailsIsStillShown()
    {
        var fake = new FakeStreamingPlatform();
        var log = new CapturingLog();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, log, Device("phone"));
        await coordinator.StartPlayingAsync("phone", CancellationToken.None);
        fake.ReleaseFails("phone");
        fake.NextDiscovery(FakeStreamingPlatform.Found());

        await coordinator.RefreshAsync(CancellationToken.None);

        Assert.AreEqual("Release(phone)", fake.Calls[^1]);
        AssertTheFailedReleaseIsInTheLog(log, "phone");
        Assert.AreEqual(MayStill, coordinator.StatusLine);
        Assert.AreEqual(MayStill, coordinator.TakeReleaseFailureNotice());
        Assert.IsNull(coordinator.TakeReleaseFailureNotice());
        AssertTheMenuStillOffersToLetGo(coordinator, "phone");
        Assert.IsFalse(coordinator.Menu.Items.Any(i => i.Command == StreamingMenuCommand.Play), "It is gone from the list, and still there to be let go of.");
    }

    [TestMethod]
    public async Task AnEnableThatFailsIsLetGoThroughTheSameFunnel()
    {
        var fake = new FakeStreamingPlatform();
        var log = new CapturingLog();
        fake.EnableAnswers("phone", StreamingEnableStatus.StartFailed, unchecked((int)0x80070490));
        fake.ReleaseFails("phone");
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, log, Device("phone"));

        StreamingOpenOutcome outcome = await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        // The platform no longer lets go by itself when StartAsync fails, where the outcome had nowhere to go: the
        // coordinator does it, so the outcome lands with every other one.
        Assert.AreEqual(StreamingOpenStatus.NotEnabled, outcome.Status);
        Assert.AreEqual("Release(phone)", fake.Calls[^1]);
        AssertTheFailedReleaseIsInTheLog(log, "phone");
        Assert.AreEqual(MayStill, coordinator.StatusLine);
        AssertTheMenuStillOffersToLetGo(coordinator, "phone");
    }

    [TestMethod]
    public async Task ACancelledStartWhoseReleaseFailsIsNoLongerSilent()
    {
        var fake = new FakeStreamingPlatform();
        var log = new CapturingLog();
        fake.OpenNeverAnswers("phone");
        fake.ReleaseFails("phone");
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, log, Device("phone"));
        using var closing = new CancellationTokenSource();

        Task<StreamingOpenOutcome> start = coordinator.StartPlayingAsync("phone", closing.Token);
        await fake.OpenEntered("phone").WaitAsync(Guard);
        await closing.CancelAsync();
        await start.WaitAsync(Guard);

        AssertTheFailedReleaseIsInTheLog(log, "phone");
        Assert.AreEqual(MayStill, coordinator.StatusLine);
        Assert.AreEqual(MayStill, coordinator.TakeReleaseFailureNotice());
    }

    // Cancelled one step earlier, while Windows is still enabling. The platform lets go of nothing by itself, so a
    // connection it had already created is let go of here, and that outcome is recorded like every other.
    [TestMethod]
    public async Task AStartCancelledWhileItIsBeingEnabledIsLetGoAndTheOutcomeRecorded()
    {
        var fake = new FakeStreamingPlatform();
        var log = new CapturingLog();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.EnableRuns("phone", async token =>
        {
            var never = new TaskCompletionSource<StreamingEnableOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() => never.TrySetCanceled(token));
            entered.TrySetResult();
            return await never.Task.ConfigureAwait(false);
        });
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, log, Device("phone"));
        using var closing = new CancellationTokenSource();

        Task<StreamingOpenOutcome> start = coordinator.StartPlayingAsync("phone", closing.Token);
        await entered.Task.WaitAsync(Guard);
        await closing.CancelAsync();
        StreamingOpenOutcome outcome = await start.WaitAsync(Guard);

        Assert.AreEqual(StreamingOpenStatus.NotStarted, outcome.Status);
        Assert.AreEqual("cancelled", outcome.Step.Detail);
        Assert.IsEmpty(fake.CallsNamed("Open("));
        Assert.AreEqual("Release(phone)", fake.Calls[^1], "What Windows may have created before the cancel is let go of.");
        Assert.IsTrue(log.Has(LogLevel.Info, "released device " + StreamingLog.Key("phone")), "And that release is in the log.");

        // The same again with a release Windows does not confirm: no longer silent.
        fake.ReleaseFails("phone");
        using var closingAgain = new CancellationTokenSource();
        var enteredAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.EnableRuns("phone", async token =>
        {
            var never = new TaskCompletionSource<StreamingEnableOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() => never.TrySetCanceled(token));
            enteredAgain.TrySetResult();
            return await never.Task.ConfigureAwait(false);
        });
        Task<StreamingOpenOutcome> again = coordinator.StartPlayingAsync("phone", closingAgain.Token);
        await enteredAgain.Task.WaitAsync(Guard);
        await closingAgain.CancelAsync();
        await again.WaitAsync(Guard);

        AssertTheFailedReleaseIsInTheLog(log, "phone");
        Assert.AreEqual(MayStill, coordinator.TakeReleaseFailureNotice());
    }

    // The two late releases: ReleaseAll has already run, and the start comes back with something it enabled.
    [TestMethod]
    public async Task ALateReleaseAfterAnEnableThatOutlivedReleaseAllIsLoggedWhenItFails()
    {
        var fake = new FakeStreamingPlatform();
        var log = new CapturingLog();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<StreamingEnableOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.EnableRuns("phone", _ =>
        {
            entered.TrySetResult();
            return answer.Task;
        });
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, log, Device("phone"));

        Task<StreamingOpenOutcome> start = coordinator.StartPlayingAsync("phone", CancellationToken.None);
        await entered.Task.WaitAsync(Guard);
        coordinator.ReleaseAll();
        fake.ReleaseFails("phone");
        answer.SetResult(new StreamingEnableOutcome(StreamingEnableStatus.Enabled, "phone", StepOutcomes.FromHResult("streaming-enable", 0)));
        await start.WaitAsync(Guard);

        Assert.AreEqual("Release(phone)", fake.Calls[^1]);
        AssertTheFailedReleaseIsInTheLog(log, "phone");
        Assert.AreEqual(MayStill, coordinator.TakeReleaseFailureNotice(), "The host can still tell the owner after the coordinator has closed.");
    }

    [TestMethod]
    public async Task ALateReleaseAfterAnOpenThatOutlivedReleaseAllIsLoggedWhenItFails()
    {
        var fake = new FakeStreamingPlatform();
        var log = new CapturingLog();
        var answer = new TaskCompletionSource<StreamingOpenOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OpenRuns("phone", _ => answer.Task);
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, log, Device("phone"));

        Task<StreamingOpenOutcome> start = coordinator.StartPlayingAsync("phone", CancellationToken.None);
        await fake.OpenEntered("phone").WaitAsync(Guard);
        coordinator.ReleaseAll();
        fake.ReleaseFails("phone");
        answer.SetResult(new StreamingOpenOutcome(StreamingOpenStatus.Open, "phone", StepOutcomes.FromHResult("streaming-open", 0)));
        await start.WaitAsync(Guard);

        Assert.AreEqual("Release(phone)", fake.Calls[^1]);
        AssertTheFailedReleaseIsInTheLog(log, "phone");
        Assert.AreEqual(MayStill, coordinator.TakeReleaseFailureNotice());
    }

    [TestMethod]
    public async Task ReleaseAllKeepsEveryOutcomeAndLogsEachOne()
    {
        var fake = new FakeStreamingPlatform();
        var log = new CapturingLog();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, log, Device("phone"));
        await coordinator.StartPlayingAsync("phone", CancellationToken.None);
        fake.ReleaseFails("phone");

        IReadOnlyList<StreamingReleaseOutcome> outcomes = coordinator.ReleaseAll();

        Assert.AreEqual(1, outcomes.Count);
        Assert.IsTrue(outcomes[0].Failed);
        Assert.AreEqual("phone", outcomes[0].DeviceId);
        Assert.AreEqual(unchecked((int)0x8000FFFF), outcomes[0].Step.Code);
        AssertTheFailedReleaseIsInTheLog(log, "phone");
        Assert.AreEqual(MayStill, coordinator.TakeReleaseFailureNotice());

        // Disposing afterwards is not a second ReleaseAll: what failed is tried once more, not once per call.
        int calls = fake.CallsNamed("Release(").Count;
        coordinator.Dispose();
        Assert.AreEqual(calls, fake.CallsNamed("Release(").Count);
    }

    [TestMethod]
    public async Task ReleaseAllWithNothingEnabledReturnsNothingAndFailsNothing()
    {
        var fake = new FakeStreamingPlatform();
        var log = new CapturingLog();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, log, Device("phone"));

        IReadOnlyList<StreamingReleaseOutcome> outcomes = coordinator.ReleaseAll();

        Assert.AreEqual(0, outcomes.Count);
        Assert.IsEmpty(fake.CallsNamed("Release("));
        Assert.IsNull(coordinator.TakeReleaseFailureNotice());
    }

    // The real platform keeps a connection Windows would not close, so the same connection is tried again by the next
    // release and nothing is dropped unconfirmed. Driven through a stand-in for the connection: no WinRT is touched.
    [TestMethod]
    public void ThePlatformKeepsAConnectionItCouldNotCloseSoTheNextReleaseTriesItAgain()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Assert.Inconclusive("Windows older than 10.0.19041: the platform refuses every call before it reaches this.");
            return;
        }

        var platform = new WindowsStreamingPlatform(new CapturingLog());
        var link = new StubLink { Fails = true };
        platform.HoldForTest("phone", link);

        StreamingReleaseOutcome first = platform.Release("phone");

        Assert.IsTrue(first.Failed);
        Assert.AreEqual(unchecked((int)0x8000FFFF), first.Step.Code);
        Assert.AreEqual(1, platform.HeldConnections, "Windows did not confirm, so the connection is still held.");

        link.Fails = false;
        StreamingReleaseOutcome second = platform.Release("phone");

        Assert.IsTrue(second.Released);
        Assert.AreEqual(2, link.Closes, "The same connection was tried again.");
        Assert.AreEqual(0, platform.HeldConnections);
        Assert.IsFalse(platform.Release("phone").Failed, "With nothing held there is nothing to fail.");
    }

    private sealed class StubLink : IHeldLink
    {
        public bool Fails { get; set; }

        public int Closes { get; private set; }

        public StepOutcome Close()
        {
            Closes++;
            return Fails ? FakeStreamingPlatform.ReleaseFailure : StepOutcomes.FromHResult("streaming-release", 0);
        }
    }

    // "Nothing to release" is an ordinary answer, not a failure, and must never frighten the owner.
    [TestMethod]
    public void NothingToReleaseIsNotAFailure()
    {
        Assert.IsFalse(new StreamingReleaseOutcome(false, "id", StepOutcomes.NotAttempted("streaming-release", "nothing enabled")).Failed);
        Assert.IsFalse(new StreamingReleaseOutcome(false, "id", StepOutcomes.NotAvailable("streaming-release", "support")).Failed);
        Assert.IsFalse(new StreamingReleaseOutcome(true, "id", StepOutcomes.FromHResult("streaming-release", 0)).Failed);
        Assert.IsTrue(new StreamingReleaseOutcome(false, "id", FakeStreamingPlatform.ReleaseFailure).Failed);
    }
}
