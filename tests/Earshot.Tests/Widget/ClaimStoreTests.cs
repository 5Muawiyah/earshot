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
    // (M1: a stored threshold is usable only when it equals the current one).
    private static ClaimStore NewStore(string path, ILog log) => new(path, log, static () => (sbyte)-70);

    [TestMethod]
    public void SaveIsAtomicAndReadable()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var store = NewStore(path, new CapturingLog());
        WidgetClaim claim = SampleClaim();

        store.Save(claim);

        Assert.IsFalse(File.Exists(path + ".tmp"), "The temporary file must not be left behind.");
        Assert.AreEqual(claim, store.Current);

        var reloaded = NewStore(path, new CapturingLog());
        Assert.AreEqual(claim, reloaded.Current);
    }

    [TestMethod]
    public void ForgetDeletesTheFile()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var store = NewStore(path, new CapturingLog());
        store.Save(SampleClaim());
        Assert.IsTrue(File.Exists(path));

        store.ForgetClaim();

        Assert.IsFalse(File.Exists(path));
        Assert.IsNull(store.Current);
    }

    [TestMethod]
    public void ThePathIsUnderTheLocalFolderAndFollowsTheDataRoot()
    {
        using var temp = new TempFolder();
        Paths paths = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? temp.Path : null);
        var store = NewStore(paths.WidgetClaimFile, new CapturingLog());

        store.Save(SampleClaim());

        Assert.IsTrue(File.Exists(paths.WidgetClaimFile));
        Assert.IsTrue(
            paths.WidgetClaimFile.StartsWith(Path.Combine(temp.Path, "Local"), StringComparison.OrdinalIgnoreCase),
            paths.WidgetClaimFile);
    }

    // M1, reviewer probes 2 and 3: a claim whose stored threshold no longer matches phase 0's current one
    // (here, the production default, which ships null) is unusable, whatever else in the file is valid.
    [TestMethod]
    public void AClaimWhoseThresholdNoLongerMatchesTheCurrentOneIsNoClaimAndLogged()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var writer = NewStore(path, new CapturingLog());
        writer.Save(SampleClaim());
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

    // M1's "plus a read-only file": an invalid claim must be recognised, logged and left in place even when
    // the file cannot be written back to, proving nothing here ever attempts to rewrite or quarantine it.
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
