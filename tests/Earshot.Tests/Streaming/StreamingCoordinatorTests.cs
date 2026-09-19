using System.Diagnostics;
using Earshot.Contracts;
using Earshot.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Streaming;

// The streaming state machine against a scripted platform. Nothing here touches WinRT, a radio or a real device,
// and nothing sleeps: a time limit is reached by moving a test clock, and a callback from another thread is waited
// for on a signal. Every expected sentence is written out here rather than read from StreamingCopy, so a change to
// the copy is a change someone has to make twice and mean.
[TestClass]
public sealed class StreamingCoordinatorTests
{
    // A placeholder with the shape of a real id, distinctive enough to search every string for.
    internal const string PlaceholderId = @"BTHENUM\PLACEHOLDER-ID-0001";
    private const string Name = "Test Phone";

    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static StreamingDevice Device(string id, string name = Name, Guid? container = null) =>
        new(id, name, container ?? Guid.NewGuid());

    private static StreamingCoordinator Make(
        FakeStreamingPlatform fake,
        IBusyGate? gate = null,
        StreamingSettings? settings = null,
        Func<StreamingDevice, bool>? isExcluded = null,
        TimeProvider? time = null) =>
        new(fake, gate ?? new ManualBusyGate(), settings ?? StreamingSettings.Default, isExcluded ?? (_ => false), time ?? new TestTimeProvider());

    private static async Task<StreamingCoordinator> MakeWithListAsync(FakeStreamingPlatform fake, params StreamingDevice[] devices)
    {
        fake.NextDiscovery(FakeStreamingPlatform.Found(devices));
        StreamingCoordinator coordinator = Make(fake);
        await coordinator.RefreshAsync(CancellationToken.None);
        return coordinator;
    }

    // Acceptance test 1. Support is asked once, on construction, and never again: not for a menu, not for a refresh.
    [TestMethod]
    public async Task SupportCheckedOnceAndCached()
    {
        var first = new FakeStreamingPlatform();
        var second = new FakeStreamingPlatform();
        using StreamingCoordinator a = Make(first);
        using StreamingCoordinator b = Make(second);

        for (int i = 0; i < 5; i++)
        {
            _ = a.Menu;
            _ = b.Menu;
            _ = a.StatusLine;
        }

        await a.RefreshAsync(CancellationToken.None);
        await b.RefreshAsync(CancellationToken.None);
        await a.StartPlayingAsync("anything", CancellationToken.None);

        Assert.AreEqual(1, first.CheckSupportCalls);
        Assert.AreEqual(1, second.CheckSupportCalls);
        Assert.AreEqual(StreamingSupport.Supported, a.Support);
    }

    // Acceptance test 2. An old build stops before Windows is asked for anything.
    [TestMethod]
    [DataRow(StreamingSupport.BuildTooOld, "Needs a newer version of Windows.")]
    [DataRow(StreamingSupport.TypeMissing, "This copy of Windows cannot receive Bluetooth audio.")]
    [DataRow(StreamingSupport.NotChecked, "Not checked yet.")]
    public async Task BuildTooOldOffersNothingAndAttemptsNothing(object support, string sentence)
    {
        var fake = new FakeStreamingPlatform { Support = (StreamingSupport)support };
        using StreamingCoordinator coordinator = Make(fake);

        StreamingDiscovery discovery = await coordinator.RefreshAsync(CancellationToken.None);
        StreamingOpenOutcome outcome = await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        Assert.IsFalse(coordinator.Menu.ParentEnabled);
        Assert.AreEqual("Play from a phone", coordinator.Menu.ParentText);
        Assert.AreEqual(1, coordinator.Menu.Items.Count);
        Assert.AreEqual(sentence, coordinator.Menu.Items[0].Text);
        Assert.IsFalse(coordinator.Menu.Items[0].Enabled);
        Assert.AreEqual(sentence, coordinator.StatusLine);
        Assert.AreEqual(StreamingDiscoveryStatus.Unsupported, discovery.Status);
        Assert.AreEqual(StreamingOpenStatus.Unsupported, outcome.Status);
        Assert.AreEqual(NativeCodes.NotAttempted, outcome.Step.Code, "A refusal must never read as S_OK.");
        Assert.IsEmpty(fake.Calls, "An unsupported build must not reach discovery, enable or open.");
    }

    // Acceptance test 3. Enable, then open: two steps, always, in that order.
    [TestMethod]
    public async Task EnableThenOpenInThatOrder()
    {
        var fake = new FakeStreamingPlatform();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"), Device("console", "Console"));

        StreamingOpenOutcome outcome = await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        Assert.AreEqual(StreamingOpenStatus.Open, outcome.Status);
        CollectionAssert.AreEqual(Sequence.Of("List", "Enable(phone)", "Open(phone)"), fake.Calls.ToArray());
        Assert.IsEmpty(fake.CallsNamed("Open(console"), "A device that was never enabled must never be opened.");
        Assert.AreEqual("Waiting for Test Phone. Start playing something on it.", coordinator.StatusLine);
    }

    // Acceptance test 5. Every failure the owner can meet, the exact sentence for each, and only the two timeouts
    // sharing one.
    [TestMethod]
    public async Task EachFailureHasItsOwnSentence()
    {
        IReadOnlyList<FailureRow> rows = await DriveEveryFailureRowAsync("phone", Name);

        var expected = new Dictionary<string, string>
        {
            ["windows-too-old"] = "Needs a newer version of Windows.",
            ["type-missing"] = "This copy of Windows cannot receive Bluetooth audio.",
            ["enumeration-throws"] = "Could not read the list of paired devices.",
            ["enumeration-empty"] = "No paired device can send audio to this PC.",
            ["create-returns-null"] = "Test Phone cannot send audio to this PC.",
            ["start-throws"] = "Could not get ready for Test Phone.",
            ["open-timed-out"] = "Test Phone did not answer in time. Try again.",
            ["open-denied"] = "Windows refused the connection. Reconnect Test Phone in Bluetooth settings, then try again.",
            ["open-unknown-failure"] = "Could not connect to Test Phone. See the log.",
            ["open-throws"] = "Could not connect to Test Phone.",
            ["local-timeout"] = "Test Phone did not answer in time. Try again.",
            ["device-disappears"] = "Test Phone has disconnected.",
            ["busy"] = "Busy just now. Try again in a moment.",
        };

        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), rows.Select(r => r.Row).ToArray(), "Every row of the failure table is driven, once.");
        foreach (FailureRow row in rows)
        {
            Assert.AreEqual(expected[row.Row], row.Seen[0], row.Row);
        }

        // The second sentence of the empty list sends the owner to Windows Settings to pair: Earshot cannot pair.
        FailureRow empty = rows.Single(r => r.Row == "enumeration-empty");
        CollectionAssert.AreEqual(
            Sequence.Of("No paired device can send audio to this PC.", "Pair the phone in Windows Settings first."),
            empty.Seen.ToArray());

        // Exactly one sentence is shared, by the two timeouts and by nothing else.
        var groups = rows.GroupBy(r => r.Seen[0], StringComparer.Ordinal).ToList();
        foreach (IGrouping<string, FailureRow> shared in groups.Where(g => g.Count() > 1))
        {
            Assert.AreEqual("Test Phone did not answer in time. Try again.", shared.Key);
            CollectionAssert.AreEquivalent(Sequence.Of("open-timed-out", "local-timeout"), shared.Select(r => r.Row).ToArray());
        }

        Assert.AreEqual(rows.Count - 1, groups.Count);

        // The two are still told apart where it matters, in the outcome.
        StreamingOpenOutcome fromWindows = rows.Single(r => r.Row == "open-timed-out").Outcome!;
        StreamingOpenOutcome ours = rows.Single(r => r.Row == "local-timeout").Outcome!;
        Assert.AreEqual(StreamingOpenStatus.TimedOut, fromWindows.Status);
        Assert.AreEqual(StreamingOpenStatus.TimedOut, ours.Status);
        Assert.AreEqual("local timeout", ours.Step.Detail);
        Assert.AreNotEqual("local timeout", fromWindows.Step.Detail);

        // The code Windows gave is kept on the outcome for the log; it is never put in front of the owner.
        StreamingOpenOutcome unknown = rows.Single(r => r.Row == "open-unknown-failure").Outcome!;
        Assert.AreEqual(unchecked((int)0x8007048F), unknown.Step.Code);
        Assert.IsFalse(rows.SelectMany(r => r.Seen).Any(s => s.Contains("8007048F", StringComparison.OrdinalIgnoreCase)));
    }

    // An unknown failure that came with no code from Windows has nothing to look up, so it reads as a plain failure.
    [TestMethod]
    public async Task AnUnknownFailureWithNoCodeFromWindowsDoesNotSendTheOwnerToTheLog()
    {
        var fake = new FakeStreamingPlatform();
        fake.OpenAnswers("phone", StreamingOpenStatus.UnknownFailure, hresult: 0);
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"));

        await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        Assert.AreEqual("Could not connect to Test Phone.", coordinator.StatusLine);
    }

    // Acceptance test 7. The device id reaches no sentence and no menu text, whatever happens.
    [TestMethod]
    public async Task DeviceIdNeverAppearsInCopy()
    {
        IReadOnlyList<FailureRow> rows = await DriveEveryFailureRowAsync(PlaceholderId, Name);
        var everything = rows.SelectMany(r => r.Seen).Concat(rows.SelectMany(r => r.MenuTexts)).ToList();

        // A happy path too: waiting, playing, the Stop item, stopped, no longer accepting.
        var fake = new FakeStreamingPlatform();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device(PlaceholderId));
        await coordinator.StartPlayingAsync(PlaceholderId, CancellationToken.None);
        everything.Add(coordinator.StatusLine);
        everything.AddRange(coordinator.Menu.Items.Select(i => i.Text));
        fake.RaiseLink(PlaceholderId, StreamingLinkState.Opened);
        everything.Add(coordinator.StatusLine);
        everything.Add(coordinator.TooltipLine);
        coordinator.StopPlaying();
        everything.Add(coordinator.StatusLine);

        Assert.IsTrue(everything.Count > 20, "The check must have something to check.");
        foreach (string text in everything)
        {
            foreach (string part in Sequence.Of(PlaceholderId, "BTHENUM", "PLACEHOLDER", "0001"))
            {
                Assert.IsFalse(text.Contains(part, StringComparison.OrdinalIgnoreCase), "\"" + text + "\" carries part of the device id.");
            }
        }
    }

    // Acceptance test 8. The device Earshot manages is never offered and never reaches Windows.
    [TestMethod]
    public async Task ExcludedDeviceIsNeverOfferedOrOpened()
    {
        var fake = new FakeStreamingPlatform();
        fake.NextDiscovery(FakeStreamingPlatform.Found(Device("phone"), Device("earbuds", "Managed Earbuds"), Device("console", "Console")));
        using StreamingCoordinator coordinator = Make(fake, isExcluded: d => d.DeviceId == "earbuds");

        StreamingDiscovery discovery = await coordinator.RefreshAsync(CancellationToken.None);
        StreamingOpenOutcome outcome = await coordinator.StartPlayingAsync("earbuds", CancellationToken.None);

        string[] offered = coordinator.Menu.Items.Where(i => i.Command == StreamingMenuCommand.Play).Select(i => i.Text).ToArray();
        CollectionAssert.AreEqual(Sequence.Of("Test Phone", "Console"), offered);
        Assert.IsFalse(discovery.Devices.Any(d => d.DeviceId == "earbuds"), "The managed device must not leave the coordinator in a result either.");
        Assert.AreEqual(StreamingOpenStatus.NotStarted, outcome.Status);
        Assert.AreEqual("excluded", outcome.Step.Detail);
        Assert.IsEmpty(fake.CallsNamed("Enable("));
        Assert.IsEmpty(fake.CallsNamed("Open("));
    }

    // An id the last read of the list never held is not something the owner could have clicked, so Windows is not
    // asked about it.
    [TestMethod]
    public async Task AnIdTheListNeverHeldIsNeverPassedToWindows()
    {
        var fake = new FakeStreamingPlatform();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"));

        StreamingOpenOutcome outcome = await coordinator.StartPlayingAsync("something-else", CancellationToken.None);

        Assert.AreEqual(StreamingOpenStatus.NotStarted, outcome.Status);
        Assert.AreEqual("unknown device", outcome.Step.Detail);
        CollectionAssert.AreEqual(Sequence.Of("List"), fake.Calls.ToArray());
    }

    // Acceptance test 9. Streaming never starts in the middle of an earbud operation.
    [TestMethod]
    public async Task BusyGateBlocksStart()
    {
        var fake = new FakeStreamingPlatform();
        var gate = new ManualBusyGate();
        fake.NextDiscovery(FakeStreamingPlatform.Found(Device("phone")));
        using StreamingCoordinator coordinator = Make(fake, gate);
        await coordinator.RefreshAsync(CancellationToken.None);

        gate.Hold();
        StreamingOpenOutcome refused = await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        Assert.AreEqual(StreamingOpenStatus.NotStarted, refused.Status);
        Assert.AreEqual("a connect is in flight", refused.Step.Detail);
        Assert.AreEqual("Busy just now. Try again in a moment.", coordinator.StatusLine);
        Assert.IsEmpty(fake.CallsNamed("Enable("));
        Assert.IsEmpty(fake.CallsNamed("Open("));

        gate.Release();
        StreamingOpenOutcome started = await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        Assert.AreEqual(StreamingOpenStatus.Open, started.Status);
        CollectionAssert.AreEqual(Sequence.Of("List", "Enable(phone)", "Open(phone)"), fake.Calls.ToArray());
    }

    // Acceptance test 10. One device at a time: the first is let go before the second is enabled.
    [TestMethod]
    public async Task SecondDeviceReplacesFirst()
    {
        var fake = new FakeStreamingPlatform();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("A", "Phone A"), Device("B", "Phone B"));

        await coordinator.StartPlayingAsync("A", CancellationToken.None);
        await coordinator.StartPlayingAsync("B", CancellationToken.None);

        CollectionAssert.AreEqual(
            Sequence.Of("List", "Enable(A)", "Open(A)", "Release(A)", "Enable(B)", "Open(B)"),
            fake.Calls.ToArray());
        string[] ticked = coordinator.Menu.Items.Where(i => i.Checked).Select(i => i.Text).ToArray();
        CollectionAssert.AreEqual(Sequence.Of("Phone B"), ticked);
        Assert.AreEqual("Stop playing from Phone B", coordinator.Menu.Items.Single(i => i.Command == StreamingMenuCommand.Stop).Text);
    }

    // Acceptance test 11. A link change arrives on a thread of Windows' own; the line and the tick follow it.
    [TestMethod]
    public async Task LinkChangedUpdatesLineFromAnyThread()
    {
        var fake = new FakeStreamingPlatform();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"));
        await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        var changed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int raisedOn = 0;
        EventHandler handler = (_, _) =>
        {
            raisedOn = Environment.CurrentManagedThreadId;
            changed.TrySetResult(raisedOn);
        };
        coordinator.Changed += handler;

        int poolThread = await Task.Run(() =>
        {
            fake.RaiseLink("phone", StreamingLinkState.Opened);
            return Environment.CurrentManagedThreadId;
        });
        await changed.Task.WaitAsync(Guard);

        Assert.AreEqual(poolThread, raisedOn, "Changed is raised where the link change arrived: nothing is marshalled inside the coordinator.");
        Assert.AreEqual("Test Phone is connected and can play through this PC.", coordinator.StatusLine);
        Assert.IsTrue(coordinator.Menu.Items.Single(i => i.Command == StreamingMenuCommand.Play).Checked);

        coordinator.Changed -= handler;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.Changed += (_, _) => closed.TrySetResult();
        await Task.Run(() => fake.RaiseLink("phone", StreamingLinkState.Closed));
        await closed.Task.WaitAsync(Guard);

        Assert.AreEqual("Test Phone has disconnected.", coordinator.StatusLine);
        Assert.IsFalse(coordinator.Menu.Items.Single(i => i.Command == StreamingMenuCommand.Play).Checked, "The tick is cleared when the phone lets go.");

        // Still enabled, so the phone can start again by itself, and still stoppable: nothing is left that the owner
        // cannot see and let go of.
        Assert.IsEmpty(fake.CallsNamed("Release("));
        Assert.AreEqual("Stop playing from Test Phone", coordinator.Menu.Items.Single(i => i.Command == StreamingMenuCommand.Stop).Text);
        Assert.AreEqual("Test Phone has disconnected.", coordinator.TooltipLine);
    }

    // A link change for a device that is not the one in use says nothing: a late report for one already let go, or
    // one for a device nobody chose.
    [TestMethod]
    public async Task ALinkChangeForADeviceNotInUseChangesNothing()
    {
        var fake = new FakeStreamingPlatform();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"), Device("other", "Other"));
        await coordinator.StartPlayingAsync("phone", CancellationToken.None);
        string before = coordinator.StatusLine;
        int raised = 0;
        coordinator.Changed += (_, _) => raised++;

        fake.RaiseLink("other", StreamingLinkState.Opened);
        fake.RaiseLink("phone", StreamingLinkState.Unknown);

        Assert.AreEqual(before, coordinator.StatusLine);
        Assert.AreEqual(0, raised);
    }

    // Acceptance test 12. The way out lets go of everything, once each.
    [TestMethod]
    public async Task ReleaseAllReleasesEverythingEnabled()
    {
        var fake = new FakeStreamingPlatform();
        fake.OpenAnswers("A", StreamingOpenStatus.TimedOut);
        fake.OpenAnswers("B", StreamingOpenStatus.DeniedBySystem);
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("A", "Phone A"), Device("B", "Phone B"), Device("C", "Phone C"));

        await coordinator.StartPlayingAsync("A", CancellationToken.None);
        await coordinator.StartPlayingAsync("B", CancellationToken.None);
        await coordinator.StartPlayingAsync("C", CancellationToken.None);
        Assert.AreEqual(3, fake.CallsNamed("Enable(").Count, "Three devices were enabled.");

        StreamingReleaseOutcome last = coordinator.ReleaseAll();

        foreach (string id in Sequence.Of("A", "B", "C"))
        {
            Assert.AreEqual(1, fake.Calls.Count(c => c == "Release(" + id + ")"), id + " must be released exactly once.");
        }

        Assert.IsTrue(last.Released);
        Assert.AreEqual("C", last.DeviceId);
        Assert.AreEqual("Release(C)", fake.Calls[^1], "The one still enabled is what ReleaseAll itself lets go of.");

        // Final: nothing is released twice, and nothing starts afterwards.
        coordinator.ReleaseAll();
        StreamingOpenOutcome after = await coordinator.StartPlayingAsync("A", CancellationToken.None);
        Assert.AreEqual(3, fake.CallsNamed("Release(").Count);
        Assert.AreEqual(StreamingOpenStatus.NotStarted, after.Status);
        Assert.AreEqual("closed", after.Step.Detail);
        Assert.AreEqual(3, fake.CallsNamed("Enable(").Count);
    }

    // Nothing the owner cannot see stays enabled: an enable whose open fails is let go at once, for every way an
    // open can fail, and no Stop item is left behind for it.
    [TestMethod]
    [DataRow(StreamingOpenStatus.TimedOut)]
    [DataRow(StreamingOpenStatus.DeniedBySystem)]
    [DataRow(StreamingOpenStatus.UnknownFailure)]
    [DataRow(StreamingOpenStatus.CallFailed)]
    [DataRow(StreamingOpenStatus.NotEnabled)]
    public async Task AFailedOpenLeavesNothingEnabled(object status)
    {
        var fake = new FakeStreamingPlatform();
        fake.OpenAnswers("phone", (StreamingOpenStatus)status, unchecked((int)0x80004005));
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"));

        StreamingOpenOutcome outcome = await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        Assert.AreEqual((StreamingOpenStatus)status, outcome.Status);
        CollectionAssert.AreEqual(Sequence.Of("List", "Enable(phone)", "Open(phone)", "Release(phone)"), fake.Calls.ToArray());
        Assert.IsFalse(coordinator.Menu.Items.Any(i => i.Command == StreamingMenuCommand.Stop));
        Assert.IsFalse(coordinator.Menu.Items.Any(i => i.Checked));
        Assert.AreEqual("", coordinator.TooltipLine);
    }

    // Acceptance test 13.
    [TestMethod]
    public void StopWhenNothingOpenIsHarmless()
    {
        var fake = new FakeStreamingPlatform();
        using StreamingCoordinator coordinator = Make(fake);
        string before = coordinator.StatusLine;

        StreamingReleaseOutcome outcome = coordinator.StopPlaying();

        Assert.IsFalse(outcome.Released);
        Assert.AreEqual("", outcome.DeviceId);
        Assert.AreEqual("nothing open", outcome.Step.Detail);
        Assert.AreEqual(before, coordinator.StatusLine);
        Assert.IsEmpty(fake.Calls);
    }

    [TestMethod]
    public async Task StoppingLetsGoAndSaysSo()
    {
        var fake = new FakeStreamingPlatform();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"));
        await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        StreamingReleaseOutcome outcome = coordinator.StopPlaying();

        Assert.IsTrue(outcome.Released);
        Assert.AreEqual("Release(phone)", fake.Calls[^1]);
        Assert.AreEqual("This PC is no longer accepting audio from Test Phone.", coordinator.StatusLine);
        Assert.AreEqual("", coordinator.TooltipLine, "With nothing in use the tooltip is what it always was.");
        Assert.IsFalse(coordinator.Menu.Items.Any(i => i.Command == StreamingMenuCommand.Stop || i.Checked));

        // And ReleaseAll afterwards has nothing left to let go of.
        coordinator.ReleaseAll();
        Assert.AreEqual(1, fake.CallsNamed("Release(").Count);
    }

    // Acceptance test 14. The time limit is Earshot's own, on the injected clock, and nothing sleeps for it.
    [TestMethod]
    public async Task LocalTimeoutProducesTimedOut()
    {
        var clock = new TestTimeProvider();
        var fake = new FakeStreamingPlatform();
        fake.OpenNeverAnswers("phone");
        fake.NextDiscovery(FakeStreamingPlatform.Found(Device("phone")));
        using StreamingCoordinator coordinator = Make(fake, settings: StreamingSettings.Default with { OpenTimeoutSeconds = 20 }, time: clock);
        await coordinator.RefreshAsync(CancellationToken.None);
        var watch = Stopwatch.StartNew();

        Task<StreamingOpenOutcome> start = coordinator.StartPlayingAsync("phone", CancellationToken.None);
        await fake.OpenEntered("phone").WaitAsync(Guard);
        Assert.IsFalse(start.IsCompleted, "Windows has not answered and the clock has not moved.");

        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.IsFalse(start.IsCompleted, "One second short of the limit is not the limit.");

        clock.Advance(TimeSpan.FromSeconds(2));
        StreamingOpenOutcome outcome = await start.WaitAsync(Guard);
        watch.Stop();

        Assert.AreEqual(StreamingOpenStatus.TimedOut, outcome.Status);
        Assert.AreEqual("local timeout", outcome.Step.Detail);
        Assert.AreEqual("Test Phone did not answer in time. Try again.", coordinator.StatusLine);
        Assert.AreEqual("Release(phone)", fake.Calls[^1], "What was enabled for an open that never answered is let go.");
        Assert.AreEqual(0, clock.LiveTimers, "The limit's timer is let go of once the open has ended.");

        // Twenty seconds of limit passed on the test clock. Had any of it been waited for on the real clock, this
        // could not hold; ten seconds of allowance is for a loaded machine, not for a sleep.
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), "The limit was waited for on the real clock: " + watch.Elapsed);
    }

    [TestMethod]
    public async Task AReadOfTheListHasItsOwnLimitOnTheSameClock()
    {
        var clock = new TestTimeProvider();
        var fake = new FakeStreamingPlatform();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.NextDiscovery(async token =>
        {
            var never = new TaskCompletionSource<StreamingDiscovery>(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() => never.TrySetCanceled(token));
            entered.TrySetResult();
            return await never.Task.ConfigureAwait(false);
        });
        using StreamingCoordinator coordinator = Make(fake, time: clock);

        Task<StreamingDiscovery> read = coordinator.RefreshAsync(CancellationToken.None);
        await entered.Task.WaitAsync(Guard);
        Assert.AreEqual("Looking for devices.", coordinator.Menu.Items[0].Text);

        clock.Advance(TimeSpan.FromSeconds(6));
        StreamingDiscovery discovery = await read.WaitAsync(Guard);

        Assert.AreEqual(StreamingDiscoveryStatus.Failed, discovery.Status);
        Assert.AreEqual("local timeout", discovery.Step.Detail);
        Assert.AreEqual("Could not read the list of paired devices.", coordinator.Menu.Items[0].Text);
    }

    // A caller that cancels (Earshot closing) is not a timeout: nothing is said, and what was enabled is let go.
    [TestMethod]
    public async Task AStartTheCallerCancelsLetsGoAndSaysNothing()
    {
        var fake = new FakeStreamingPlatform();
        fake.OpenNeverAnswers("phone");
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"));
        string before = coordinator.StatusLine;
        using var closing = new CancellationTokenSource();

        Task<StreamingOpenOutcome> start = coordinator.StartPlayingAsync("phone", closing.Token);
        await fake.OpenEntered("phone").WaitAsync(Guard);
        await closing.CancelAsync();
        StreamingOpenOutcome outcome = await start.WaitAsync(Guard);

        Assert.AreEqual(StreamingOpenStatus.NotStarted, outcome.Status);
        Assert.AreEqual("cancelled", outcome.Step.Detail);
        Assert.AreEqual(before, coordinator.StatusLine);
        Assert.AreEqual("Release(phone)", fake.Calls[^1]);
    }

    // The worst bug this feature can have is the PC still accepting audio after Earshot has gone. ReleaseAll landing
    // while an open is still in flight lets go of what that start enabled, and the start takes nothing up afterwards.
    [TestMethod]
    public async Task ReleaseAllWhileAnOpenIsInFlightLeavesNothingEnabled()
    {
        var fake = new FakeStreamingPlatform();
        var answer = new TaskCompletionSource<StreamingOpenOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OpenRuns("phone", _ => answer.Task);
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"));

        Task<StreamingOpenOutcome> start = coordinator.StartPlayingAsync("phone", CancellationToken.None);
        await fake.OpenEntered("phone").WaitAsync(Guard);
        coordinator.ReleaseAll();
        Assert.AreEqual(1, fake.Calls.Count(c => c == "Release(phone)"), "ReleaseAll knows about a device whose open has not come back yet.");

        // Windows then says the open worked. It is too late: nothing is taken up, and it is let go again to be sure.
        answer.SetResult(new StreamingOpenOutcome(StreamingOpenStatus.Open, "phone", StepOutcomes.FromHResult("streaming-open", 0)));
        StreamingOpenOutcome outcome = await start.WaitAsync(Guard);

        Assert.AreEqual(StreamingOpenStatus.NotStarted, outcome.Status);
        Assert.AreEqual("released meanwhile", outcome.Step.Detail);
        Assert.IsFalse(coordinator.Menu.Items.Any(i => i.Checked || i.Command == StreamingMenuCommand.Stop));
        Assert.AreEqual("", coordinator.TooltipLine);
        Assert.AreEqual("Release(phone)", fake.Calls[^1]);
    }

    // The same, one step earlier: ReleaseAll lands while Windows is still enabling, before there is anything to
    // release. The start releases it itself when the enable comes back, and never opens it.
    [TestMethod]
    public async Task ReleaseAllWhileAnEnableIsInFlightIsMadeGoodWhenTheEnableComesBack()
    {
        var fake = new FakeStreamingPlatform();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<StreamingEnableOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.EnableRuns("phone", _ =>
        {
            entered.TrySetResult();
            return answer.Task;
        });
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"));

        Task<StreamingOpenOutcome> start = coordinator.StartPlayingAsync("phone", CancellationToken.None);
        await entered.Task.WaitAsync(Guard);
        coordinator.ReleaseAll();
        answer.SetResult(new StreamingEnableOutcome(StreamingEnableStatus.Enabled, "phone", StepOutcomes.FromHResult("streaming-enable", 0)));
        StreamingOpenOutcome outcome = await start.WaitAsync(Guard);

        Assert.AreEqual(StreamingOpenStatus.NotStarted, outcome.Status);
        Assert.IsEmpty(fake.CallsNamed("Open("), "A device released while it was being enabled is never opened.");
        Assert.AreEqual("Release(phone)", fake.Calls[^1], "The enable that came back late is let go.");
    }

    // Two fast clicks cannot interleave: the second waits for the first to end.
    [TestMethod]
    public async Task TwoStartsNeverInterleave()
    {
        var fake = new FakeStreamingPlatform();
        var answer = new TaskCompletionSource<StreamingOpenOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OpenRuns("A", _ => answer.Task);
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("A", "Phone A"), Device("B", "Phone B"));

        Task<StreamingOpenOutcome> first = coordinator.StartPlayingAsync("A", CancellationToken.None);
        await fake.OpenEntered("A").WaitAsync(Guard);
        Task<StreamingOpenOutcome> second = coordinator.StartPlayingAsync("B", CancellationToken.None);
        Assert.IsFalse(second.IsCompleted);
        Assert.IsEmpty(fake.CallsNamed("Enable(B"), "The second start has not begun while the first holds the turn.");

        answer.SetResult(new StreamingOpenOutcome(StreamingOpenStatus.Open, "A", StepOutcomes.FromHResult("streaming-open", 0)));
        await first.WaitAsync(Guard);
        await second.WaitAsync(Guard);

        CollectionAssert.AreEqual(
            Sequence.Of("List", "Enable(A)", "Open(A)", "Release(A)", "Enable(B)", "Open(B)"),
            fake.Calls.ToArray());
    }

    // The device in use, chosen again: Windows is not asked to open what is open. Once the phone has closed the
    // link, the same click opens it again.
    [TestMethod]
    public async Task ChoosingTheDeviceInUseAgainAsksWindowsNothingUntilItsLinkHasClosed()
    {
        var fake = new FakeStreamingPlatform();
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"));
        await coordinator.StartPlayingAsync("phone", CancellationToken.None);
        int callsAfterFirst = fake.Calls.Count;

        StreamingOpenOutcome again = await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        Assert.AreEqual(StreamingOpenStatus.Open, again.Status);
        Assert.AreEqual("already open", again.Step.Detail);
        Assert.AreEqual(callsAfterFirst, fake.Calls.Count);

        fake.RaiseLink("phone", StreamingLinkState.Closed);
        StreamingOpenOutcome reopened = await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        Assert.AreEqual(StreamingOpenStatus.Open, reopened.Status);
        CollectionAssert.AreEqual(Sequence.Of("Enable(phone)", "Open(phone)"), fake.Calls.Skip(callsAfterFirst).ToArray());
        Assert.IsTrue(coordinator.Menu.Items.Single(i => i.Command == StreamingMenuCommand.Play).Checked);
    }

    // Windows may report the link before OpenAsync returns. Opened then is what a good open looks like; Closed then
    // means the phone has already let go, and that is what the owner is shown.
    [TestMethod]
    public async Task ALinkThatClosedBeforeTheOpenReturnedIsShownAsStopped()
    {
        var fake = new FakeStreamingPlatform();
        fake.OpenRuns("phone", _ =>
        {
            fake.RaiseLink("phone", StreamingLinkState.Opened);
            fake.RaiseLink("phone", StreamingLinkState.Closed);
            return Task.FromResult(new StreamingOpenOutcome(StreamingOpenStatus.Open, "phone", StepOutcomes.FromHResult("streaming-open", 0)));
        });
        using StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"));

        await coordinator.StartPlayingAsync("phone", CancellationToken.None);

        Assert.AreEqual("Test Phone has disconnected.", coordinator.StatusLine);
        Assert.IsFalse(coordinator.Menu.Items.Single(i => i.Command == StreamingMenuCommand.Play).Checked);
        Assert.IsTrue(coordinator.Menu.Items.Any(i => i.Command == StreamingMenuCommand.Stop), "Still enabled, so still stoppable.");
    }

    // One read that fails, or that answers something that is neither a list nor a failure, must not be the last
    // read: the next one still runs and still counts.
    [TestMethod]
    public async Task AReadThatFailsOrAnswersNothingUsefulDoesNotStopTheNextOne()
    {
        var fake = new FakeStreamingPlatform();
        fake.NextDiscovery(FakeStreamingPlatform.ListFailed(unchecked((int)0x80070005)));
        fake.NextDiscovery(new StreamingDiscovery(StreamingDiscoveryStatus.NotStarted, [], StepOutcomes.NotAttempted("streaming-list", "nothing")));
        fake.NextDiscovery(_ => throw new InvalidOperationException("a platform that breaks its word"));
        fake.NextDiscovery(FakeStreamingPlatform.Found(Device("phone")));
        using StreamingCoordinator coordinator = Make(fake);

        StreamingDiscovery failed = await coordinator.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(StreamingDiscoveryStatus.Failed, failed.Status);
        Assert.AreEqual(unchecked((int)0x80070005), failed.Step.Code);
        Assert.AreEqual("Could not read the list of paired devices.", coordinator.Menu.Items[0].Text);

        await coordinator.RefreshAsync(CancellationToken.None);
        StreamingDiscovery threw = await coordinator.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(StreamingDiscoveryStatus.Failed, threw.Status);
        Assert.AreEqual(nameof(InvalidOperationException), threw.Step.Detail);

        StreamingDiscovery good = await coordinator.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(StreamingDiscoveryStatus.Ok, good.Status);
        Assert.AreEqual(4, fake.CallsNamed("List").Count);
        Assert.AreEqual("Test Phone", coordinator.Menu.Items[0].Text);
        Assert.IsTrue(coordinator.Menu.Items[^1].Enabled, "Refresh the list stays clickable after every kind of read.");
    }

    // A rule that throws, or a Changed handler that throws, must not leave a read counted as in flight: the menu would
    // say it is looking for devices for good. And when the rule cannot say which device is the managed one, none is
    // offered.
    [TestMethod]
    public async Task ARuleOrAHandlerThatThrowsLeavesNoReadInFlightAndOffersNothing()
    {
        var fake = new FakeStreamingPlatform();
        fake.NextDiscovery(FakeStreamingPlatform.Found(Device("phone")));
        bool ruleThrows = true;
        using StreamingCoordinator coordinator = Make(fake, isExcluded: _ => ruleThrows ? throw new InvalidOperationException("the host rule broke") : false);

        StreamingDiscovery broken = await coordinator.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(StreamingDiscoveryStatus.Failed, broken.Status);
        Assert.AreEqual(nameof(InvalidOperationException), broken.Step.Detail);
        Assert.AreEqual(0, broken.Devices.Count);
        CollectionAssert.AreEqual(
            Sequence.Of("Could not read the list of paired devices.", "Refresh the list"),
            coordinator.Menu.Items.Select(i => i.Text).ToArray());

        ruleThrows = false;
        bool handlerThrows = true;
        coordinator.Changed += (_, _) =>
        {
            if (handlerThrows)
            {
                handlerThrows = false;
                throw new InvalidOperationException("a handler broke");
            }
        };

        await coordinator.RefreshAsync(CancellationToken.None);
        await coordinator.RefreshAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            Sequence.Of("Test Phone", "Refresh the list"),
            coordinator.Menu.Items.Select(i => i.Text).ToArray(),
            "No read is left counted as in flight, so the menu is not stuck looking for devices.");
    }

    [TestMethod]
    public async Task DisposeLetsGoOfEverythingAndOfTheEvent()
    {
        var fake = new FakeStreamingPlatform();
        StreamingCoordinator coordinator = await MakeWithListAsync(fake, Device("phone"));
        await coordinator.StartPlayingAsync("phone", CancellationToken.None);
        Assert.IsTrue(fake.HasLinkSubscribers);

        coordinator.Dispose();
        coordinator.Dispose();

        Assert.AreEqual(1, fake.CallsNamed("Release(").Count);
        Assert.IsFalse(fake.HasLinkSubscribers);
        StreamingOpenOutcome after = await coordinator.StartPlayingAsync("phone", CancellationToken.None);
        Assert.AreEqual(StreamingOpenStatus.NotStarted, after.Status);
    }

    [TestMethod]
    public void LimitsOutsideTheirRangeAreReplacedAndTheCoordinatorSaysSo()
    {
        var fake = new FakeStreamingPlatform();
        using StreamingCoordinator coordinator = Make(fake, settings: StreamingSettings.Default with { OpenTimeoutSeconds = 0 });

        Assert.AreEqual(1, coordinator.SettingsNotes.Count);
        StringAssert.Contains(coordinator.SettingsNotes[0].Step, "OpenTimeoutSeconds");
    }

    internal sealed record FailureRow(string Row, IReadOnlyList<string> Seen, IReadOnlyList<string> MenuTexts, StreamingOpenOutcome? Outcome);

    // Drives every row of the failure table and returns, for each, what the owner is shown: the sentence or
    // sentences in the menu for a row about the list, and the status line for every other row.
    internal static async Task<IReadOnlyList<FailureRow>> DriveEveryFailureRowAsync(string id, string name)
    {
        var rows = new List<FailureRow>();

        async Task StartRowAsync(string row, Action<FakeStreamingPlatform> script, StreamingSupport support = StreamingSupport.Supported)
        {
            var fake = new FakeStreamingPlatform { Support = support };
            script(fake);
            fake.NextDiscovery(FakeStreamingPlatform.Found(Device(id, name)));
            using StreamingCoordinator coordinator = Make(fake);
            await coordinator.RefreshAsync(CancellationToken.None);
            StreamingOpenOutcome outcome = await coordinator.StartPlayingAsync(id, CancellationToken.None);
            rows.Add(new FailureRow(row, [coordinator.StatusLine], coordinator.Menu.Items.Select(i => i.Text).ToArray(), outcome));
        }

        async Task ListRowAsync(string row, StreamingDiscovery discovery)
        {
            var fake = new FakeStreamingPlatform();
            fake.NextDiscovery(discovery);
            using StreamingCoordinator coordinator = Make(fake);
            await coordinator.RefreshAsync(CancellationToken.None);
            string[] sentences = coordinator.Menu.Items.Where(i => i.Command == StreamingMenuCommand.None).Select(i => i.Text).ToArray();
            rows.Add(new FailureRow(row, sentences, coordinator.Menu.Items.Select(i => i.Text).ToArray(), null));
        }

        await StartRowAsync("windows-too-old", _ => { }, StreamingSupport.BuildTooOld);
        await StartRowAsync("type-missing", _ => { }, StreamingSupport.TypeMissing);
        await ListRowAsync("enumeration-throws", FakeStreamingPlatform.ListFailed(unchecked((int)0x80070005)));
        await ListRowAsync("enumeration-empty", FakeStreamingPlatform.Found());
        await StartRowAsync("create-returns-null", f => f.EnableAnswers(id, StreamingEnableStatus.NotStreamCapable));
        await StartRowAsync("start-throws", f => f.EnableAnswers(id, StreamingEnableStatus.StartFailed, unchecked((int)0x80070490)));
        await StartRowAsync("open-timed-out", f => f.OpenAnswers(id, StreamingOpenStatus.TimedOut));
        await StartRowAsync("open-denied", f => f.OpenAnswers(id, StreamingOpenStatus.DeniedBySystem));
        await StartRowAsync("open-unknown-failure", f => f.OpenAnswers(id, StreamingOpenStatus.UnknownFailure, unchecked((int)0x8007048F)));
        await StartRowAsync("open-throws", f => f.OpenAnswers(id, StreamingOpenStatus.CallFailed, unchecked((int)0x80004005)));

        // Earshot's own limit runs out first.
        {
            var clock = new TestTimeProvider();
            var fake = new FakeStreamingPlatform();
            fake.OpenNeverAnswers(id);
            fake.NextDiscovery(FakeStreamingPlatform.Found(Device(id, name)));
            using StreamingCoordinator coordinator = Make(fake, time: clock);
            await coordinator.RefreshAsync(CancellationToken.None);
            Task<StreamingOpenOutcome> start = coordinator.StartPlayingAsync(id, CancellationToken.None);
            await fake.OpenEntered(id).WaitAsync(Guard);
            clock.Advance(TimeSpan.FromSeconds(StreamingSettings.DefaultOpenTimeoutSeconds + 1));
            StreamingOpenOutcome outcome = await start.WaitAsync(Guard);
            rows.Add(new FailureRow("local-timeout", [coordinator.StatusLine], coordinator.Menu.Items.Select(i => i.Text).ToArray(), outcome));
        }

        // The device goes away while it is open: Windows reports the link closed, and the next read drops it.
        {
            var fake = new FakeStreamingPlatform();
            fake.NextDiscovery(FakeStreamingPlatform.Found(Device(id, name)));
            fake.NextDiscovery(FakeStreamingPlatform.Found());
            using StreamingCoordinator coordinator = Make(fake);
            await coordinator.RefreshAsync(CancellationToken.None);
            StreamingOpenOutcome outcome = await coordinator.StartPlayingAsync(id, CancellationToken.None);
            fake.RaiseLink(id, StreamingLinkState.Closed);
            string seen = coordinator.StatusLine;
            Assert.IsEmpty(fake.CallsNamed("Release("), "A closed link alone lets go of nothing: the phone may come back by itself.");
            await coordinator.RefreshAsync(CancellationToken.None);
            Assert.IsFalse(coordinator.Menu.Items.Any(i => i.Command == StreamingMenuCommand.Play), "The next read drops a device that has gone.");
            Assert.AreEqual("Release(" + id + ")", fake.Calls[^1], "A device in use that a good read no longer holds is let go.");
            Assert.IsFalse(coordinator.Menu.Items.Any(i => i.Command == StreamingMenuCommand.Stop));
            Assert.AreEqual(seen, coordinator.StatusLine);
            rows.Add(new FailureRow("device-disappears", [seen], coordinator.Menu.Items.Select(i => i.Text).ToArray(), outcome));
        }

        // The busy gate says busy.
        {
            var fake = new FakeStreamingPlatform();
            var gate = new ManualBusyGate();
            gate.Hold();
            fake.NextDiscovery(FakeStreamingPlatform.Found(Device(id, name)));
            using StreamingCoordinator coordinator = Make(fake, gate);
            await coordinator.RefreshAsync(CancellationToken.None);
            StreamingOpenOutcome outcome = await coordinator.StartPlayingAsync(id, CancellationToken.None);
            rows.Add(new FailureRow("busy", [coordinator.StatusLine], coordinator.Menu.Items.Select(i => i.Text).ToArray(), outcome));
        }

        return rows;
    }
}
