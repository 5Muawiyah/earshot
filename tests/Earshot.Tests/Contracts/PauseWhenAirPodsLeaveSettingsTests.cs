using Earshot.Contracts;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Contracts;

// The setting defaults on, an older file with no member reads as on, and the value round-trips through the shipped
// source-generated serialiser without moving SchemaVersion.
[TestClass]
public sealed class PauseWhenAirPodsLeaveSettingsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("settings.json");

    private JsonSettingsStore Open() => new(SettingsPath, _log);

    [TestMethod]
    public void ANewSettingsObjectDefaultsPauseOn()
    {
        Assert.IsTrue(new EarshotSettings().PauseWhenAirPodsLeave);
    }

    [TestMethod]
    public void AnOlderFileWithNoPauseMemberReadsOn()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"DeviceMatch\": \"Beats\", \"HandBackOnShutdownAndSleep\": false }");

        JsonSettingsStore store = Open();

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.IsTrue(store.Current.PauseWhenAirPodsLeave);
        Assert.IsFalse(store.Current.HandBackOnShutdownAndSleep, "The member beside it was read as well.");
        Assert.AreEqual(1, store.Current.SchemaVersion, "The newer-schema rule is untouched by this member.");
    }

    [TestMethod]
    public void FalseRoundTripsThroughTheShippedSerialiser()
    {
        Open().Update(s => s.PauseWhenAirPodsLeave = false);

        JsonSettingsStore reopened = Open();

        Assert.AreEqual(SettingsLoadStatus.Loaded, reopened.LastLoadStatus);
        Assert.IsFalse(reopened.Current.PauseWhenAirPodsLeave);
        Assert.IsTrue(reopened.Current.HandBackOnShutdownAndSleep, "Turning one off moved the other.");
        Assert.AreEqual(1, reopened.Current.SchemaVersion);
    }

    [TestMethod]
    public void TrueRoundTripsThroughTheShippedSerialiser()
    {
        Open().Update(s => s.PauseWhenAirPodsLeave = false);
        Open().Update(s => s.PauseWhenAirPodsLeave = true);

        Assert.IsTrue(Open().Current.PauseWhenAirPodsLeave);
    }
}
