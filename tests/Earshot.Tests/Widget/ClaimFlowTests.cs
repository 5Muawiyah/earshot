using System.Text.Json;
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
        // Matches the fixed -70 threshold the claim below is made at, so the store's own reload after Save
        // does not invalidate it (M1: a stored threshold is usable only when it matches the current one).
        var store = new ClaimStore(temp.File("claim.json"), log, static () => (sbyte)-70);
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
        await store.IdleAsync(); // M2: let the queued write finish before the temp folder is torn down
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

    // M5: the prior version only checked that each expected member's name appeared somewhere in the file
    // (StringAssert.Contains), so it could not have failed had the file also carried an extra, unwanted
    // member (an address, a per-run tag, a payload byte) alongside the expected ones. Parses the file and
    // compares both the top-level and the nested Last object's property names as exact sets.
    [TestMethod]
    public async Task TheClaimFileHoldsTheFieldsAndNothingElse()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        string path = temp.File("claim.json");
        var store = new ClaimStore(path, log, static () => (sbyte)-70);
        var flow = new ClaimFlow(store, log);
        var source = new FakeAdvertisementSource { State = AdvertisementSourceState.Started };
        var clock = new TestTimeProvider();

        Task<ClaimOutcome> task = flow.RunAsync(
            source, clock, WidgetTiming.ClaimWindow, signalThresholdDbm: (sbyte)-70, ProximityDecodeTable.Unproved, CancellationToken.None);
        source.Raise(new AdvertisementSample(ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(), Rssi: -60, clock.GetUtcNow(), SenderTag: 1));
        clock.Advance(WidgetTiming.ClaimWindow);
        await task;
        await store.IdleAsync(); // M2: the write is now queued on a background thread

        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        var topLevelMembers = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var expectedTopLevel = new HashSet<string>(StringComparer.Ordinal)
        {
            "SchemaVersion", "ModelHigh", "ModelLow", "Colour", "SignalThresholdDbm", "ClaimedAtUtc", "Last",
        };
        CollectionAssert.AreEquivalent(expectedTopLevel.ToList(), topLevelMembers.ToList(), "Top level: " + json);

        var lastMembers = root.GetProperty("Last").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var expectedLast = new HashSet<string>(StringComparer.Ordinal) { "NibbleHigh", "NibbleLow", "Case", "AtUtc" };
        CollectionAssert.AreEquivalent(expectedLast.ToList(), lastMembers.ToList(), "Last: " + json);
    }
}
