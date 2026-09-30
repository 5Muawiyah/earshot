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

    // One set of AirPods as two senders: each bud reports its own level first, so the two bud nibbles arrive in
    // opposite order, and one status bit differs. Invented bytes; the case level and the two bud levels are shared.
    private void SendBud(uint sender, sbyte rssi, bool firstOrder, byte colour = WidgetFixtures.Colour, byte batteryB = 0x09, byte pairHigh = 0x7, byte pairLow = 0x4)
    {
        byte batteryA = firstOrder ? (byte)((pairHigh << 4) | pairLow) : (byte)((pairLow << 4) | pairHigh);
        byte status = firstOrder ? (byte)0x40 : (byte)0x60;
        Send(sender, rssi, WidgetFixtures.Proximity(status: status, batteryA: batteryA, batteryB: batteryB, colour: colour));
    }

    // A different set: other bud levels, so it cannot be mistaken for the buds above.
    private void SendOtherSet(uint sender, sbyte rssi, byte batteryA = 0x31, byte batteryB = 0x02, byte colour = WidgetFixtures.Colour, byte modelLow = WidgetFixtures.ModelLow) =>
        Send(sender, rssi, WidgetFixtures.Proximity(modelLow: modelLow, status: 0x40, batteryA: batteryA, batteryB: batteryB, colour: colour));

    private void SendOtherSetMany(uint sender, params sbyte[] rssis)
    {
        foreach (sbyte rssi in rssis)
        {
            SendOtherSet(sender, rssi);
        }
    }

    private static readonly string[] ExpectedTags = ["0A0B0C0D", "01020304"];

    private void Tick(int milliseconds) => _clock.Advance(TimeSpan.FromMilliseconds(milliseconds));

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

    // The owner's case: the two buds of one set are two senders, and a short-form sender (his phone) is stronger
    // than either. The buds are one candidate, the short form is neither a candidate nor a competitor.
    [TestMethod]
    public async Task TwoBudsOfOneSetAndAStrongerShortFormSenderAreOneCandidate()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                SendBud(1, -61, firstOrder: true);
                SendBud(2, -58, firstOrder: false);
                Send(3, -52, WidgetFixtures.UnknownSeventeenByteForm());
                Tick(500);
            }
        });

        Assert.AreEqual(BatterySetupListenStatus.Found, listen.Status);
        BatterySetupCandidate candidate = listen.Candidate!;
        Assert.AreEqual(40, candidate.Messages, "Both buds' messages, and none of the short form's.");
        Assert.AreEqual((sbyte)-61, candidate.RssiMin);
        Assert.AreEqual((sbyte)-58, candidate.RssiMax);
        Assert.AreEqual((sbyte)-71, candidate.ThresholdDbm, "Ten decibels under the weakest bud message.");
        Assert.IsEmpty(listen.OtherSenders, "The short form is not a competing set.");
    }

    // Only the documented form can be the candidate: a phone's short-form messages, however many and however strong,
    // are not AirPods, and nothing documented was heard.
    [TestMethod]
    public async Task OnlyShortFormSendersIsNotFound()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            for (int i = 0; i < 6; i++)
            {
                Send(1, -50, WidgetFixtures.UnknownSeventeenByteForm());
                Send(2, -80, WidgetFixtures.UnknownSeventeenByteForm());
            }
        });

        Assert.AreEqual(BatterySetupListenStatus.NotFound, listen.Status);
        Assert.AreEqual(WidgetCopy.SetupNotFound, listen.Message);
        Assert.IsNull(listen.Candidate);
    }

    // A different set within ten decibels still refuses, exactly as before: merging is for the buds of one set only.
    [TestMethod]
    public async Task TwoDifferentSetsWithinTenDecibelsAreStillAmbiguous()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            for (int i = 0; i < 10; i++)
            {
                SendBud(1, -60, firstOrder: true);
                SendBud(2, -60, firstOrder: false);
                SendOtherSet(3, -66);
                Tick(500);
            }
        });

        Assert.AreEqual(BatterySetupListenStatus.Ambiguous, listen.Status);
        Assert.IsNull(listen.Candidate);
        Assert.IsTrue(_log.Has(Earshot.Contracts.LogLevel.Warn, "two senders too close in signal"));
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
            SendOtherSetMany(2, -90, -88, -75, -89, -87);      // a louder single message at -75, but a median of -88
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
            SendOtherSetMany(2, -90, -88, -30, -89, -87);
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
            SendOtherSetMany(2, -69, -69, -69);   // nine decibels under: too close
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
            SendOtherSetMany(2, -70, -70, -70);
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
            SendOtherSetMany(2, -90, -85, -70);        // median -85 is far enough, but one message reaches -70
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
            SendOtherSet(2, -65);
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
            SendOtherSetMany(2, -95, -92, -96, -94);   // same model and colour (the fixture's), other levels, far away
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
        Assert.AreEqual(3, listen.Candidate.Messages, "The candidate's messages are documented-form ones only.");
        Assert.AreEqual(0, listen.Candidate.OtherFormMessages);
        Assert.HasCount(2, listen.Candidate.ShortFormCaptures!, "The short form is kept as evidence, not counted.");
    }

    [TestMethod]
    public async Task CapturesHoldNineBytesForTheDocumentedFormAndTheWholeValueOtherwise()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            Send(1, -55);
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

        BatterySetupCapture other = listen.Candidate.ShortFormCaptures![0];
        Assert.AreEqual((byte)0x06, other.Prefix);
        Assert.AreEqual(17, other.Length);
        Assert.AreEqual(Convert.ToHexString(WidgetFixtures.UnknownSeventeenByteForm()[2..]), other.ValueHex, "Any other form is kept whole.");

        // The sixteen encrypted bytes of the documented form are never copied.
        string encrypted = Convert.ToHexString(WidgetFixtures.ProximityValue()[9..13]);
        Assert.DoesNotContain(encrypted, documented.ValueHex);
    }

    [TestMethod]
    public async Task OtherSetsAreCountsOnlyAndAShortFormSenderIsEvidenceOnly()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            SendMany(1, -50, -50, -50);
            SendOtherSetMany(2, -95, -90, -92);
            Send(3, -99, WidgetFixtures.UnknownSeventeenByteForm());
        });

        Assert.AreEqual(BatterySetupListenStatus.Found, listen.Status);
        Assert.HasCount(1, listen.OtherSenders, "The short-form sender is not a set.");
        BatterySetupSenderSummary three = listen.OtherSenders[0];
        Assert.AreEqual(3, three.Messages);
        Assert.AreEqual(3, three.OkFormMessages);
        Assert.AreEqual((sbyte)-92, three.RssiMedian);
        Assert.AreEqual(3, listen.Candidate!.Messages, "No message of another sender is among the candidate's.");
        Assert.HasCount(1, listen.Candidate.ShortFormCaptures!);
        Assert.AreEqual(7, listen.ProximityItemsSeen, "Every proximity item is counted, whoever sent it.");
        Assert.AreEqual(7, listen.AppleSectionsSeen);
    }

    // The merge is the record's business as much as the decision's: every sender's tag is listed, every capture says
    // which sender it came from, and the newest message of each sender is kept so the proof can see both buds.
    [TestMethod]
    public async Task ARecordListsEachMergedSendersTagAndItsNewestMessage()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            for (int i = 0; i < 4; i++)
            {
                SendBud(0x0A0B0C0D, -60, firstOrder: true);
                SendBud(0x01020304, -60, firstOrder: false);
                Tick(300);
            }

            Send(0x11121314, -55, WidgetFixtures.UnknownSeventeenByteForm());
        });

        BatterySetupCandidate candidate = listen.Candidate!;
        CollectionAssert.AreEqual(ExpectedTags, candidate.SenderTags!.ToArray(), "In the order the senders were first heard.");
        IReadOnlyList<ProximityMessage> newest = candidate.SenderLastOkMessages!;
        Assert.HasCount(2, newest);
        Assert.AreEqual((byte)0x40, newest[0].Status);
        Assert.AreEqual((byte)0x60, newest[1].Status);
        Assert.AreEqual((byte)0x74, newest[0].BatteryA);
        Assert.AreEqual((byte)0x47, newest[1].BatteryA);
        Assert.AreEqual(newest[1], candidate.LastOkMessage, "The newest of all is the second bud's.");
        Assert.AreEqual(8, candidate.Captures.Count(c => c.SenderTag == "0A0B0C0D") + candidate.Captures.Count(c => c.SenderTag == "01020304"));
        Assert.AreEqual("11121314", candidate.ShortFormCaptures![0].SenderTag);
    }

    // The two buds need a pair of messages close together with the same levels; two seconds is the bound, inclusive.
    [TestMethod]
    public async Task BudsThatNeverSpeakWithinTwoSecondsOfEachOtherAreNotOneSet()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            for (int i = 0; i < 4; i++)
            {
                SendBud(1, -60, firstOrder: true);
                Tick(2001);
                SendBud(2, -60, firstOrder: false);
                Tick(2001);
            }
        });

        Assert.AreEqual(BatterySetupListenStatus.Ambiguous, listen.Status, "Two sets that cannot be told apart.");
    }

    [TestMethod]
    public async Task BudsTwoSecondsApartAreOneSet()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            for (int i = 0; i < 2; i++)
            {
                SendBud(1, -60, firstOrder: true);
                SendBud(1, -60, firstOrder: true);
                Tick(2000);
                SendBud(2, -60, firstOrder: false);
                SendBud(2, -60, firstOrder: false);
                Tick(5000);
            }
        });

        Assert.AreEqual(BatterySetupListenStatus.Found, listen.Status);
        Assert.AreEqual(8, listen.Candidate!.Messages);
    }

    // A sender joins a set through any one member of it: the first and third never speak within two seconds of each
    // other, but each does with the second.
    [TestMethod]
    public async Task TheMergeIsTransitive()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            for (int i = 0; i < 2; i++)
            {
                SendBud(1, -60, firstOrder: true);
                Tick(1500);
                SendBud(2, -60, firstOrder: false);
                Tick(1500);
                SendBud(3, -60, firstOrder: true);
                Tick(3000);
            }
        });

        Assert.AreEqual(BatterySetupListenStatus.Found, listen.Status);
        Assert.AreEqual(6, listen.Candidate!.Messages);
        Assert.HasCount(3, listen.Candidate.SenderTags!);
    }

    // Same bud levels heard together but another colour, another model, another case level or other bud levels is
    // another set: each of these two senders stands alone and they are too close in signal to tell apart.
    [TestMethod]
    public async Task ADifferentColourModelCaseLevelOrBudLevelsIsNotTheSameSet()
    {
        (string Name, Action<BatterySetupFlowTests> Other)[] cases =
        [
            ("colour", t => { for (int i = 0; i < 4; i++) { t.SendBud(2, -62, firstOrder: false, colour: WidgetFixtures.StrangerColour); } }),
            ("model", t => { for (int i = 0; i < 4; i++) { t.Send(2, -62, WidgetFixtures.Proximity(modelLow: WidgetFixtures.StrangerModelLow, status: 0x60, batteryA: 0x47, batteryB: 0x09)); } }),
            ("case level", t => { for (int i = 0; i < 4; i++) { t.SendBud(2, -62, firstOrder: false, batteryB: 0x08); } }),
            ("bud levels", t => { for (int i = 0; i < 4; i++) { t.SendBud(2, -62, firstOrder: false, pairHigh: 0x7, pairLow: 0x3); } }),
        ];

        foreach ((string name, Action<BatterySetupFlowTests> other) in cases)
        {
            Dispose();
            Setup();
            BatterySetupListen listen = await RunAsync(() =>
            {
                for (int i = 0; i < 4; i++)
                {
                    SendBud(1, -60, firstOrder: true);
                }

                other(this);
            });

            Assert.AreEqual(BatterySetupListenStatus.Ambiguous, listen.Status, "A different " + name + " is a different set.");
        }
    }

    // A set that is one bud only (the other is in the case, or out of range) is still a candidate.
    [TestMethod]
    public async Task OneBudAloneIsACandidate()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            for (int i = 0; i < 5; i++)
            {
                SendBud(1, -60, firstOrder: true);
            }
        });

        Assert.AreEqual(BatterySetupListenStatus.Found, listen.Status);
        Assert.HasCount(1, listen.Candidate!.SenderTags!);
        Assert.HasCount(1, listen.Candidate.SenderLastOkMessages!);
    }

    // The merged candidate is measured across both buds, and the threshold follows the weakest of either.
    [TestMethod]
    public async Task TheMergedCandidatesSignalCoversBothBuds()
    {
        BatterySetupListen listen = await RunAsync(() =>
        {
            SendBud(1, -50, firstOrder: true);
            SendBud(2, -70, firstOrder: false);
            SendBud(1, -52, firstOrder: true);
            SendBud(2, -72, firstOrder: false);
        });

        BatterySetupCandidate candidate = listen.Candidate!;
        Assert.AreEqual((sbyte)-72, candidate.RssiMin);
        Assert.AreEqual((sbyte)-50, candidate.RssiMax);
        Assert.AreEqual((sbyte)-70, candidate.RssiMedian, "The lower of the middle two of four.");
        Assert.AreEqual((sbyte)-82, candidate.ThresholdDbm);
    }

    // The owner's log lines, for a case like his: two buds of one set and his phone, the phone the strongest sender.
    [TestMethod]
    public async Task TheLogSaysWhatWasMergedAndWhatWasIgnored()
    {
        _ = await RunAsync(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                SendBud(1, -61, firstOrder: true);
                SendBud(2, -58, firstOrder: false);
                Send(3, -52, WidgetFixtures.UnknownSeventeenByteForm());
                Send(4, -93, WidgetFixtures.UnknownSeventeenByteForm());
                Tick(500);
            }
        });

        Assert.IsTrue(_log.Has(
            Earshot.Contracts.LogLevel.Info,
            "Battery set-up: 4 senders (2 documented form in 1 sets, 2 other form ignored with 40 messages); candidate 40 messages from 2 senders, " +
            "signal min/median/max -61/-61/-58 dBm, runner-up median none; threshold -71 dBm."));
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
            SendOtherSetMany(7, -95, -95, -95);
        });

        foreach (LogEntry entry in _log.Entries)
        {
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(entry.Message, "[0-9A-F]{5,}"), "A hex run or a tag in: " + entry.Message);
            Assert.DoesNotContain("EEEE", entry.Message);
            Assert.DoesNotContain("4242424242", entry.Message);
        }
    }
}
