using System.Globalization;
using System.Text.Json;
using Earshot.Infra;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// One advertisement from a saved capture, ready to hand to the parser: the Apple manufacturer data section as the
// watcher would deliver it (type 0x07, length, value), with the capture's own tag, signal and time.
internal sealed record ReplayMessage(DateTimeOffset At, sbyte Rssi, uint Tag, byte[] Section);

// Reads the owner's own saved captures from where the app wrote them, at test time. Nothing is copied into the
// repository: on a machine without the files (the hosted build) a test that needs them is inconclusive. Nothing
// is ever written back.
internal static class OwnerCaptureReplay
{
    // The one 60 s passive capture the battery decode was checked against, by name.
    public const string BaselineCaptureName = "capture-20260930T005643Z.log";

    private static string LocalFolder(params string[] parts) =>
        Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Earshot", .. parts]);

    public static string PhaseZeroFolder => LocalFolder("phase0");

    public static string SetupFolder => LocalFolder("widget", "setup");

    // The capture's ADV lines: tab separated, the time, the milliseconds since the start, the word ADV, the signal
    // as rssi=, the sender as tag=, the kind, the length as len= and the Apple item's value in hex.
    public static IReadOnlyList<ReplayMessage>? LoadCapture(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var messages = new List<ReplayMessage>();
        foreach (string line in File.ReadLines(path))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 8 || parts[2] != "ADV")
            {
                continue;
            }

            DateTimeOffset at = DateTimeOffset.Parse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            sbyte rssi = sbyte.Parse(parts[3].Replace("rssi=", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
            uint tag = uint.Parse(parts[4].Replace("tag=", "", StringComparison.Ordinal), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte[] value = Convert.FromHexString(parts[7]);
            messages.Add(new ReplayMessage(at, rssi, tag, Section(value)));
        }

        return messages;
    }

    // The set-up records each keep the first nine bytes of every documented-form message they heard and the
    // whole value of the others. The nine bytes hold every field the decode reads; the encrypted tail is padded
    // with zeros, which nothing reads.
    public static IReadOnlyList<ReplayMessage>? LoadSetupRecord(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        var messages = new List<ReplayMessage>();
        foreach (JsonElement capture in document.RootElement.GetProperty("candidate").GetProperty("captures").EnumerateArray())
        {
            byte[] value = Convert.FromHexString(capture.GetProperty("valueHex").GetString()!);
            if (capture.GetProperty("length").GetInt32() == ProximityParser.DocumentedLength && value.Length < ProximityParser.DocumentedLength)
            {
                Array.Resize(ref value, ProximityParser.DocumentedLength);
            }

            messages.Add(new ReplayMessage(
                capture.GetProperty("atUtc").GetDateTimeOffset(),
                (sbyte)capture.GetProperty("rssi").GetInt32(),
                uint.Parse(capture.GetProperty("senderTag").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                Section(value)));
        }

        return messages.OrderBy(m => m.At).ToList();
    }

    // The two set-up records the replay tests were written for, by name: both hold the owner's pair with both buds
    // in the case and the lid open (case level known). A record made later says something else (the third, from the
    // evening of the same day, holds the pair in use with the case level unknown), so the tests never take whatever
    // happens to be in the folder.
    public static readonly string[] PinnedSetupRecordNames =
    [
        "setup-2026.10.01T01.13.18Z.json",
        "setup-2026.10.01T01.14.14Z.json",
    ];

    // The record the pair in use with the case level unknown was saved in, by name.
    public const string InUseSetupRecordName = "setup-2026.10.01T18.58.14Z.json";

    // The pinned records that are on this machine, in the order named. A caller that needs all of them checks the count.
    public static IReadOnlyList<string> SetupRecordFiles() =>
        PinnedSetupRecordNames.Select(name => Path.Combine(SetupFolder, name)).Where(File.Exists).ToArray();

    public static IReadOnlyList<string> CaptureFiles() =>
        Directory.Exists(PhaseZeroFolder) ? Directory.GetFiles(PhaseZeroFolder, "capture-*.log").Order().ToArray() : [];

    // type 0x07, the value's length, the value: the section the Apple company id's data holds.
    private static byte[] Section(byte[] value)
    {
        var section = new byte[2 + value.Length];
        section[0] = ProximityParser.ProximityType;
        section[1] = checked((byte)value.Length);
        value.CopyTo(section, 2);
        return section;
    }
}

// What a replay through the parser, the selector and the decoder came to.
internal sealed class ReplayResult
{
    public int DocumentedForm { get; set; }

    public int ShortForm { get; set; }

    public int Applied { get; set; }

    public int BudOrderDisagree { get; set; }

    public int Flickers { get; set; }

    public int SetsInRange { get; set; }

    public List<DecodedReading> Readings { get; } = [];

    public DateTimeOffset LastAt { get; set; }

    public PartReading Left { get; set; } = PartReading.Unknown;

    public PartReading Right { get; set; } = PartReading.Unknown;

    public PartReading Case { get; set; } = PartReading.Unknown;
}

internal static class ReplayRunner
{
    // Feeds every message through the real parser and, for the documented form, the real selector and decoder. The
    // paired model is the one the messages carry for the model given, which is what the paired AirPods' product id
    // reads on this device.
    public static ReplayResult Run(IReadOnlyList<ReplayMessage> messages, ushort pairedModel)
    {
        var selector = new BroadcastSelector();
        selector.SetPairedModel(pairedModel);
        var result = new ReplayResult();
        (int? Left, int? Right)? previous = null;

        foreach (ReplayMessage message in messages)
        {
            ProximityParse parse = ProximityParser.Parse(ProximityParser.AppleCompanyId, message.Section);
            if (parse.Status != ProximityParseStatus.Ok || parse.Message is not ProximityMessage m)
            {
                if (parse.Status == ProximityParseStatus.UnknownForm)
                {
                    result.ShortForm++;
                }

                continue;
            }

            result.DocumentedForm++;
            SelectionObservation seen = selector.Observe(m, message.Tag, message.Rssi, message.At);
            result.SetsInRange = seen.SetsInRange;
            if (!seen.IsChosenSet)
            {
                continue;
            }

            DecodedReading reading = ProximityDecoder.Decode(m, ProximityDecodeTable.Documented, message.At);
            result.Applied++;
            result.Readings.Add(reading);
            result.LastAt = message.At;
            if (reading.Left.Percent is not null)
            {
                result.Left = reading.Left;
            }

            if (reading.Right.Percent is not null)
            {
                result.Right = reading.Right;
            }

            if (reading.Case.Percent is not null)
            {
                result.Case = reading.Case;
            }

            var now = (reading.Left.Percent, reading.Right.Percent);
            if (previous is { } before && before != now)
            {
                result.Flickers++;
            }

            previous = now;

            foreach (SenderMessage other in selector.OtherChosenSenders(message.Tag, message.At, BroadcastRules.SameSetWithin))
            {
                DecodedReading otherReading = ProximityDecoder.Decode(other.Message, ProximityDecodeTable.Documented, other.At);
                if (otherReading.Left.Percent != reading.Left.Percent || otherReading.Right.Percent != reading.Right.Percent)
                {
                    result.BudOrderDisagree++;
                }
            }
        }

        return result;
    }
}

// The decode and the selection checked against what the owner's own AirPods really sent. Each test is
// inconclusive where the saved files are not (the hosted build, a machine that never captured).
[TestClass]
public sealed class OwnerCaptureReplayTests
{
    // The paired AirPods' product id as Windows holds it and the saved messages carry it.
    private const ushort OwnersModel = 0x2027;

    private static IReadOnlyList<ReplayMessage> Baseline()
    {
        IReadOnlyList<ReplayMessage>? messages = OwnerCaptureReplay.LoadCapture(Path.Combine(OwnerCaptureReplay.PhaseZeroFolder, OwnerCaptureReplay.BaselineCaptureName));
        if (messages is null || messages.Count == 0)
        {
            Assert.Inconclusive("The saved capture is not on this machine.");
        }

        return messages!;
    }

    [TestMethod]
    public void TheCaptureIsOneSetFromTwoSendersAndTheShortFormIsNeverDecoded()
    {
        ReplayResult result = ReplayRunner.Run(Baseline(), OwnersModel);

        Assert.IsGreaterThan(0, result.DocumentedForm);
        Assert.AreEqual(1, result.SetsInRange, "Two documented-form senders are one set.");
        Assert.IsGreaterThan(0, result.ShortForm, "The capture holds the short form too.");
        Assert.IsGreaterThan(0, result.Applied);
    }

    [TestMethod]
    public void TheBudsAreTheUnorderedPairOfTheCaptureAndTheCaseIsFullAndNotCharging()
    {
        ReplayResult result = ReplayRunner.Run(Baseline(), OwnersModel);

        int?[] buds = [result.Left.Percent, result.Right.Percent];
        CollectionAssert.AreEquivalent(new int?[] { 50, 60 }, buds);
        Assert.AreEqual(100, result.Case.Percent);
        Assert.AreEqual(false, result.Case.Charging);
        Assert.AreEqual(true, result.Left.Charging);
        Assert.AreEqual(true, result.Right.Charging);
    }

    [TestMethod]
    public void LeftAndRightNeverChangeFromOneMessageToTheNextAndTheTwoBudsAlwaysAgree()
    {
        ReplayResult result = ReplayRunner.Run(Baseline(), OwnersModel);

        Assert.AreEqual(0, result.Flickers, "The card must not alternate between the two senders' figures.");
        Assert.AreEqual(0, result.BudOrderDisagree, "Two senders of the chosen set within two seconds decode to the same pair.");
    }

    [TestMethod]
    public void TheGaugeNumberIsTheLowerBud()
    {
        ReplayResult result = ReplayRunner.Run(Baseline(), OwnersModel);

        ShownBattery shown = BatteryFreshness.Shown(
            result.Left, result.Right, result.Case, PartReading.Unknown, onThisPc: true, linked: true, result.LastAt + TimeSpan.FromSeconds(1));

        Assert.AreEqual(50, shown.Gauge?.Percent);
    }

    // Which physical side each figure belongs to is not settled by either source or by this capture: the rule in
    // the decode table puts the higher figure on the left. This is the one test that says so, so a later change
    // to that rule fails here and nowhere else.
    [TestMethod]
    public void UnderTheUnverifiedSideRuleTheLeftBudReadsSixtyAndTheRightFifty()
    {
        ReplayResult result = ReplayRunner.Run(Baseline(), OwnersModel);

        Assert.AreEqual(60, result.Left.Percent);
        Assert.AreEqual(50, result.Right.Percent);
    }

    [TestMethod]
    public void EveryCaptureOnThisMachineReplaysWithoutFlickerOrDisagreement()
    {
        IReadOnlyList<string> files = OwnerCaptureReplay.CaptureFiles();
        if (files.Count == 0)
        {
            Assert.Inconclusive("No saved capture is on this machine.");
        }

        foreach (string file in files)
        {
            IReadOnlyList<ReplayMessage> messages = OwnerCaptureReplay.LoadCapture(file)!;
            ReplayResult result = ReplayRunner.Run(messages, OwnersModel);

            Assert.AreEqual(0, result.Flickers, Path.GetFileName(file));
            Assert.AreEqual(0, result.BudOrderDisagree, Path.GetFileName(file));
        }
    }

    [TestMethod]
    public void TheSetupRecordsReplayToFullBudsAndAHalfFullCase()
    {
        IReadOnlyList<string> files = OwnerCaptureReplay.SetupRecordFiles();
        if (files.Count != OwnerCaptureReplay.PinnedSetupRecordNames.Length)
        {
            Assert.Inconclusive("The two set-up records this test was written for are not both on this machine.");
        }

        foreach (string file in files)
        {
            IReadOnlyList<ReplayMessage> messages = OwnerCaptureReplay.LoadSetupRecord(file)!;
            ReplayResult result = ReplayRunner.Run(messages, OwnersModel);
            string name = Path.GetFileName(file);

            Assert.IsGreaterThan(0, result.Applied, name);
            Assert.AreEqual(100, result.Left.Percent, name);
            Assert.AreEqual(100, result.Right.Percent, name);
            Assert.AreEqual(50, result.Case.Percent, name);
            Assert.AreEqual(0, result.Flickers, name);
            Assert.IsTrue(result.Readings.All(r => r.Left.Percent == 100 && r.Right.Percent == 100), name + ": every message reads both buds full.");
            Assert.IsTrue(result.Readings.All(r => r.Case.Percent is null or 50), name + ": the case is half or unknown.");

            // The two buds charging with the case not is what nearly every message says.
            var modal = result.Readings
                .GroupBy(r => (r.Left.Charging, r.Right.Charging, r.Case.Charging))
                .OrderByDescending(g => g.Count())
                .First().Key;
            Assert.AreEqual((true, true, false), (modal.Item1, modal.Item2, modal.Item3), name);
        }
    }

    // A clock the replay sets from the messages' own times, so the service's idea of "now" is the capture's.
    private sealed class ReplayClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Through the real service, with a fake watcher source and a fake paired model, keeping each message's own tag,
    // signal and time.
    private static WidgetSnapshot ThroughTheService(IReadOnlyList<ReplayMessage> messages, out DateTimeOffset now)
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        var settings = new JsonSettingsStore(temp.File("settings.json"), log);
        var source = new FakeAdvertisementSource();
        var clock = new ReplayClock { Now = messages[0].At };
        using var service = new WidgetStatusService(
            () => source, settings, new Phase3.FakeDeviceMonitor(clock), () => null, log, action => action(), clock,
            new FakePairedModelSource(OwnersModel));
        service.Start();
        foreach (ReplayMessage message in messages)
        {
            clock.Now = message.At;
            source.Raise(new AdvertisementSample(ProximityParser.AppleCompanyId, message.Section, message.Rssi, message.At, message.Tag));
        }

        clock.Now = messages[^1].At + TimeSpan.FromSeconds(1);
        now = clock.Now;
        return service.Current;
    }

    [TestMethod]
    public void ThroughTheServiceTheCaptureShowsTheBudsTheCaseAndTheLowerBudOnTheGauge()
    {
        IReadOnlyList<ReplayMessage> messages = Baseline();

        WidgetSnapshot snapshot = ThroughTheService(messages, out DateTimeOffset now);

        int?[] buds = [snapshot.Left.Percent, snapshot.Right.Percent];
        CollectionAssert.AreEquivalent(new int?[] { 50, 60 }, buds);
        Assert.AreEqual(100, snapshot.Case.Percent);
        Assert.AreEqual(false, snapshot.Case.Charging);
        Assert.AreEqual(true, snapshot.Left.Charging);
        Assert.AreEqual(true, snapshot.Right.Charging);
        Assert.AreEqual(BroadcastSelectionState.Linked, snapshot.Selection);
        Assert.AreEqual(1, snapshot.Counters.Sets, "One set from the two senders.");
        Assert.AreEqual(0, snapshot.Counters.BudOrderDisagree);
        Assert.AreEqual(0, snapshot.Counters.ModelMismatch);
        Assert.IsGreaterThan(0, snapshot.Counters.UnknownForm, "The short form is counted by shape and never decoded.");
        Assert.IsGreaterThan(0, snapshot.Counters.Chosen);
        Assert.IsLessThanOrEqualTo(
            snapshot.Counters.OkForm,
            snapshot.Counters.Chosen + snapshot.Counters.OtherSet + snapshot.Counters.NoPairedModel + snapshot.Counters.ModelMismatch + snapshot.Counters.ColourMismatch,
            "Every documented-form message lands in at most one of the classes.");
        Assert.AreEqual(50, BatteryFreshness.Shown(snapshot with { Where = AirPodsWhere.ThisPc }, now).Gauge?.Percent, "With the AirPods connected to this PC the lower bud is on the gauge.");
    }

    [TestMethod]
    public void ThroughTheServiceTheSetupRecordsShowFullBudsAndAHalfFullCase()
    {
        IReadOnlyList<string> files = OwnerCaptureReplay.SetupRecordFiles();
        if (files.Count != OwnerCaptureReplay.PinnedSetupRecordNames.Length)
        {
            Assert.Inconclusive("The two set-up records this test was written for are not both on this machine.");
        }

        foreach (string file in files)
        {
            IReadOnlyList<ReplayMessage> messages = OwnerCaptureReplay.LoadSetupRecord(file)!;
            WidgetSnapshot snapshot = ThroughTheService(messages, out _);
            string name = Path.GetFileName(file);

            Assert.AreEqual(100, snapshot.Left.Percent, name);
            Assert.AreEqual(100, snapshot.Right.Percent, name);
            Assert.AreEqual(50, snapshot.Case.Percent, name);
            Assert.AreEqual(0, snapshot.Counters.BudOrderDisagree, name);
        }
    }

    // The signal of each documented-form message that carries a case level (a bud in the case with the lid open), in time order,
    // senders together: what the near-the-PC level of BroadcastRules.LinkThresholdDbm is a median of.
    private static List<int> CaseKnownSignals(IReadOnlyList<ReplayMessage> messages)
    {
        var signals = new List<int>();
        foreach (ReplayMessage message in messages.OrderBy(m => m.At))
        {
            ProximityParse parse = ProximityParser.Parse(ProximityParser.AppleCompanyId, message.Section);
            if (parse.Status == ProximityParseStatus.Ok && parse.Message is ProximityMessage m && BroadcastSenderSets.CaseKnown(m))
            {
                signals.Add(message.Rssi);
            }
        }

        return signals;
    }

    // The median of every run of LinkMessages consecutive signals.
    private static List<int> WindowMedians(List<int> signals)
    {
        var medians = new List<int>();
        for (int i = 0; i + BroadcastRules.LinkMessages <= signals.Count; i++)
        {
            medians.Add(signals.Skip(i).Take(BroadcastRules.LinkMessages).Order().ElementAt(BroadcastRules.LinkMessages / 2));
        }

        return medians;
    }

    // The figures the documents give for -70 dBm, held to the saved records they were taken from, by name: the 30 September
    // capture and the two set-up records of 1 October, each the owner's pair with both buds in the case and the lid open. About 300
    // case-known messages; the median over every run of five of them (senders together, in time order) ran from -72 to -51 dBm;
    // the run in the middle of each record read -61, -58 or -54; and about 95% of the weakest record's runs were above -70.
    // Inconclusive where the files are not (the hosted build).
    [TestMethod]
    public void TheLinkThresholdFiguresInTheDocumentsHoldForTheThreeSavedRecordsTheyWereTakenFrom()
    {
        IReadOnlyList<string> setups = OwnerCaptureReplay.SetupRecordFiles();
        IReadOnlyList<ReplayMessage>? capture = OwnerCaptureReplay.LoadCapture(Path.Combine(OwnerCaptureReplay.PhaseZeroFolder, OwnerCaptureReplay.BaselineCaptureName));
        if (setups.Count != OwnerCaptureReplay.PinnedSetupRecordNames.Length || capture is null)
        {
            Assert.Inconclusive("The capture and the two set-up records the figures were taken from are not all on this machine.");
        }

        var records = new List<(string Name, List<int> Signals)>();
        foreach (string file in setups)
        {
            records.Add((Path.GetFileName(file), CaseKnownSignals(OwnerCaptureReplay.LoadSetupRecord(file)!)));
        }

        records.Add((OwnerCaptureReplay.BaselineCaptureName, CaseKnownSignals(capture!)));

        int total = records.Sum(r => r.Signals.Count);
        Assert.IsTrue(total is >= 290 and <= 300, "About 300 case-known messages in all, not " + total + ".");

        var windows = records.Select(r => (r.Name, Medians: WindowMedians(r.Signals))).ToList();
        Assert.AreEqual(-72, windows.Min(w => w.Medians.Min()), "The weakest window's median.");
        Assert.AreEqual(-51, windows.Max(w => w.Medians.Max()), "The strongest window's median.");
        CollectionAssert.AreEqual(new[] { -54, -61, -58 }, windows.Select(w => w.Medians[w.Medians.Count / 2]).ToArray(), "The middle window of each record, in the order they are named above.");

        (string weakestName, List<int> weakest) = windows.OrderBy(w => w.Medians.Min()).Select(w => (w.Name, w.Medians)).First();
        double above = weakest.Count(m => m > -70) / (double)weakest.Count;
        Assert.IsTrue(above is >= 0.94 and <= 0.96, weakestName + ": " + above.ToString("P1", CultureInfo.InvariantCulture) + " of its windows were above -70 dBm, and the documents say about 95%.");
    }

    [TestMethod]
    public void AShortFormSenderIsCountedAsUnknownAndNeverChosen()
    {
        // The short form's bytes, whatever they are, never reach the selector or the decoder.
        byte[] value = new byte[17];
        value[0] = 0x06;
        byte[] section = new byte[2 + value.Length];
        section[0] = ProximityParser.ProximityType;
        section[1] = (byte)value.Length;
        value.CopyTo(section, 2);
        var messages = new List<ReplayMessage>();
        for (int i = 0; i < 40; i++)
        {
            messages.Add(new ReplayMessage(BroadcastFixtures.Start + TimeSpan.FromMilliseconds(i * 500), -40, 7, section));
        }

        ReplayResult result = ReplayRunner.Run(messages, OwnersModel);

        Assert.AreEqual(40, result.ShortForm);
        Assert.AreEqual(0, result.DocumentedForm);
        Assert.AreEqual(0, result.Applied);
    }
}
