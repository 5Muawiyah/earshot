using Earshot.Tests.Streaming;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class ClaimFlowTests
{
    [TestMethod]
    public async Task NoThresholdMeansNoClaim()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        var store = new ClaimStore(temp.File("claim.json"), log);
        var flow = new ClaimFlow(store, log);
        var source = new FakeAdvertisementSource { State = AdvertisementSourceState.Started };
        var clock = new TestTimeProvider();

        // The public entry point reads WidgetDefaults.SignalThresholdDbm directly, and that constant ships
        // null until phase 0 proves it, so this exercises the real, un-overridden default.
        ClaimOutcome outcome = await flow.RunAsync(source, clock, WidgetTiming.ClaimWindow, CancellationToken.None);

        Assert.AreEqual(ClaimOutcomeStatus.NoThreshold, outcome.Status);
        Assert.IsNull(outcome.Claim);
        Assert.IsNull(store.Current);
    }

    [TestMethod]
    public async Task OneSenderClearingTheThresholdIsClaimed()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        var store = new ClaimStore(temp.File("claim.json"), log);
        var flow = new ClaimFlow(store, log);
        var source = new FakeAdvertisementSource { State = AdvertisementSourceState.Started };
        var clock = new TestTimeProvider();

        Task<ClaimOutcome> task = flow.RunAsync(
            source, clock, WidgetTiming.ClaimWindow, signalThresholdDbm: (sbyte)-70, ProximityDecodeTable.Unproved, CancellationToken.None);
        source.Raise(new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(), Rssi: -60, clock.GetUtcNow(), SenderTag: 111));
        clock.Advance(WidgetTiming.ClaimWindow);
        ClaimOutcome outcome = await task;

        Assert.AreEqual(ClaimOutcomeStatus.Claimed, outcome.Status);
        Assert.IsNotNull(outcome.Claim);
        Assert.AreEqual(WidgetFixtures.ModelHigh, outcome.Claim!.ModelHigh);
        Assert.AreEqual(WidgetFixtures.ModelLow, outcome.Claim.ModelLow);
        Assert.AreEqual(WidgetFixtures.Colour, outcome.Claim.Colour);
        Assert.AreEqual((sbyte)-70, outcome.Claim.SignalThresholdDbm);
        Assert.AreEqual(outcome.Claim, store.Current);
    }

    [TestMethod]
    public async Task TwoSendersRefuseTheClaim()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        var store = new ClaimStore(temp.File("claim.json"), log);
        var flow = new ClaimFlow(store, log);
        var source = new FakeAdvertisementSource { State = AdvertisementSourceState.Started };
        var clock = new TestTimeProvider();

        Task<ClaimOutcome> task = flow.RunAsync(
            source, clock, WidgetTiming.ClaimWindow, signalThresholdDbm: (sbyte)-70, ProximityDecodeTable.Unproved, CancellationToken.None);
        source.Raise(new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(), Rssi: -60, clock.GetUtcNow(), SenderTag: 1));
        source.Raise(new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(), Rssi: -60, clock.GetUtcNow(), SenderTag: 2));
        clock.Advance(WidgetTiming.ClaimWindow);
        ClaimOutcome outcome = await task;

        Assert.AreEqual(ClaimOutcomeStatus.MultipleSenders, outcome.Status);
        Assert.IsNull(outcome.Claim);
        Assert.IsNull(store.Current);
    }

    [TestMethod]
    public async Task ASenderBelowTheThresholdIsIgnored()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        var store = new ClaimStore(temp.File("claim.json"), log);
        var flow = new ClaimFlow(store, log);
        var source = new FakeAdvertisementSource { State = AdvertisementSourceState.Started };
        var clock = new TestTimeProvider();

        Task<ClaimOutcome> task = flow.RunAsync(
            source, clock, WidgetTiming.ClaimWindow, signalThresholdDbm: (sbyte)-70, ProximityDecodeTable.Unproved, CancellationToken.None);
        source.Raise(new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(), Rssi: -90, clock.GetUtcNow(), SenderTag: 1));
        clock.Advance(WidgetTiming.ClaimWindow);
        ClaimOutcome outcome = await task;

        Assert.AreEqual(ClaimOutcomeStatus.NoSender, outcome.Status);
    }

    [TestMethod]
    public async Task OnlyTheDocumentedFormCounts()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        var store = new ClaimStore(temp.File("claim.json"), log);
        var flow = new ClaimFlow(store, log);
        var source = new FakeAdvertisementSource { State = AdvertisementSourceState.Started };
        var clock = new TestTimeProvider();

        Task<ClaimOutcome> task = flow.RunAsync(
            source, clock, WidgetTiming.ClaimWindow, signalThresholdDbm: (sbyte)-70, ProximityDecodeTable.Unproved, CancellationToken.None);
        source.Raise(new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.UnknownSeventeenByteForm(), Rssi: -60, clock.GetUtcNow(), SenderTag: 1));
        clock.Advance(WidgetTiming.ClaimWindow);
        ClaimOutcome outcome = await task;

        Assert.AreEqual(ClaimOutcomeStatus.NoSender, outcome.Status);
    }

    [TestMethod]
    public async Task WithTheLidRuleProvedAClosedCaseCannotBeClaimed()
    {
        var table = ProximityDecodeTable.Unproved with { CaseNibbleReadsOnlyWithLidOpen = true };
        using var temp = new TempFolder();
        var log = new CapturingLog();
        var store = new ClaimStore(temp.File("claim.json"), log);
        var flow = new ClaimFlow(store, log);
        var source = new FakeAdvertisementSource { State = AdvertisementSourceState.Started };
        var clock = new TestTimeProvider();

        Task<ClaimOutcome> task = flow.RunAsync(
            source, clock, WidgetTiming.ClaimWindow, signalThresholdDbm: (sbyte)-70, table, CancellationToken.None);
        // Case nibble 0xF: unreadable, as it would be with the lid shut once that claim is proved.
        source.Raise(new AdvertisementSample(
            ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryB: 0x0F), Rssi: -60, clock.GetUtcNow(), SenderTag: 1));
        clock.Advance(WidgetTiming.ClaimWindow);
        ClaimOutcome outcome = await task;

        Assert.AreEqual(ClaimOutcomeStatus.NoSender, outcome.Status);
    }

    [TestMethod]
    public async Task TheClaimFileHoldsTheFieldsAndNothingElse()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        string path = temp.File("claim.json");
        var store = new ClaimStore(path, log);
        var flow = new ClaimFlow(store, log);
        var source = new FakeAdvertisementSource { State = AdvertisementSourceState.Started };
        var clock = new TestTimeProvider();

        Task<ClaimOutcome> task = flow.RunAsync(
            source, clock, WidgetTiming.ClaimWindow, signalThresholdDbm: (sbyte)-70, ProximityDecodeTable.Unproved, CancellationToken.None);
        source.Raise(new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(), Rssi: -60, clock.GetUtcNow(), SenderTag: 1));
        clock.Advance(WidgetTiming.ClaimWindow);
        await task;

        string json = File.ReadAllText(path);
        foreach (string member in new[]
                 {
                     "SchemaVersion", "ModelHigh", "ModelLow", "Colour", "SignalThresholdDbm", "ClaimedAtUtc", "Last",
                     "NibbleHigh", "NibbleLow", "Case", "AtUtc",
                 })
        {
            StringAssert.Contains(json, member);
        }
    }
}
