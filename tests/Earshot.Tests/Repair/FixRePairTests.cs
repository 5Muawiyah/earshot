using Earshot.Contracts;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

[TestClass]
public sealed class FixRePairTests
{
    internal const string OldId = "paired-old";
    internal const string NewId = "unpaired-new";

    internal static FixConsent RePairConsent()
    {
        var prompter = new FakePrompter();
        prompter.SayOk();
        return new FixConsentDialog(prompter, FakeDesktop.Interactive()).AskRePair(["Jonathan’s AirPods Pro"], removeFirst: true)!;
    }

    // The old pair is paired, a new AirPods-like device appears after the prompt, and pairing it brings its nodes into the table.
    internal static void ArrangeHappyPairing(FixRig rig, bool newDeviceIsAirPods = true)
    {
        rig.Pairing.Paired.Add(new PairedDevice(OldId, "Jonathan’s AirPods Pro", RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer));
        rig.Pairing.Paired.Add(new PairedDevice("paired-phone", "iPhone", RecordedNodes.IPhoneAddress, RecordedNodes.IPhoneContainer));
        rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice("seen-before", "Old speaker", "AABBCCDDEE01", IsAudioVideo: true, MinorClass: 6)));
        rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice("seen-before-2", "Some watch", "AABBCCDDEE02", IsAudioVideo: false)));
        rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.InitialListDone, null));
        rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice("not-audio", "Keyboard", "AABBCCDDEE03", IsAudioVideo: false)));
        rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice(NewId, "AirPods Pro", FixRig.NewAirPodsAddress, IsAudioVideo: true, MinorClass: 6)));
        rig.Pairing.OnUnpaired = () =>
        {
            // Removing a device from Windows takes its nodes with it; this is only what the fake does.
            rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Where(n => n.Container != RecordedNodes.AirPodsContainer));
        };
        rig.Pairing.OnPaired = _ =>
        {
            FakeNode[] arrived = newDeviceIsAirPods
                ? FixRig.NewAirPodsNodes()
                :
                [
                    new FakeNode(@"BTHENUM\DEV_" + FixRig.NewAirPodsAddress + @"\b&1a2b3c4d&0&BLUETOOTHDEVICE_" + FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer, "Speaker"),
                    new FakeNode(@"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2028\b&1a2b3c4d&0&" + FixRig.NewAirPodsAddress + "_C00000000", FixRig.NewAirPodsContainer),
                ];
            rig.Nodes.Inner = new FakeNodeApi(rig.Nodes.Inner.Nodes.Concat(arrived));
            rig.Pairing.Paired.Add(new PairedDevice("paired-new", "AirPods Pro", FixRig.NewAirPodsAddress, FixRig.NewAirPodsContainer));
        };
    }

    private static RePairer Make(FixRig rig) => new(rig.Pairing, rig.Nodes, rig.Ui, rig.Host().Options, rig.Log);

    private static readonly RePairTarget Target = new(RecordedNodes.AirPodsAddress, "Jonathan’s AirPods Pro");

    [TestMethod]
    public async Task ItUnpairsThenWatchesThenPairsInThatOrderAndNeverScansFirst()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.IsTrue(result.Paired, string.Join("; ", result.Lines));
        Expect.Same(rig.Pairing.Calls, "list-paired", "unpair:" + OldId, "watch", "pair:" + NewId, "list-paired");
        Assert.AreEqual(FixRig.NewAirPodsAddress, result.Adoptable!.Address);
    }

    // Before the pairing window exists the person has no Cancel, and the run holds the Fix lock: a paired list that does not
    // answer ends the run within its budget, with nothing removed.
    [TestMethod]
    public async Task APairedListThatDoesNotAnswerBeforeTheRemovalRemovesNothingWithinItsBudget()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        rig.Pairing.HangOnList = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.AreEqual(RePairFailure.NotResolved, result.Failure, string.Join("; ", result.Lines));
        Assert.IsFalse(result.Touched);
        Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("unpair:", StringComparison.Ordinal)));
        Assert.IsTrue(result.Steps.Any(s => s.Step == "pair-list" && s.Code == 1460 && s.CodeName == "earshot-timeout"), string.Join(", ", result.Steps.Select(s => s.Step + " " + s.Code)));
        Assert.IsLessThan(10_000L, watch.ElapsedMilliseconds);
    }

    // A removal Windows does not answer: the run ends within its budget, says it cannot tell whether the pair was removed, and
    // counts as touched, so the closing block follows.
    [TestMethod]
    public async Task ARemovalThatDoesNotAnswerEndsWithinItsBudgetAndSaysItCannotTell()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        rig.Pairing.HangOnUnpair = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.AreEqual(RePairFailure.UnpairFailed, result.Failure);
        Assert.IsTrue(result.Touched);
        Assert.AreEqual(1460, result.Statuses.Single().Code);
        Assert.IsTrue(result.Lines.Any(l => l.Contains("not known whether it was removed", StringComparison.Ordinal)), string.Join("; ", result.Lines));
        Assert.IsFalse(rig.Pairing.Calls.Contains("watch"), "Nothing was scanned.");
        Assert.IsLessThan(10_000L, watch.ElapsedMilliseconds);
    }

    [TestMethod]
    public async Task AFirstDeviceListedBeforeThePromptIsNeverACandidate()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);

        await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        string pick = rig.Ui.Shown.Single(s => s.StartsWith("pick:", StringComparison.Ordinal));
        Assert.AreEqual("pick:AirPods Pro", pick);
    }

    [TestMethod]
    public async Task SeveralNewAudioDevicesAreShownByNameForTheOwnerToPick()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        rig.Pairing.Events.Add(new WatchEvent(WatchEventKind.Added, new UnpairedDevice("headset-2", "Some headset", "AABBCCDDEE04", IsAudioVideo: true, MinorClass: 6)));

        await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.AreEqual("pick:AirPods Pro|Some headset", rig.Ui.Shown.Single(s => s.StartsWith("pick:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task TheInstructionIsAppleWordingAndShownBeforeTheOwnerPicks()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);

        await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        int instruction = rig.Ui.Shown.FindIndex(s => s.Contains("double-tap", StringComparison.OrdinalIgnoreCase));
        int pick = rig.Ui.Shown.FindIndex(s => s.StartsWith("pick:", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, instruction);
        Assert.IsLessThan(pick, instruction);
        string text = rig.Ui.Shown[instruction];
        Assert.Contains("white", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("button on the back", text, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task TwoPairedDevicesWithTheAddressMeansNothingIsUnpaired()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        rig.Pairing.Paired.Add(new PairedDevice("paired-copy", "AirPods copy", RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer));

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.AreEqual(RePairFailure.NotResolved, result.Failure);
        Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("unpair", StringComparison.Ordinal) || c == "watch" || c.StartsWith("pair:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NoPairedDeviceWithTheAddressMeansNothingIsUnpaired()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        rig.Pairing.Paired.RemoveAll(p => p.Id == OldId);

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.AreEqual(RePairFailure.NotResolved, result.Failure);
        Expect.Same(rig.Pairing.Calls, "list-paired");
    }

    [TestMethod]
    public async Task TheIdentityRuleIsCheckedAgainOnTheContainerAtCallTime()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        // The paired entry now sits in a container that holds no AirPods nodes.
        rig.Pairing.Paired.RemoveAll(p => p.Id == OldId);
        rig.Pairing.Paired.Add(new PairedDevice(OldId, "Jonathan’s AirPods Pro", RecordedNodes.AirPodsAddress, RecordedNodes.IPhoneContainer));

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.AreEqual(RePairFailure.NotAirPods, result.Failure);
        Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("unpair", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task APhoneAddressIsNeverUnpairedEvenIfAskedTo()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), new RePairTarget(RecordedNodes.IPhoneAddress, "iPhone"), CancellationToken.None);

        Assert.AreEqual(RePairFailure.NotAirPods, result.Failure);
        Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("unpair", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AFailedUnpairStopsBeforeAnyScanAndRecordsTheStatus()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        rig.Pairing.UnpairResult = new PairingCallResult("Failed", 2, false, null);

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.AreEqual(RePairFailure.UnpairFailed, result.Failure);
        Assert.IsFalse(rig.Pairing.Calls.Contains("watch"));
        Assert.IsTrue(result.Statuses.Any(s => s.Name == "Failed" && s.Code == 2));
    }

    [TestMethod]
    public async Task WhetherTheNodesWentWithTheUnpairIsRecordedNotAssumed()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        rig.Pairing.OnUnpaired = null;

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.IsTrue(result.NodesRemainedAfterUnpair);
        Assert.IsTrue(result.Lines.Any(l => l.Contains("still", StringComparison.OrdinalIgnoreCase) && l.Contains("unproved", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ConfirmOnlyIsAcceptedAfterTheSecondOkAndAPinIsShownToTheOwner()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        rig.Pairing.Requests.Add((FixPairKind.ConfirmOnly, null));
        rig.Pairing.Requests.Add((FixPairKind.ConfirmPinMatch, "123456"));
        rig.Pairing.Requests.Add((FixPairKind.DisplayPin, "654321"));

        await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Expect.Same(rig.Pairing.RequestAnswers, true, true, true);
        // The owner is asked only for the two that show a pin.
        Expect.Same(rig.Ui.PinsShown, "ConfirmPinMatch:123456", "DisplayPin:654321");
    }

    [TestMethod]
    public async Task APinTheOwnerDoesNotConfirmIsNotAccepted()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        rig.Pairing.Requests.Add((FixPairKind.ConfirmPinMatch, "123456"));
        rig.Ui.ConfirmPin = false;

        await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Expect.Same(rig.Pairing.RequestAnswers, false);
    }

    [TestMethod]
    public async Task AfterAPairingThatPassesTheRuleTheNewDeviceIsOfferedForAdoption()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.IsTrue(result.Paired);
        Assert.AreEqual(RePairFailure.None, result.Failure);
        Assert.AreEqual(FixRig.NewAirPodsContainer, result.Adoptable!.ContainerId);
        Assert.IsTrue(result.Statuses.Any(s => s.Name == "Paired"));
    }

    [TestMethod]
    public async Task ADevicePickedThatIsNotAirPodsAdoptsNothingAndStaysPaired()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig, newDeviceIsAirPods: false);

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.AreEqual(RePairFailure.NotAirPodsAfterPair, result.Failure);
        Assert.IsNull(result.Adoptable);
        Assert.IsTrue(result.Paired, "The wrong device did pair, and the result says so.");
        Assert.IsTrue(result.Lines.Any(l => l.Contains("stays paired", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(result.Lines.Any(l => l.Contains("nothing was adopted", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task NoDeviceAppearingEndsTheScanWithoutPairing()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        rig.Pairing.Events.RemoveAll(e => e.Device?.Id == NewId);

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.AreEqual(RePairFailure.NoDevice, result.Failure);
        Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("pair:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AnOwnerWhoPicksNothingPairsNothing()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        rig.Ui.Pick = -1;

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), Target, CancellationToken.None);

        Assert.AreEqual(RePairFailure.Cancelled, result.Failure);
        Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("pair:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task WithNoTargetItOnlyScansAndPairs()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        rig.Pairing.Paired.Clear();

        RePairResult result = await Make(rig).RunAsync(RePairConsent(), target: null, CancellationToken.None);

        Assert.IsTrue(result.Paired);
        Assert.IsFalse(rig.Pairing.Calls.Any(c => c.StartsWith("unpair", StringComparison.Ordinal)));
        Assert.AreEqual("watch", rig.Pairing.Calls[0]);
    }

    [TestMethod]
    public async Task AChangesConsentCannotStartARePair()
    {
        using var rig = new FixRig();
        ArrangeHappyPairing(rig);
        var prompter = new FakePrompter();
        prompter.SayOk();
        FixConsent wrong = new FixConsentDialog(prompter, FakeDesktop.Interactive()).AskChanges(new FixPlan([], [], [], [], false, false)).Consent!;

        await Assert.ThrowsAsync<ArgumentException>(() => Make(rig).RunAsync(wrong, Target, CancellationToken.None));

        Assert.IsEmpty(rig.Pairing.Calls);
    }
}
