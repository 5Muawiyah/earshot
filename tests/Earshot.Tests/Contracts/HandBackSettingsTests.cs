using Earshot.Contracts;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Contracts;

// T1 (handback-on-shutdown-and-sleep, section 7 and 11): the setting defaults on, an older file with no
// member reads as on, and the value round-trips through the shipped source-generated serialiser without
// moving SchemaVersion.
[TestClass]
public sealed class HandBackSettingsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("settings.json");

    private JsonSettingsStore Open() => new(SettingsPath, _log);

    [TestMethod]
    public void ANewSettingsObjectDefaultsHandBackOn()
    {
        Assert.IsTrue(new EarshotSettings().HandBackOnShutdownAndSleep);
    }

    [TestMethod]
    public void AnOlderFileWithNoHandBackMemberReadsOn()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"DeviceMatch\": \"Beats\" }");

        JsonSettingsStore store = Open();

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.IsTrue(store.Current.HandBackOnShutdownAndSleep);
        Assert.AreEqual(1, store.Current.SchemaVersion, "L17: the newer-schema rule is untouched by this member.");
    }

    [TestMethod]
    public void FalseRoundTripsThroughTheShippedSerialiser()
    {
        Open().Update(s => s.HandBackOnShutdownAndSleep = false);

        JsonSettingsStore reopened = Open();

        Assert.AreEqual(SettingsLoadStatus.Loaded, reopened.LastLoadStatus);
        Assert.IsFalse(reopened.Current.HandBackOnShutdownAndSleep);
        Assert.AreEqual(1, reopened.Current.SchemaVersion);
    }

    [TestMethod]
    public void TrueRoundTripsThroughTheShippedSerialiser()
    {
        Open().Update(s => s.HandBackOnShutdownAndSleep = false);
        Open().Update(s => s.HandBackOnShutdownAndSleep = true);

        JsonSettingsStore reopened = Open();

        Assert.IsTrue(reopened.Current.HandBackOnShutdownAndSleep);
    }
}
