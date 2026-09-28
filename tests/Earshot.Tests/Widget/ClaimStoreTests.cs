using System.Reflection;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class ClaimStoreTests
{
    private static WidgetClaim SampleClaim() => new(
        SchemaVersion: 1,
        ModelHigh: WidgetFixtures.ModelHigh,
        ModelLow: WidgetFixtures.ModelLow,
        Colour: WidgetFixtures.Colour,
        SignalThresholdDbm: -70,
        ClaimedAtUtc: new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero),
        Last: new OwnedBattery(5, 6, 7, new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero)));

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

    // SampleClaim's threshold is fixed at -70; every test below that saves it supplies a matching
    // currentSignalThreshold so the store's own post-save reload does not invalidate what it just wrote
    // a stored threshold is usable only when it equals the current one.
    private static ClaimStore NewStore(string path, ILog log) => new(path, log, static () => (sbyte)-70);

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

    // M1, reviewer probes 2 and 3: a claim whose stored threshold no longer matches phase 0's current one
    // (here, the production default, which ships null) is unusable, whatever else in the file is valid.
    [TestMethod]
    public async Task AClaimWhoseThresholdNoLongerMatchesTheCurrentOneIsNoClaimAndLogged()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var writer = NewStore(path, new CapturingLog());
        writer.Save(SampleClaim());
        await writer.IdleAsync();
        var log = new CapturingLog();

        var store = new ClaimStore(path, log); // the real, un-overridden default: WidgetDefaults.SignalThresholdDbm, null

        Assert.IsNull(store.Current);
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "threshold"));
        Assert.AreNotEqual(0, new FileInfo(path).Length, "The file must be left in place, not deleted or rewritten.");
    }

    // Probe 3: a newer schema version is never used, whatever threshold it carries.
    [TestMethod]
    public void ANewerSchemaVersionIsNoClaimNeverRewrittenAndLogged()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        string json =
            "{\"SchemaVersion\":99,\"ModelHigh\":238,\"ModelLow\":238,\"Colour\":238," +
            "\"SignalThresholdDbm\":-128,\"ClaimedAtUtc\":\"2026-09-27T00:00:00+00:00\"," +
            "\"Last\":{\"NibbleHigh\":5,\"NibbleLow\":6,\"Case\":7,\"AtUtc\":\"2026-09-27T00:00:00+00:00\"}}";
        File.WriteAllText(path, json);
        var log = new CapturingLog();

        var store = new ClaimStore(path, log, static () => (sbyte)-128);

        Assert.IsNull(store.Current);
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "99"));
        Assert.AreEqual(json, File.ReadAllText(path), "An invalid claim file must be left exactly as it was.");
    }

    // A claim file written before NibblesAreNamedOrder existed has no such member at all. Schema stays 1; a
    // missing member must read as false (wire order), which is what every such file actually holds, rather
    // than fail to load or default to something that would misread its nibbles.
    [TestMethod]
    public void AnOlderClaimFileLoadsAsWireOrder()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        string json =
            "{\"SchemaVersion\":1,\"ModelHigh\":238,\"ModelLow\":238,\"Colour\":238," +
            "\"SignalThresholdDbm\":-70,\"ClaimedAtUtc\":\"2026-09-27T00:00:00+00:00\"," +
            "\"Last\":{\"NibbleHigh\":5,\"NibbleLow\":6,\"Case\":7,\"AtUtc\":\"2026-09-27T00:00:00+00:00\"}}";
        File.WriteAllText(path, json);

        var store = new ClaimStore(path, new CapturingLog(), static () => (sbyte)-70);

        Assert.IsNotNull(store.Current);
        Assert.IsFalse(store.Current!.NibblesAreNamedOrder, "A file with no such member must read as wire order.");
    }

    // Probe 2: a file with no last reading at all is not a usable claim.
    [TestMethod]
    public void AClaimWithNoLastReadingIsNoClaimAndLogged()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        string json =
            "{\"SchemaVersion\":1,\"ModelHigh\":238,\"ModelLow\":238,\"Colour\":238," +
            "\"SignalThresholdDbm\":-70,\"ClaimedAtUtc\":\"2026-09-27T00:00:00+00:00\"}";
        File.WriteAllText(path, json);
        var log = new CapturingLog();

        var store = new ClaimStore(path, log, static () => (sbyte)-70);

        Assert.IsNull(store.Current);
        Assert.IsTrue(log.Has(Earshot.Contracts.LogLevel.Warn, "last"));
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
        var store = new ClaimStore(path, log, static () => (sbyte)-70);
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
        string json =
            "{\"SchemaVersion\":1,\"ModelHigh\":238,\"ModelLow\":238,\"Colour\":238," +
            "\"SignalThresholdDbm\":-70,\"ClaimedAtUtc\":\"" + future.ToString("O") + "\"," +
            "\"Last\":{\"NibbleHigh\":5,\"NibbleLow\":6,\"Case\":7,\"AtUtc\":\"" + future.ToString("O") + "\"}}";
        File.WriteAllText(path, json);
        var log = new CapturingLog();
        var fixedNow = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

        var store = new ClaimStore(path, log, static () => (sbyte)-70, () => fixedNow);

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
        string json =
            "{\"SchemaVersion\":1,\"ModelHigh\":238,\"ModelLow\":238,\"Colour\":238," +
            "\"SignalThresholdDbm\":-70,\"ClaimedAtUtc\":\"" + future.ToString("O") + "\"," +
            "\"Last\":{\"NibbleHigh\":5,\"NibbleLow\":6,\"Case\":7,\"AtUtc\":\"" + future.ToString("O") + "\"}}";
        File.WriteAllText(path, json);
        var log = new CapturingLog();
        var fixedNow = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        var store = new ClaimStore(path, log, static () => (sbyte)-70, () => fixedNow);

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
        string json =
            "{\"SchemaVersion\":99,\"ModelHigh\":238,\"ModelLow\":238,\"Colour\":238," +
            "\"SignalThresholdDbm\":-70,\"ClaimedAtUtc\":\"2026-09-27T00:00:00+00:00\"," +
            "\"Last\":{\"NibbleHigh\":5,\"NibbleLow\":6,\"Case\":7,\"AtUtc\":\"2026-09-27T00:00:00+00:00\"}}";
        File.WriteAllText(path, json);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var log = new CapturingLog();

        try
        {
            var store = new ClaimStore(path, log, static () => (sbyte)-70);

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
