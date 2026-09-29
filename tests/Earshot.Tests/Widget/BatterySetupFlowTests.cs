using Earshot.Contracts;
using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Step 1 of the battery set-up, against a fake advertisement source and a fake clock: who is the candidate,
// what threshold follows from it, and what is kept as evidence. Nothing here is read from a device.
[TestClass]
public sealed class BatterySetupFlowTests : IDisposable
{
    private static readonly TimeSpan Window = WidgetTiming.SetupListenWindow;

    private FakeAdvertisementSource _source = null!;
    private TestTimeProvider _clock = null!;
    private CapturingLog _log = null!;

    public void Dispose() => _source.Dispose();

    [TestInitialize]
    public void Setup()
    {
        _source = new FakeAdvertisementSource { State = AdvertisementSourceState.Started };
        _clock = new TestTimeProvider();
        _log = new CapturingLog();
    }

    private void Send(uint sender, sbyte rssi, byte[]? section = null) =>
        _source.Raise(new AdvertisementSample(ProximityParser.AppleCompanyId, section ?? WidgetFixtures.Proximity(batteryA: 0x86, batteryB: 0x05), rssi, _clock.GetUtcNow(), sender));

    private void SendMany(uint sender, params sbyte[] rssis)
    {
        foreach (sbyte rssi in rssis)
        {
            Send(sender, rssi);
        }
    }

    private async Task<BatterySetupListen> RunAsync(Action arrange, CancellationToken ct = default)
    {
        Task<BatterySetupListen> task = BatterySetupFlow.ListenAsync(_source, _clock, Window, _log, ct);
        arrange();
        _clock.Advance(Window);
        return await task;
    }

    [TestMethod]
    public async Task AWatcherThatIsNotStartedIsRefusedBeforeListening()
    {
        _source.State = AdvertisementSourceState.Stopped;

        BatterySetupListen listen = await BatterySetupFlow.ListenAsync(_source, _clock, Window, _log, CancellationToken.None);

        Assert.AreEqual(BatterySetupListenStatus.WatcherNotStarted, listen.Status);
        Assert.AreEqual(WidgetCopy.SetupBluetoothOff, listen.Message);
        Assert.IsNull(listen.Candidate);
        Assert.AreEqual(0, _clock.TimersCreated, "It never started waiting.");
    }

    // Bluetooth goes off, or the machine sleeps, while the window is open: the watcher stops, so what the window
    // heard says nothing about whether the AirPods are near. The answer is "Bluetooth is off", not "Couldn't find
    // your AirPods", and it does not wait for the rest of the window.
    [TestMethod]
    public async Task AWatcherThatStopsMidWindowIsBluetoothOffNotNotFound()
    {
        Task<BatterySetupListen> task = BatterySetupFlow.ListenAsync(_source, _clock, Window, _log, CancellationToken.None);
        SendMany(1, -50, -50, -50);
        _source.State = AdvertisementSourceState.Stopped;
        _source.RaiseStopped(1, "RadioNotAvailable", StepOutcomes.FromWin32("fake-stop", 1));

        BatterySetupListen listen = await task;

        Assert.AreEqual(BatterySetupListenStatus.WatcherNotStarted, listen.Status);
        Assert.AreEqual(WidgetCopy.SetupBluetoothOff, listen.Message);
        Assert.IsNull(listen.Candidate, "What a deaf window heard is not evidence.");
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Warn, "the watcher stopped while listening: RadioNotAvailable (1)"));
        Assert.AreEqual(0, _source.ReceivedHandlerCount, "The listener let go.");
    }

    [TestMethod]
    public async Task AWatcherThatIsNoLongerStartedAtTheEndOfTheWindowIsBluetoothOffToo()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            SendMany(1, -50, -50, -50);
            _source.State = AdvertisementSourceState.Stopped;
        });

        Assert.AreEqual(BatterySetupListenStatus.WatcherNotStarted, listen.Status);
        Assert.AreEqual(WidgetCopy.SetupBluetoothOff, listen.Message);
    }

    [TestMethod]
    public async Task NothingInTheWindowIsNotFound()
    {
        BatterySetupListen listen = await RunAsync(() => { });

        Assert.AreEqual(BatterySetupListenStatus.NotFound, listen.Status);
        Assert.AreEqual(WidgetCopy.SetupNotFound, listen.Message);
        Assert.IsNull(listen.Candidate);
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Warn, "no AirPods found in 20 s"));
    }

    [TestMethod]
    public async Task ASenderWithTwoMessagesIsNotACandidate()
    {
        BatterySetupListen listen = await RunAsync(() => SendMany(1, -50, -50));

        Assert.AreEqual(BatterySetupListenStatus.NotFound, listen.Status, "One and two messages are a passer-by.");
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Warn, "1 senders below 3 messages"));
    }

    [TestMethod]
    public async Task ThreeMessagesMakeACandidate()
    {
        BatterySetupListen listen = await RunAsync(() => SendMany(1, -50, -50, -50));

        Assert.AreEqual(BatterySetupListenStatus.Found, listen.Status);
        Assert.AreEqual(3, listen.Candidate!.Messages);
    }

    // The middle value chooses the candidate, not the loudest single message: one spike from a passing device
    // must not win.
    [TestMethod]
    public async Task TheStrongestMedianIsTheCandidate()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            SendMany(1, -55, -56, -57, -58);           // the owner's case: median -57 (lower of the middle two)
            SendMany(2, -90, -88, -75, -89, -87);      // a louder single message at -75, but a median of -88
        });

        Assert.AreEqual(BatterySetupListenStatus.Found, listen.Status);
        Assert.AreEqual((sbyte)-57, listen.Candidate!.RssiMedian);
        Assert.AreEqual(4, listen.Candidate.Messages, "The candidate is sender 1, not the one with more messages.");
        Assert.HasCount(1, listen.OtherSenders);
        Assert.AreEqual((sbyte)-88, listen.OtherSenders[0].RssiMedian);
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Info, "runner-up median -88 dBm"));
    }

    // One spike from a passing device does not choose the candidate (the middle value does), but a message that
    // loud would pass the run-time signal check, so nothing is claimed.
    [TestMethod]
    public async Task ASpikeFromAPassingDeviceDoesNotChooseTheCandidateButBlocksTheClaim()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            SendMany(1, -55, -56, -57, -58);
            SendMany(2, -90, -88, -30, -89, -87);
        });

        Assert.AreEqual(BatterySetupListenStatus.Ambiguous, listen.Status);
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Info, "candidate 4 messages"), "The candidate was still chosen by its median.");
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Warn, "a sender clears the threshold"));
    }

    [TestMethod]
    public async Task MediansWithinTenDecibelsAreAmbiguous()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            SendMany(1, -60, -60, -60);
            SendMany(2, -69, -69, -69);   // nine decibels under: too close
        });

        Assert.AreEqual(BatterySetupListenStatus.Ambiguous, listen.Status);
        Assert.AreEqual(WidgetCopy.SetupAmbiguous, listen.Message);
        Assert.IsNull(listen.Candidate, "Nothing is claimed from two senders that cannot be told apart.");
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Warn, "two senders too close in signal (medians -60 and -69 dBm)"));
    }

    // Ten decibels clear passes the separation rule, but the runner-up's own loudest message then reaches the
    // threshold that is ten decibels under the candidate's weakest, so it is refused for that reason.
    [TestMethod]
    public async Task MediansTenDecibelsApartAreStillRefusedByTheThresholdRule()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            SendMany(1, -60, -60, -60);
            SendMany(2, -70, -70, -70);
        });

        Assert.AreEqual(BatterySetupListenStatus.Ambiguous, listen.Status);
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Warn, "a sender clears the threshold (-70 dBm)"));
    }

    [TestMethod]
    public async Task TheThresholdIsTheCandidatesMinimumMinusTen()
    {
        BatterySetupListen listen = await RunAsync(() => SendMany(1, -60, -55, -58, -52));

        Assert.AreEqual((sbyte)-60, listen.Candidate!.RssiMin);
        Assert.AreEqual((sbyte)-52, listen.Candidate.RssiMax);
        Assert.AreEqual((sbyte)-70, listen.Candidate.ThresholdDbm, "Ten decibels under the weakest message.");
    }

    [TestMethod]
    public async Task TheThresholdIsClampedAtTheBottomOfTheSignalRange()
    {
        BatterySetupListen listen = await RunAsync(() => SendMany(1, -125, -120, -122));

        // The floor is the weakest signal a record or a claim may hold (SetupRules.WeakestSignalDbm), so the record a
        // set-up writes is one it can read back.
        Assert.AreEqual((sbyte)SetupRules.WeakestSignalDbm, listen.Candidate!.ThresholdDbm);
    }

    // A stranger that would pass the run-time signal check right now makes the threshold useless.
    [TestMethod]
    public async Task ARunnerUpAtTheThresholdIsAmbiguous()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            SendMany(1, -60, -58, -55);        // candidate: threshold -70
            SendMany(2, -90, -85, -70);        // median -85 is far enough, but one message reaches -70
        });

        Assert.AreEqual(BatterySetupListenStatus.Ambiguous, listen.Status);
        Assert.IsNull(listen.Candidate);
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Warn, "a sender clears the threshold (-70 dBm) beside the candidate; nothing claimed"));
    }

    // Even a single message from another sender counts against the threshold: it is not a candidate, but it
    // would pass the run-time check.
    [TestMethod]
    public async Task AOneMessageStrangerAtTheThresholdIsAmbiguousToo()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            SendMany(1, -60, -58, -55);
            Send(2, -65);
        });

        Assert.AreEqual(BatterySetupListenStatus.Ambiguous, listen.Status);
    }

    // The owner's accepted residual risk, exactly as documented: a stranger of the same model that is much
    // weaker than the case never blocks the set-up and is not the candidate.
    [TestMethod]
    public async Task AFarAwayStrangerOfTheSameModelIsNeitherTheCandidateNorAnObstacle()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            SendMany(1, -55, -56, -57, -55);
            SendMany(2, -95, -92, -96, -94);   // same model and colour (the fixture's), far away
        });

        Assert.AreEqual(BatterySetupListenStatus.Found, listen.Status);
        Assert.AreEqual(4, listen.Candidate!.Messages);
        Assert.AreEqual((sbyte)-55, listen.Candidate.RssiMax, "Only the candidate's own signal figures.");
        Assert.HasCount(1, listen.OtherSenders);
        Assert.AreEqual(4, listen.OtherSenders[0].Messages);
    }

    // ... and after the claim, at run time, that stranger's residual risk is the one the docs state: a
    // same-model message above the threshold with an equal, lower or one-step-higher battery passes as the
    // owner's; anything more than one step higher does not (while not charging).
    [TestMethod]
    public async Task ASameModelStrangerAboveTheThresholdWithAConsistentBatteryStillPassesAsTheOwners()
    {
        BatterySetupListen listen = await RunAsync(() => SendMany(1, -55, -56, -57));
        ProximityMessage claimed = listen.Candidate!.LastOkMessage!.Value;
        var claim = SetupRecordFixtures.Claim(OwnedBattery.FromMessage(claimed, ProximityDecodeTable.Unproved, null, SetupRecordFixtures.Start), threshold: listen.Candidate.ThresholdDbm);

        // Lower battery on both buds, stronger than the threshold: passes.
        ProximityMessage lower = SetupRecordFixtures.Message(high: 6, low: 4, caseNibble: 3);
        OwnershipResult lowerResult = OwnershipRule.Evaluate(new OwnershipInput(
            new ProximityParse(ProximityParseStatus.Ok, lower, null, null, 1, []), -60, claim, ProximityDecodeTable.Unproved, SetupRecordFixtures.Start));
        Assert.AreEqual(OwnershipVerdict.Owned, lowerResult.Verdict, "The documented residual risk: a same-model stranger with a lower battery passes.");

        // Much higher battery: does not.
        ProximityMessage higher = SetupRecordFixtures.Message(high: 10, low: 10, caseNibble: 10);
        OwnershipResult higherResult = OwnershipRule.Evaluate(new OwnershipInput(
            new ProximityParse(ProximityParseStatus.Ok, higher, null, null, 1, []), -60, claim, ProximityDecodeTable.Unproved, SetupRecordFixtures.Start));
        Assert.AreEqual(OwnershipVerdict.BatteryInconsistent, higherResult.Verdict);

        // Below the threshold: does not, whatever its battery.
        OwnershipResult farResult = OwnershipRule.Evaluate(new OwnershipInput(
            new ProximityParse(ProximityParseStatus.Ok, lower, null, null, 1, []), -80, claim, ProximityDecodeTable.Unproved, SetupRecordFixtures.Start));
        Assert.AreEqual(OwnershipVerdict.SignalBelowThreshold, farResult.Verdict);
    }

    [TestMethod]
    public async Task ACandidateWithOnlyTheShortFormIsShortFormOnlyWithItsCaptures()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            for (int i = 0; i < 4; i++)
            {
                Send(1, -55, WidgetFixtures.UnknownSeventeenByteForm());
            }
        });

        Assert.AreEqual(BatterySetupListenStatus.ShortFormOnly, listen.Status);
        BatterySetupCandidate candidate = listen.Candidate!;
        Assert.AreEqual(0, candidate.OkFormMessages);
        Assert.AreEqual(4, candidate.OtherFormMessages);
        Assert.IsNull(candidate.LastOkMessage, "Nothing to draft a claim from.");
        Assert.HasCount(4, candidate.Captures, "The captures are kept.");
        Assert.AreEqual((byte)0x06, candidate.Captures[0].Prefix);
        Assert.AreEqual(17, candidate.Captures[0].Length);
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Warn, "only forms the parser does not read (prefix 06 length 17 x4)"));
    }

    [TestMethod]
    public async Task TheClaimDraftIsTheCandidatesNewestOkMessage()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            Send(1, -55, WidgetFixtures.Proximity(batteryA: 0x11, batteryB: 0x01));
            Send(1, -55, WidgetFixtures.Proximity(batteryA: 0x22, batteryB: 0x02));
            Send(1, -55, WidgetFixtures.UnknownSeventeenByteForm());     // newer, but not readable
            Send(1, -55, WidgetFixtures.Proximity(batteryA: 0x33, batteryB: 0x03));
            Send(1, -55, WidgetFixtures.UnknownSeventeenByteForm());
        });

        Assert.AreEqual(BatterySetupListenStatus.Found, listen.Status);
        Assert.AreEqual((byte)0x33, listen.Candidate!.LastOkMessage!.Value.BatteryA, "The newest documented-form message, not merely the newest.");
        Assert.AreEqual(3, listen.Candidate.OkFormMessages);
        Assert.AreEqual(2, listen.Candidate.OtherFormMessages);
    }

    [TestMethod]
    public async Task CapturesHoldNineBytesForTheDocumentedFormAndTheWholeValueOtherwise()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            Send(1, -55);
            Send(1, -55);
            Send(1, -55, WidgetFixtures.UnknownSeventeenByteForm());
        });

        BatterySetupCapture documented = listen.Candidate!.Captures[0];
        Assert.AreEqual((byte)0x01, documented.Prefix);
        Assert.AreEqual(25, documented.Length, "The length is the item's own.");
        Assert.AreEqual(Convert.ToHexString(WidgetFixtures.ProximityValue(batteryA: 0x86, batteryB: 0x05)[..9]), documented.ValueHex,
            "Prefix to reserved: nine bytes.");
        Assert.AreEqual(18, documented.ValueHex.Length);

        BatterySetupCapture other = listen.Candidate.Captures[2];
        Assert.AreEqual((byte)0x06, other.Prefix);
        Assert.AreEqual(17, other.Length);
        Assert.AreEqual(Convert.ToHexString(WidgetFixtures.UnknownSeventeenByteForm()[2..]), other.ValueHex, "Any other form is kept whole.");

        // The sixteen encrypted bytes of the documented form are never copied.
        string encrypted = Convert.ToHexString(WidgetFixtures.ProximityValue()[9..13]);
        Assert.DoesNotContain(encrypted, documented.ValueHex);
    }

    [TestMethod]
    public async Task OtherSendersAreCountsOnly()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            SendMany(1, -50, -50, -50);
            SendMany(2, -95, -90, -92);
            Send(3, -99, WidgetFixtures.UnknownSeventeenByteForm());
        });

        Assert.AreEqual(BatterySetupListenStatus.Found, listen.Status);
        Assert.HasCount(2, listen.OtherSenders);
        BatterySetupSenderSummary three = listen.OtherSenders.Single(s => s.Messages == 3);
        Assert.AreEqual(3, three.OkFormMessages);
        Assert.AreEqual((sbyte)-92, three.RssiMedian);
        BatterySetupSenderSummary one = listen.OtherSenders.Single(s => s.Messages == 1);
        Assert.AreEqual(0, one.OkFormMessages);
        Assert.AreEqual(3, listen.Candidate!.Messages, "No message of another sender is among the candidate's.");
        Assert.AreEqual(7, listen.ProximityItemsSeen, "Every proximity item is counted, whoever sent it.");
        Assert.AreEqual(7, listen.AppleSectionsSeen);
    }

    [TestMethod]
    public async Task ANonAppleSectionIsNotCounted()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            SendMany(1, -50, -50, -50);
            _source.Raise(new AdvertisementSample(0x0006, [1, 2, 3], -40, _clock.GetUtcNow(), 9));
        });

        Assert.AreEqual(3, listen.AppleSectionsSeen);
    }

    [TestMethod]
    public async Task CancellationEndsTheWindowWithNothing()
    {
        using var cts = new CancellationTokenSource();
        Task<BatterySetupListen> task = BatterySetupFlow.ListenAsync(_source, _clock, Window, _log, cts.Token);
        SendMany(1, -50, -50, -50);

        await cts.CancelAsync();
        BatterySetupListen listen = await task;

        Assert.AreEqual(BatterySetupListenStatus.Cancelled, listen.Status);
        Assert.IsNull(listen.Candidate, "Nothing is kept from a cancelled listen.");
        Assert.AreEqual(string.Empty, listen.Message);
    }

    [TestMethod]
    public async Task TheFlowStopsListeningWhenItEnds()
    {
        Task<BatterySetupListen> task = BatterySetupFlow.ListenAsync(_source, _clock, Window, _log, CancellationToken.None);
        Assert.AreEqual(1, _source.ReceivedHandlerCount, "It listens while the window is open.");
        SendMany(1, -50, -50, -50);
        _clock.Advance(Window);
        BatterySetupListen listen = await task;

        Assert.AreEqual(BatterySetupListenStatus.Found, listen.Status);
        Assert.AreEqual(0, _source.ReceivedHandlerCount, "It lets go of the source when the window ends.");

        Task<BatterySetupListen> cancelled = BatterySetupFlow.ListenAsync(_source, _clock, Window, _log, new CancellationToken(canceled: true));
        await cancelled;
        Assert.AreEqual(0, _source.ReceivedHandlerCount, "And when it is cancelled.");
    }

    // The log lines of the flow carry numbers only: never a byte, a sender tag or a model or colour byte.
    [TestMethod]
    public async Task TheLogLinesCarryNumbersOnly()
    {
        _ = await RunAsync(() =>
        {
            SendMany(4242424242, -50, -50, -50);
            SendMany(7, -95, -95, -95);
        });

        foreach (LogEntry entry in _log.Entries)
        {
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(entry.Message, "[0-9A-F]{5,}"), "A hex run or a tag in: " + entry.Message);
            Assert.DoesNotContain("EEEE", entry.Message);
            Assert.DoesNotContain("4242424242", entry.Message);
        }
    }
}
