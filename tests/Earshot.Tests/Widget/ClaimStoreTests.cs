using System.Reflection;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class ClaimStoreTests
{
    private static readonly string[] LastMembers = ["NibbleHigh", "NibbleLow", "Case", "AtUtc"];

    private const string SetupRecordName = "setup-2026.09.27T00.00.00Z.json";

    private static WidgetClaim SampleClaim() => new(
        SchemaVersion: 2,
        ModelHigh: WidgetFixtures.ModelHigh,
        ModelLow: WidgetFixtures.ModelLow,
        Colour: WidgetFixtures.Colour,
        SignalThresholdDbm: -70,
        SignalMinDbm: -60,
        SignalMedianDbm: -58,
        SignalMaxDbm: -55,
        SignalSamples: 12,
        SetupRecord: SetupRecordName,
        ClaimedAtUtc: new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero),
        Last: new OwnedBattery(5, 6, 7, new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero)));

    // A hand-written schema 2 file, with every member the store reads; each test replaces the one it is
    // about. Member names are the ones the store itself writes.
    private static string Json(
        int schema = 2, int threshold = -70, int min = -60, int median = -58, int max = -55, int samples = 12,
        string setupRecord = SetupRecordName, string claimedAt = "2026-09-27T00:00:00+00:00", string? last = "default", bool namedOrder = false)
    {
        string lastJson = last == "default"
            ? "{\"NibbleHigh\":5,\"NibbleLow\":6,\"Case\":7,\"AtUtc\":\"2026-09-27T00:00:00+00:00\"}"
            : last ?? "null";
        return
            "{\"SchemaVersion\":" + schema + ",\"ModelHigh\":238,\"ModelLow\":238,\"Colour\":238," +
            "\"SignalThresholdDbm\":" + threshold + ",\"SignalMinDbm\":" + min + ",\"SignalMedianDbm\":" + median +
            ",\"SignalMaxDbm\":" + max + ",\"SignalSamples\":" + samples + ",\"SetupRecord\":\"" + setupRecord + "\"," +
            "\"ClaimedAtUtc\":\"" + claimedAt + "\",\"Last\":" + lastJson + ",\"NibblesAreNamedOrder\":" + (namedOrder ? "true" : "false") + "}";
    }

    [TestMethod]
    public void AMissingFileIsNoClaim()
    {
        using var temp = new TempFolder();
        var store = new ClaimStore(temp.File("claim.json"), new CapturingLog());

        Assert.IsNull(store.Current);
    }

    [TestMethod]
    public void AnUnreadableFileIsNoClaimAndIsLoggedAndLeft()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        File.WriteAllText(path, "not valid json");
        var log = new CapturingLog();

        var store = new ClaimStore(path, log);

        Assert.IsNull(store.Current);
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "not valid"));
        Assert.AreEqual("not valid json", File.ReadAllText(path));
    }

    private static ClaimStore NewStore(string path, ILog log) => new(path, log);

    [TestMethod]
    public async Task SaveIsAtomicAndReadable()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var store = NewStore(path, new CapturingLog());
        WidgetClaim claim = SampleClaim();

        store.Save(claim);
        Assert.AreEqual(claim, store.Current, "The in-memory claim updates synchronously, before the disk write.");
        await store.IdleAsync();

        Assert.IsFalse(File.Exists(path + ".tmp"), "The temporary file must not be left behind.");

        var reloaded = NewStore(path, new CapturingLog());
        Assert.AreEqual(claim, reloaded.Current);
    }

    [TestMethod]
    public async Task ForgetDeletesTheFile()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var store = NewStore(path, new CapturingLog());
        store.Save(SampleClaim());
        await store.IdleAsync();
        Assert.IsTrue(File.Exists(path));

        store.ForgetClaim();
        await store.IdleAsync(); // the delete is now queued and serialised with any in-flight write, like a save

        Assert.IsFalse(File.Exists(path));
        Assert.IsNull(store.Current);
    }

    [TestMethod]
    public async Task ThePathIsUnderTheLocalFolderAndFollowsTheDataRoot()
    {
        using var temp = new TempFolder();
        Paths paths = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? temp.Path : null);
        var store = NewStore(paths.WidgetClaimFile, new CapturingLog());

        store.Save(SampleClaim());
        await store.IdleAsync();

        Assert.IsTrue(File.Exists(paths.WidgetClaimFile));
        Assert.IsTrue(
            paths.WidgetClaimFile.StartsWith(Path.Combine(temp.Path, "Local"), StringComparison.OrdinalIgnoreCase),
            paths.WidgetClaimFile);
    }

    // No claim was ever makeable before the schema changed, so a version 1 file can only be one a test or a
    // hand wrote. It is not used, is logged by its version, and is left exactly where it is.
    [TestMethod]
    public void ASchemaOneFileIsNoClaimAndLogged()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        string json = Json(schema: 1);
        File.WriteAllText(path, json);
        var log = new CapturingLog();

        var store = new ClaimStore(path, log);

        Assert.IsNull(store.Current);
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "schema version 1, not 2"), "The refusal must name the version.");
        Assert.AreEqual(json, File.ReadAllText(path), "The file must be left in place, not deleted or rewritten.");
    }

    // The threshold is whatever the set-up measured, so it is checked only for being a plausible signal.
    [TestMethod]
    public void AThresholdOutsideTheRangeIsNoClaim()
    {
        foreach (int threshold in new[] { 1, -128 })
        {
            using var temp = new TempFolder();
            string path = temp.File("claim.json");
            File.WriteAllText(path, Json(threshold: threshold, min: -60));
            var log = new CapturingLog();

            var store = new ClaimStore(path, log);

            Assert.IsNull(store.Current, "A threshold of " + threshold + " must be refused.");
            Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "threshold"));
        }
    }

    // The threshold is the one the set-up rules derive from the weakest message, ten decibels under it and never below
    // the floor a signal can have, so a claim written by a set-up always loads.
    [TestMethod]
    public void TheThresholdTheSetUpRulesDeriveLoadsAtAnySignalIncludingTheFloor()
    {
        (int Min, int Median, int Max, int Threshold)[] made =
        [
            (-60, -58, -55, -70),
            (-100, -90, -80, -110),
            (-120, -110, -100, -127),   // ten under would be -130: the floor
            (-127, -120, -100, -127),
        ];
        foreach ((int min, int median, int max, int threshold) in made)
        {
            using var temp = new TempFolder();
            string path = temp.File("claim.json");
            File.WriteAllText(path, Json(threshold: threshold, min: min, median: median, max: max));

            var store = new ClaimStore(path, new CapturingLog());

            Assert.IsNotNull(store.Current, "A threshold of " + threshold + " under a weakest message of " + min + " is what the rules make.");
            Assert.AreEqual(threshold, store.Current.SignalThresholdDbm);
            Assert.AreEqual(threshold, SetupRules.ThresholdFor(min));
        }
    }

    // A hand-edited threshold, above or below what the rules make, would let a stranger's signal pass or lock the
    // owner's out, so it is not a claim.
    [TestMethod]
    public void AThresholdThatIsNotWhatTheSetUpRulesMakeIsNoClaim()
    {
        (int Min, int Threshold)[] wrong =
        [
            (-60, -60),    // at the weakest message: no margin
            (-60, -65),    // half the margin
            (-60, -80),    // a wider margin than the rules make
            (-60, -127),   // wide open
            (-120, -128),
        ];
        foreach ((int min, int threshold) in wrong)
        {
            using var temp = new TempFolder();
            string path = temp.File("claim.json");
            File.WriteAllText(path, Json(threshold: threshold, min: min, median: min + 2, max: min + 5));
            var log = new CapturingLog();

            var store = new ClaimStore(path, log);

            Assert.IsNull(store.Current, "A threshold of " + threshold + " under " + min + " is not what a set-up derives.");
            Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "threshold"), "Refused with the reason.");
        }
    }

    [TestMethod]
    public void AClaimFromFewerMessagesThanACandidateNeedsIsNoClaim()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        File.WriteAllText(path, Json(samples: SetupRules.MinMessages - 1));

        Assert.IsNull(new ClaimStore(path, new CapturingLog()).Current);
    }

    // The claim is made from a set-up record and names it: with that record beside it and agreeing, it loads; with the
    // record missing, unusable or saying something else, it does not.
    [TestMethod]
    public void AClaimIsTrustedOnlyWithTheSetUpRecordItNamesBesideItAndAgreeingWithIt()
    {
        BatterySetupRecord Record(sbyte min = -60, sbyte threshold = -70, int messages = 12, byte colour = 0xEE)
        {
            ProximityMessage message = SetupRecordFixtures.Message(8, 4) with { Colour = colour, ModelHigh = WidgetFixtures.ModelHigh, ModelLow = WidgetFixtures.ModelLow };
            var candidate = new BatterySetupCandidate(messages, messages, 0, min, -58, -55, threshold, [], message);
            return new BatterySetupRecord(1, new DateTimeOffset(2026, 9, 26, 23, 59, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero),
                BatterySetupListenStatus.Found, "1.1.0", 40, 12, candidate, [], SetupRecordFixtures.Picks(40, 80));
        }

        (string Name, BatterySetupRecord? Record, bool Loads)[] cases =
        [
            ("agrees", Record(colour: WidgetFixtures.Colour), true),
            ("no record", null, false),
            ("another colour", Record(colour: (byte)(WidgetFixtures.Colour + 1)), false),
            ("another weakest message", Record(colour: WidgetFixtures.Colour, min: -61, threshold: -71), false),
            ("another message count", Record(colour: WidgetFixtures.Colour, messages: 11), false),
        ];
        foreach ((string name, BatterySetupRecord? record, bool loads) in cases)
        {
            using var temp = new TempFolder();
            string path = temp.File("claim.json");
            File.WriteAllText(path, Json());
            var log = new CapturingLog();
            string? asked = null;

            var store = new ClaimStore(path, log, setupRecord: n =>
            {
                asked = n;
                return record;
            }, now: () => new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));

            Assert.AreEqual(SetupRecordName, asked, name + ": the record the claim names is the one asked for.");
            Assert.AreEqual(loads, store.Current is not null, name);
            if (!loads)
            {
                Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "set-up record"), name + ": refused with the reason.");
            }
        }
    }

    [TestMethod]
    public void SignalFiguresOutOfOrderAreNoClaim()
    {
        (int Min, int Median, int Max, int Threshold, int Samples)[] bad =
        [
            (-58, -60, -55, -70, 12),   // minimum above the median
            (-60, -58, -59, -70, 12),   // median above the maximum
            (-60, -58, -55, -50, 12),   // threshold above the weakest message
            (-60, -58, -55, -70, 0),    // no samples
            (-60, -58, 5, -70, 12),     // a positive dBm
        ];
        foreach ((int min, int median, int max, int threshold, int samples) in bad)
        {
            using var temp = new TempFolder();
            string path = temp.File("claim.json");
            File.WriteAllText(path, Json(threshold: threshold, min: min, median: median, max: max, samples: samples));
            var log = new CapturingLog();

            var store = new ClaimStore(path, log);

            Assert.IsNull(store.Current, "Figures " + min + "/" + median + "/" + max + " threshold " + threshold + " samples " + samples + " must be refused.");
            Assert.IsTrue(log.Entries.Any(e => e.Level == Earshot.Contracts.LogLevel.Warn));
        }
    }

    [TestMethod]
    public void ASetupRecordThatIsAPathOrEmptyIsNoClaim()
    {
        foreach (string name in new[] { "", "..\\\\claim.json", "a/b.json", "C:\\\\x.json" })
        {
            using var temp = new TempFolder();
            string path = temp.File("claim.json");
            File.WriteAllText(path, Json(setupRecord: name));
            var log = new CapturingLog();

            var store = new ClaimStore(path, log);

            Assert.IsNull(store.Current, "A set-up record name of '" + name + "' must be refused.");
        }
    }

    // A hand-written file with a member missing is refused, not read with that member as zero.
    [TestMethod]
    public void AMissingRequiredMemberIsNoClaim()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        File.WriteAllText(path, Json().Replace("\"SignalMinDbm\":-60,", string.Empty, StringComparison.Ordinal));
        var log = new CapturingLog();

        var store = new ClaimStore(path, log);

        Assert.IsNull(store.Current);
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "SignalMinDbm"));
    }

    // The claim file holds exactly the schema 2 members, and the last reading's four; nothing else. No
    // address, no sender tag, no payload byte.
    [TestMethod]
    public async Task TheClaimFileHoldsTheSchemaTwoMembersAndNothingElse()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var store = NewStore(path, new CapturingLog());
        store.Save(SampleClaim());
        await store.IdleAsync();

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        string[] members = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        string[] expected =
        [
            "SchemaVersion", "ModelHigh", "ModelLow", "Colour", "SignalThresholdDbm", "SignalMinDbm", "SignalMedianDbm",
            "SignalMaxDbm", "SignalSamples", "SetupRecord", "ClaimedAtUtc", "Last", "NibblesAreNamedOrder",
        ];
        CollectionAssert.AreEqual(expected, members);
        string[] last = document.RootElement.GetProperty("Last").EnumerateObject().Select(p => p.Name).ToArray();
        CollectionAssert.AreEquivalent(LastMembers, last);
    }

    // Probe 3: a newer schema version is never used, whatever threshold it carries.
    [TestMethod]
    public void ANewerSchemaVersionIsNoClaimNeverRewrittenAndLogged()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        string json = Json(schema: 99);
        File.WriteAllText(path, json);
        var log = new CapturingLog();

        var store = new ClaimStore(path, log);

        Assert.IsNull(store.Current);
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "99"));
        Assert.AreEqual(json, File.ReadAllText(path), "An invalid claim file must be left exactly as it was.");
    }

    // A schema 2 file with no NibblesAreNamedOrder member reads as wire order (false), rather than fail to load
    // or default to something that would misread its nibbles.
    [TestMethod]
    public void AFileWithNoNamedOrderMemberLoadsAsWireOrder()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        File.WriteAllText(path, Json().Replace(",\"NibblesAreNamedOrder\":false", string.Empty, StringComparison.Ordinal));

        var store = new ClaimStore(path, new CapturingLog());

        Assert.IsNotNull(store.Current);
        Assert.IsFalse(store.Current!.NibblesAreNamedOrder, "A file with no such member must read as wire order.");
    }

    // Probe 2: a file with no last reading at all is not a usable claim.
    [TestMethod]
    public void AClaimWithNoLastReadingIsNoClaimAndLogged()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        string json = Json(last: null);
        File.WriteAllText(path, json);
        var log = new CapturingLog();

        var store = new ClaimStore(path, log);

        Assert.IsNull(store.Current);
        Assert.IsTrue(log.Entries.Any(e => e.Level == Earshot.Contracts.LogLevel.Warn), "A claim with no last reading must be refused and logged.");
    }

    // Repeated saves of a reading that has not actually changed must not hit the disk each time.
    [TestMethod]
    public async Task TwentyIdenticalSavesWriteTheFileOnce()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var store = NewStore(path, new CapturingLog());
        WidgetClaim claim = SampleClaim();

        for (int i = 0; i < 20; i++)
        {
            store.Save(claim);
        }

        await store.IdleAsync();

        Assert.AreEqual(1, store.DiskWriteCount);
    }

    // A write that fails (here, a read-only file) must not throw out of Save, must keep the in-memory
    // claim as current, and must log exactly once, not repeat the failure into a second line.
    [TestMethod]
    public async Task ASaveToAReadOnlyFileDoesNotThrowAndLogsOnce()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var writer = NewStore(path, new CapturingLog());
        writer.Save(SampleClaim());
        await writer.IdleAsync();
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var log = new CapturingLog();
        var store = new ClaimStore(path, log);
        WidgetClaim updated = SampleClaim() with { Last = new OwnedBattery(5, 6, 8, new DateTimeOffset(2026, 9, 27, 1, 0, 0, TimeSpan.Zero)) };

        try
        {
            store.Save(updated);
            await store.IdleAsync();

            Assert.AreEqual(updated, store.Current, "The in-memory claim must stay current even though the disk write failed.");
            Assert.AreEqual(1, log.Entries.Count(e => e.Level == Earshot.Contracts.LogLevel.Warn), "Exactly one line for the failed save.");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // M2, the reviewer's race: a reading decided against an old claim (ClaimFlow having already redone the
    // claim underneath it) must not overwrite the newer claim once it lands.
    [TestMethod]
    public async Task AnOlderGenerationReadingArrivingAfterARedoneClaimDoesNotOverwriteIt()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var store = NewStore(path, new CapturingLog());
        WidgetClaim originalClaim = SampleClaim();
        store.Save(originalClaim);
        await store.IdleAsync();

        // The redone claim: a brand new claim, a later ClaimedAtUtc, as ClaimFlow makes on a successful redo.
        WidgetClaim redoneClaim = originalClaim with
        {
            ClaimedAtUtc = originalClaim.ClaimedAtUtc + TimeSpan.FromMinutes(1),
            Last = new OwnedBattery(9, 9, 9, originalClaim.ClaimedAtUtc + TimeSpan.FromMinutes(1)),
        };
        store.Save(redoneClaim);

        // A stale reading, decided against the original claim before the redo reached the service, arriving
        // after the redo already landed in the store.
        WidgetClaim staleMerge = originalClaim with { Last = new OwnedBattery(1, 1, 1, originalClaim.ClaimedAtUtc + TimeSpan.FromSeconds(1)) };
        store.Save(staleMerge);
        await store.IdleAsync();

        Assert.AreEqual(redoneClaim, store.Current, "The redone claim must stand; the stale reading must not overwrite it.");

        var reloaded = NewStore(path, new CapturingLog());
        Assert.AreEqual(redoneClaim, reloaded.Current, "The redone claim, not the stale one, must be what is on disk.");
    }

    // Save runs on the UI thread (the service's callback), and so does the Current read. The disk write is
    // queued on a worker, but it used to hold the store's lock for the whole write, so a read or a Save that
    // landed while a write was in flight (a flush to a slow or scanned disk) made the UI thread wait for it.
    // The write is held open here, and both calls must return while it is.
    [TestMethod]
    public async Task ReadingOrSavingWhileAWriteIsInFlightDoesNotWaitForTheDisk()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var store = NewStore(path, new CapturingLog());
        using var entered = new ManualResetEventSlim(initialState: false);
        using var release = new ManualResetEventSlim(initialState: false);
        store.TestHookAfterWriteCheckPassed = () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        };
        WidgetClaim first = SampleClaim();
        WidgetClaim second = first with { Last = new OwnedBattery(9, 9, 9, first.ClaimedAtUtc + TimeSpan.FromMinutes(1)) };

        try
        {
            store.Save(first);
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10)), "The write never started.");

            Task<WidgetClaim?> read = Task.Run(() => store.Current);
            bool readReturned = read.Wait(TimeSpan.FromSeconds(3));
            Task save = Task.Run(() => store.Save(second));
            bool saveReturned = save.Wait(TimeSpan.FromSeconds(3));

            Assert.IsTrue(readReturned, "Current waited for a write that was still in flight.");
            Assert.IsTrue(saveReturned, "Save waited for a write that was still in flight.");
            Assert.AreEqual(second, store.Current, "The newer claim is the current one at once, before either write finishes.");
        }
        finally
        {
            release.Set();
        }

        await store.IdleAsync();
        var reloaded = NewStore(path, new CapturingLog());
        Assert.AreEqual(second, reloaded.Current, "The newer claim is what ends up on disk.");
    }

    // The generation check inside WriteOne and the actual disk write were not atomic with respect to each
    // other: ForgetClaim could delete the file and null out _current in between a write's check passing and
    // that same write actually reaching disk, resurrecting a claim the owner had just asked to forget. This
    // forces the exact interleaving deterministically (a bare race loop was tried first and never landed it
    // on this machine's thread pool; TestHookAfterWriteCheckPassed exists so the window does not depend on
    // scheduling luck) rather than relying on it to happen by chance.
    [TestMethod]
    public async Task ForgetClaimIsNeverResurrectedByAWriteAlreadyPastItsCheck()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var log = new CapturingLog();
        var store = NewStore(path, log);
        Task? forgetTask = null;
        store.TestHookAfterWriteCheckPassed = () => forgetTask = Task.Run(store.ForgetClaim);

        store.Save(SampleClaim());
        await store.IdleAsync();
        Assert.IsNotNull(forgetTask, "The hook must have fired for this test to mean anything.");
        await forgetTask!;
        await store.IdleAsync();

        Assert.IsFalse(File.Exists(path), "ForgetClaim must not be undone by a write that had already passed its check.");
        Assert.IsNull(store.Current);
    }

    // A claim whose ClaimedAtUtc is somehow in the future (a corrupted file, a clock that ran backwards
    // before it wrote) would otherwise make every later, genuine Save look "older" by ClaimStore's own
    // ordering check, and so silently fail to persist forever. Refusing it on load means it never becomes
    // the baseline a real claim is compared against.
    [TestMethod]
    public void AFutureDatedClaimedAtUtcIsRefusedOnLoad()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var future = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string json = Json(claimedAt: future.ToString("O"));
        File.WriteAllText(path, json);
        var log = new CapturingLog();
        var fixedNow = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

        var store = new ClaimStore(path, log, () => fixedNow);

        Assert.IsNull(store.Current);
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "future"));
        Assert.AreEqual(json, File.ReadAllText(path), "A future-dated claim is left in place, never rewritten.");
    }

    // A newer, genuine claim saved after a future-dated one was refused on load must persist normally: the
    // refusal must not leave some other stale generation guard behind.
    [TestMethod]
    public async Task AGenuineClaimSavesNormallyAfterAFutureDatedOneWasRefused()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var future = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        string json = Json(claimedAt: future.ToString("O"));
        File.WriteAllText(path, json);
        var log = new CapturingLog();
        var fixedNow = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        var store = new ClaimStore(path, log, () => fixedNow);

        store.Save(SampleClaim());
        await store.IdleAsync();

        Assert.AreEqual(SampleClaim(), store.Current);
    }

    // Only IOException and UnauthorizedAccessException were ever caught around the actual disk write; any
    // other exception from inside a queued write would escape as an unobserved task exception and simply be
    // lost. Forcing a genuinely different exception type (a NUL character in the path, which throws
    // ArgumentException, verified on this machine) proves it is now logged rather than silently dropped.
    [TestMethod]
    public async Task AnExceptionInsideAQueuedWriteIsLoggedNotUnobserved()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var log = new CapturingLog();
        var store = NewStore(path, log);
        typeof(ClaimStore).GetField("_path", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(store, path + "\0bad");

        store.Save(SampleClaim());
        await store.IdleAsync();

        Assert.IsTrue(
            log.Entries.Any(e => e.Level is Earshot.Contracts.LogLevel.Warn or Earshot.Contracts.LogLevel.Error),
            "An exception from inside a queued write must be logged, not left for an unobserved task exception to lose.");
    }

    // An invalid claim must be recognised, logged and left in place even when the file cannot be written
    // back to, proving nothing here ever attempts to rewrite or quarantine it.
    [TestMethod]
    public void AnInvalidClaimOnAReadOnlyFileIsLeftInPlaceAndLogged()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        string json = Json(schema: 99);
        File.WriteAllText(path, json);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var log = new CapturingLog();

        try
        {
            var store = new ClaimStore(path, log);

            Assert.IsNull(store.Current);
            Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "99"));
            Assert.AreEqual(json, File.ReadAllText(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }
}
