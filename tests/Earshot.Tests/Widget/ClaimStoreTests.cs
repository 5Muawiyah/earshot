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

    [TestMethod]
    public void SaveIsAtomicAndReadable()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var store = new ClaimStore(path, new CapturingLog());
        WidgetClaim claim = SampleClaim();

        store.Save(claim);

        Assert.IsFalse(File.Exists(path + ".tmp"), "The temporary file must not be left behind.");
        Assert.AreEqual(claim, store.Current);

        var reloaded = new ClaimStore(path, new CapturingLog());
        Assert.AreEqual(claim, reloaded.Current);
    }

    [TestMethod]
    public void ForgetDeletesTheFile()
    {
        using var temp = new TempFolder();
        string path = temp.File("claim.json");
        var store = new ClaimStore(path, new CapturingLog());
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
        var store = new ClaimStore(paths.WidgetClaimFile, new CapturingLog());

        store.Save(SampleClaim());

        Assert.IsTrue(File.Exists(paths.WidgetClaimFile));
        Assert.IsTrue(
            paths.WidgetClaimFile.StartsWith(Path.Combine(temp.Path, "Local"), StringComparison.OrdinalIgnoreCase),
            paths.WidgetClaimFile);
    }
}
