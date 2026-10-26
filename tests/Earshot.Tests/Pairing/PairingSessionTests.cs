using Earshot.App;
using Earshot.Bluetooth;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Earshot.Pairing;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Earshot.Tests.Phase6;
using Earshot.Tests.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Pairing;

[TestClass]
public sealed class PairingSessionTests
{
    private const string AirPodsId = "unpaired-airpods";

    // Every step with its code, and its time in the -ms step after it.
    private static readonly string[] DesignedSteps = ["radio-check", "radio-check-ms", "scan", "scan-ms", "pair", "pair-ms", "model-check", "model-check-ms", "adopt", "setup"];

    private static readonly string[] Planted = ["Jonathan", FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer.ToString(), "654321"];

    internal static RadioCheckResult Radio(RadioCheckState state, int? problem = null) =>
        new(state, RadioLines.For(state, problem), RadioLines.ActionFor(state), true, true, 1, null, problem, null, null, [], 3);

    private static WatchEvent Added(string id, string name, uint? minor = 6, bool? present = null, bool av = true, string address = "AABBCCDDEE10") =>
        new(WatchEventKind.Added, new UnpairedDevice(id, name, address, av, minor, present));

    // A new pair appears in the paired list and the node table when pairing completes, as Windows does.
    private static void ArrangePairs(FixRig rig, bool airPods = true)
    {
        rig.Pairing.OnPaired = _ =>
        {
            FakeNode[] arrived = airPods
                ? FixRig.NewAirPodsNodes()
                :
                [
                    new FakeNode(@"BTHENUM\DEV_" + FixRig.NewAirPodsAddress + @"\b&1a2b3c4d&0&BLUETOOTHDEVICE_" + FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer, "Speaker"),
                ];
            rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Concat(arrived));
            rig.Pairing.Paired.Add(new PairedDevice("paired-new", "AirPods Pro", FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer));
        };
    }

    private static void AirPodsAppear(FixRig rig, string name = "AirPods Pro") =>
        rig.Pairing.Events.Add(Added(AirPodsId, name, address: FixRig.NewAirPodsAddress));

    private static PairingSession Make(FixRig rig, IRadioCheck? radio = null) => new(rig.Pairing, rig.Nodes, rig.Ui, rig.Host().Options, rig.Log, radio);

    private static async Task<PairingSessionResult> RunAsync(FixRig rig, bool exclude = false, IRadioCheck? radio = null) =>
        await Make(rig, radio).RunAsync(exclude, CancellationToken.None);

    // ---- who is offered ----

    [TestMethod]
    public async Task OnlyHeadphonesAndWearableHeadsetsOfTheAudioClassAreOffered()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        rig.Pairing.Events.Add(Added("loudspeaker", "Kitchen speaker", minor: 20));
        rig.Pairing.Events.Add(Added("car", "Car", minor: 8));
        rig.Pairing.Events.Add(Added("phone", "Phone", minor: 6, av: false));
        rig.Pairing.Events.Add(Added("no-minor", "Unknown audio", minor: null));
        rig.Pairing.Events.Add(Added("headset", "Some headset", minor: 1));
        AirPodsAppear(rig);

        await RunAsync(rig);

        Assert.AreEqual("pick:Some headset|AirPods Pro", rig.Ui.Shown.Single(s => s.StartsWith("pick:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ADeviceReportedNotPresentIsNotOfferedButOneThatNeverReportedIsOffered()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        rig.Pairing.Events.Add(Added("gone", "Gone headphones", present: false));
        rig.Pairing.Events.Add(Added("here", "Here headphones", present: true));
        rig.Pairing.Events.Add(Added("silent", "Silent headphones", present: null));

        await RunAsync(rig);

        Assert.AreEqual("pick:Here headphones|Silent headphones", rig.Ui.Shown.Single(s => s.StartsWith("pick:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ADeviceTheWatcherRemovedBeforeThePickIsNotOffered()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        rig.Pairing.Events.Add(Added("first", "First"));
        rig.Pairing.Events.Add(Added("second", "Second"));
        rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Removed, new UnpairedDevice("first", "", "", false)));

        await RunAsync(rig);

        Assert.AreEqual("pick:Second", rig.Ui.Shown.Single(s => s.StartsWith("pick:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task TheEventsOfOneDeviceAreMergedSoALaterUpdateFillsInTheClassAndTheName()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice(AirPodsId, "", FixRig.NewAirPodsAddress, false)));
        rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Updated, new UnpairedDevice(AirPodsId, "AirPods Pro", "", true, 6, true)));

        PairingSessionResult result = await RunAsync(rig);

        Assert.AreEqual("pick:AirPods Pro", rig.Ui.Shown.Single(s => s.StartsWith("pick:", StringComparison.Ordinal)));
        Assert.IsFalse(result.NameUnread);
        Assert.AreEqual(PairOutcome.Paired, result.Outcome, string.Join("; ", result.Lines));
    }

    [TestMethod]
    public async Task PairAirPodsOffersWhatWasAlreadyListedWhenTheScanStartedAndFixDoesNot()
    {
        void Listed(FixRig rig)
        {
            ArrangePairs(rig);
            rig.Pairing.Events.Add(Added(AirPodsId, "AirPods Pro", address: FixRig.NewAirPodsAddress));
            rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.InitialListDone, null));
        }

        using var pairRig = new FixRig();
        Listed(pairRig);
        PairingSessionResult offered = await RunAsync(pairRig, exclude: false);
        Assert.AreEqual(PairOutcome.Paired, offered.Outcome, string.Join("; ", offered.Lines));

        using var fixRig = new FixRig();
        Listed(fixRig);
        PairingSessionResult hidden = await RunAsync(fixRig, exclude: true);
        Assert.AreEqual(PairFailureReason.NoDevice, hidden.Reason);
        Assert.IsFalse(fixRig.Pairing.Calls.Any(c => c.StartsWith("pair:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task OneCandidateIsStillListedAndNothingPairsUntilThePersonPressesPair()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        AirPodsAppear(rig);
        bool pairedBeforePick = true;
        rig.Ui.OnPick = () => pairedBeforePick = rig.Pairing.Calls.Any(c => c.StartsWith("pair:", StringComparison.Ordinal));
        rig.Ui.Pick = -1;

        PairingSessionResult result = await RunAsync(rig);

        Assert.AreEqual("pick:AirPods Pro", rig.Ui.Shown.Single(s => s.StartsWith("pick:", StringComparison.Ordinal)));
        Assert.IsFalse(pairedBeforePick);
        Assert.AreEqual(PairOutcome.Cancelled, result.Outcome);
        Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("pair:", StringComparison.Ordinal)), "A pick of nothing pairs nothing.");
    }

    [TestMethod]
    public async Task ABareConfirmationIsAnsweredYesOnlyAfterThePickAndAPinIsShown()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        AirPodsAppear(rig);
        rig.Pairing.Requests.Add((FixPairKind.ConfirmOnly, null));
        rig.Pairing.Requests.Add((FixPairKind.ConfirmPinMatch, "123456"));
        rig.Ui.ConfirmPin = false;

        await RunAsync(rig);

        Expect.Same(rig.Pairing.RequestAnswers, true, false);
        Expect.Same(rig.Ui.PinsShown, "ConfirmPinMatch:123456");
    }

    // ---- the pairing and what it says ----

    [TestMethod]
    public async Task AFailedPairingShowsTheTableLineForItsStatus()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        AirPodsAppear(rig);
        rig.Pairing.PairResult = new PairingCallResult("AuthenticationFailure", 9, false, null);

        PairingSessionResult result = await RunAsync(rig);

        Assert.AreEqual(PairOutcome.Failed, result.Outcome);
        Assert.AreEqual(PairFailureReason.PairFailed, result.Reason);
        Assert.AreEqual("Pairing was turned down.", result.CardLine);
        Assert.IsFalse(result.Unreachable, "A named device that fails to pair says nothing about the PC.");
        Assert.AreEqual(FailureClass.Unknown.ToString(), result.ToHistoryEntry(DateTimeOffset.UnixEpoch).FailureClass);
    }

    [TestMethod]
    public async Task AnUnreadNameFollowedByAFailedPairingIsTheOnePcSideSignal()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        AirPodsAppear(rig, name: "");
        rig.Pairing.PairResult = new PairingCallResult("AuthenticationTimeout", 7, false, null);

        PairingSessionResult result = await RunAsync(rig);

        Assert.IsTrue(result.NameUnread);
        Assert.IsTrue(result.Unreachable);
        Assert.AreEqual(RadioLines.Unreachable, result.CardLine);
        Assert.AreEqual("This PC found the AirPods but couldn't reach them.", result.CardLine);
        Assert.AreEqual(FailureClass.PcSide.ToString(), result.ToHistoryEntry(DateTimeOffset.UnixEpoch).FailureClass);
        Assert.IsTrue(result.HistorySteps.Any(s => s.Step == "name-unread" && s.Code == 1));
    }

    [TestMethod]
    public async Task AnUnreadNameIsNotUnreachableWhenThePairingWasStoppedOnPurposeOrThrew()
    {
        foreach (PairingCallResult stopped in new[]
        {
            new PairingCallResult("PairingCanceled", 14, false, null),
            new PairingCallResult("RejectedByHandler", 17, false, null),
            new PairingCallResult("COMException", unchecked((int)0x80070005), false, null, Threw: true),
        })
        {
            using var rig = new FixRig();
            ArrangePairs(rig);
            AirPodsAppear(rig, name: " ");
            rig.Pairing.PairResult = stopped;

            PairingSessionResult result = await RunAsync(rig);

            Assert.IsFalse(result.Unreachable, stopped.Name);
        }
    }

    [TestMethod]
    public async Task APairingThatNeverAnswersIsCancelledAfterTheTimeoutAndRecordedAsThat()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        AirPodsAppear(rig, name: "");
        rig.Pairing.HangOnPair = true;

        PairingSessionResult result = await RunAsync(rig);

        Assert.AreEqual(PairFailureReason.TimedOut, result.Reason);
        Assert.AreEqual("cancelled-timeout", result.PairStatus!.Name);
        Assert.AreEqual(1460, result.PairStatus.Code);
        Assert.IsFalse(result.Unreachable, "A timeout of this build's own is not the page timeout of the debug run.");
        Assert.AreEqual(PairingText.TimedOut, result.CardLine);
    }

    [TestMethod]
    public async Task ACancelledRunThrowsAndStillClosesTheWindow()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        AirPodsAppear(rig);
        rig.Pairing.HangOnPair = true;
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);

        await Assert.ThrowsAsync<OperationCanceledException>(() => Make(rig).RunAsync(false, cts.Token));

        Assert.AreEqual("closed", rig.Ui.Shown[^1]);
    }

    [TestMethod]
    public async Task NothingFoundEndsWithTheNoAirPodsLineAndNoPairing()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);

        PairingSessionResult result = await RunAsync(rig);

        Assert.AreEqual(PairFailureReason.NoDevice, result.Reason);
        Assert.AreEqual(PairingText.NothingFound, result.CardLine);
        Assert.IsTrue(result.HistorySteps.Any(s => s.Step == "scan" && s.Code == 0));
        Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("pair:", StringComparison.Ordinal)));
    }

    // ---- the model check ----

    [TestMethod]
    public async Task APairingThatPassesTheModelCheckIsOfferedForAdoption()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        AirPodsAppear(rig);

        PairingSessionResult result = await RunAsync(rig);

        Assert.AreEqual(PairOutcome.Paired, result.Outcome);
        Assert.AreEqual(FixRig.NewAirPodsContainer, result.Adoptable!.ContainerId);
        Assert.AreEqual(PairingText.SetsItselfUp, result.CardLine);
        Assert.IsTrue(result.HistorySteps.Any(s => s.Step == "model-check" && s.Code == 0));
    }

    // A pairing whose device could not be looked at is not called "not AirPods": the paired list did not hold it, or its nodes
    // had not arrived when the wait ended. Before, both read as NotAirPods, the card said "That was not AirPods", and the tray
    // cleared its recovery marker, so real AirPods could stay paired, unpinned and unblocked with nothing to offer them again.
    [TestMethod]
    public async Task APairWhoseNodesHaveNotArrivedIsUnverifiedNotNotAirPods()
    {
        using var rig = new FixRig();
        rig.Pairing.OnPaired = _ => rig.Pairing.Paired.Add(new PairedDevice("paired-new", "AirPods Pro", FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer));
        AirPodsAppear(rig);

        PairingSessionResult result = await RunAsync(rig);

        Assert.AreEqual(PairOutcome.Unverified, result.Outcome);
        Assert.IsTrue(result.Paired);
        Assert.IsNull(result.Adoptable);
        Assert.AreEqual(PairingText.Unverified, result.CardLine);
        Assert.IsTrue(result.HistorySteps.Any(s => s.Step == "model-check" && s.CodeName == "not-read"));
    }

    [TestMethod]
    public async Task APairTheListDoesNotHoldIsUnverified()
    {
        using var rig = new FixRig();
        rig.Pairing.OnPaired = _ => rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Concat(FixRig.NewAirPodsNodes()));
        AirPodsAppear(rig);

        PairingSessionResult result = await RunAsync(rig);

        Assert.AreEqual(PairOutcome.Unverified, result.Outcome);
        Assert.AreEqual(PairingText.Unverified, result.CardLine);
    }

    [TestMethod]
    public async Task ADeviceThatIsNotAirPodsIsNotUnpairedAdoptedNorHiddenAndTheResultSaysSo()
    {
        using var rig = new FixRig();
        ArrangePairs(rig, airPods: false);
        AirPodsAppear(rig, name: "Some headphones");

        PairingSessionResult result = await RunAsync(rig);

        Assert.AreEqual(PairOutcome.NotAirPods, result.Outcome);
        Assert.IsTrue(result.Paired, "It did pair, and the result says so.");
        Assert.IsNull(result.Adoptable);
        Assert.AreEqual(PairingText.NotAirPods, result.CardLine);
        Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("unpair", StringComparison.Ordinal)), "No automatic unpair.");
        Assert.IsTrue(result.HistorySteps.Any(s => s.Step == "model-check" && s.Code == 1));
        Assert.IsFalse(result.HistorySteps.Any(s => s.Step == "unpair-undo"));
        Assert.IsTrue(rig.Log.Has(LogLevel.Info, "left paired"));
    }

    // ---- the radio check, step 0 ----

    [TestMethod]
    public async Task ARadioThatBlocksEndsTheSessionBeforeAnyScanWithItsLine()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        AirPodsAppear(rig);
        var radio = new FakeRadioCheck(Radio(RadioCheckState.Off));

        PairingSessionResult result = await RunAsync(rig, radio: radio);

        Assert.AreEqual(PairFailureReason.RadioNotReady, result.Reason);
        Assert.AreEqual("Bluetooth is off.", result.CardLine);
        Assert.IsEmpty(rig.Pairing.Calls, "No scan, no pairing.");
        Assert.IsEmpty(rig.Ui.Shown, "No window.");
        Assert.AreEqual("radio-check", result.HistorySteps[0].Step);
        Assert.AreEqual(2, result.HistorySteps[0].Code);
    }

    [TestMethod]
    public async Task AnUnknownRadioClaimsNothingAndThePairingGoesOn()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        AirPodsAppear(rig);
        var radio = new FakeRadioCheck(Radio(RadioCheckState.Unknown));

        PairingSessionResult result = await RunAsync(rig, radio: radio);

        Assert.AreEqual(PairOutcome.Paired, result.Outcome);
        Assert.AreEqual(1, radio.Checks);
    }

    [TestMethod]
    public async Task ARadioReadTheCallerAlreadyMadeIsNotReadAgain()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        AirPodsAppear(rig);
        var radio = new FakeRadioCheck(Radio(RadioCheckState.Ok));
        PairingSession session = Make(rig, radio);

        RadioCheckResult? first = await session.CheckRadioAsync(CancellationToken.None);
        await session.RunAsync(false, CancellationToken.None, first);

        Assert.AreEqual(1, radio.Checks);
    }

    // ---- the history entry ----

    [TestMethod]
    public async Task TheHistoryEntryHasTheDesignedStepsAndNamesNoDevice()
    {
        using var rig = new FixRig();
        using var temp = new TempFolder();
        ArrangePairs(rig);
        AirPodsAppear(rig, name: "Jonathan's AirPods Pro");
        rig.Pairing.Requests.Add((FixPairKind.ConfirmPinMatch, "654321"));
        var radio = new FakeRadioCheck(Radio(RadioCheckState.Ok));
        PairingSessionResult result = await RunAsync(rig, radio: radio);
        var time = new ManualTime();
        var history = new ConnectHistory(Path.Combine(temp.Path, "connect-history.jsonl"), time, new CapturingLog());

        StepOutcome written = history.Append(result.ToHistoryEntry(
            time.GetUtcNow(), [new StepOutcome("adopt", true, 0, "ERROR_SUCCESS", null), new StepOutcome("setup", false, 1223, "ERROR_CANCELLED", null)]));

        Assert.IsTrue(written.Ok, written.Detail);
        ConnectHistoryEntry read = history.Read().Single();
        Assert.AreEqual("pair", read.Action);
        Assert.AreEqual("paired", read.Outcome);
        Assert.AreEqual("None", read.FailureClass);
        CollectionAssert.AreEqual(DesignedSteps, read.Steps.Select(s => s.Name).ToArray());
        Assert.AreEqual(1223, read.Steps[^1].Code);
        string raw = File.ReadAllText(history.FilePath);
        foreach (string planted in Planted)
        {
            Assert.IsFalse(raw.Contains(planted, StringComparison.OrdinalIgnoreCase), planted);
        }
    }

    [TestMethod]
    public async Task EachOutcomeHasItsHistoryWord()
    {
        using var rig = new FixRig();
        ArrangePairs(rig);
        PairingSessionResult none = await RunAsync(rig);
        Assert.AreEqual("failed", none.ToHistoryEntry(DateTimeOffset.UnixEpoch).Outcome);

        using var cancelRig = new FixRig();
        ArrangePairs(cancelRig);
        AirPodsAppear(cancelRig);
        cancelRig.Ui.Pick = -1;
        Assert.AreEqual("cancelled", (await RunAsync(cancelRig)).ToHistoryEntry(DateTimeOffset.UnixEpoch).Outcome);

        using var wrongRig = new FixRig();
        ArrangePairs(wrongRig, airPods: false);
        AirPodsAppear(wrongRig);
        Assert.AreEqual("not-airpods", (await RunAsync(wrongRig)).ToHistoryEntry(DateTimeOffset.UnixEpoch).Outcome);
    }

    // ---- the real watcher's reading of one event ----

    [TestMethod]
    public void TheWatcherEventIsReadForClassPresenceAndName()
    {
        var properties = new Dictionary<string, object>
        {
            [WinRtPairingApi.AddressProperty] = "0a:1b:2c:3d:4e:8e",
            [WinRtPairingApi.MajorClassProperty] = 4u,
            [WinRtPairingApi.MinorClassProperty] = 6u,
            [WinRtPairingApi.IsPresentProperty] = true,
            [WinRtPairingApi.NameProperty] = "AirPods Pro",
        };

        UnpairedDevice device = WinRtPairingApi.DescribeDevice("id", "", properties);

        Assert.AreEqual("0A1B2C3D4E8E", device.Address12);
        Assert.IsTrue(device.IsAudioVideo);
        Assert.AreEqual(6u, device.MinorClass);
        Assert.AreEqual(true, device.IsPresent);
        Assert.AreEqual("AirPods Pro", device.Name);
        Assert.IsTrue(PairingSession.IsCandidate(device));
    }

    [TestMethod]
    public void AnEventWithNoClassIsNotACandidateAndAnIntegerMinorIsRead()
    {
        UnpairedDevice bare = WinRtPairingApi.DescribeDevice("id", "x", new Dictionary<string, object>());
        UnpairedDevice withInt = WinRtPairingApi.DescribeDevice("id", "x", new Dictionary<string, object>
        {
            [WinRtPairingApi.MajorClassProperty] = 4u,
            [WinRtPairingApi.MinorClassProperty] = 1,
        });

        Assert.IsFalse(PairingSession.IsCandidate(bare));
        Assert.IsNull(bare.IsPresent);
        Assert.AreEqual(1u, withInt.MinorClass);
        Assert.IsTrue(PairingSession.IsCandidate(withInt));
    }
}
