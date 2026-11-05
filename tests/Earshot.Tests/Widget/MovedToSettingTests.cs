using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The Moved to setting: on for a new install and for a file written before it existed, kept as the owner left it, and no consumer
// of the watcher (it reads what the watcher already hears).
[TestClass]
public sealed class MovedToSettingTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("settings.json");

    [TestMethod]
    public void ItIsOnByDefault()
    {
        Assert.IsTrue(WidgetSettings.Default.MovedToOn);
        Assert.IsTrue(new EarshotSettings().Widget.MovedToOn);
        Assert.IsTrue(EarshotSettings.NewInstallDefaults().Widget.MovedToOn, "A new install has it on.");
    }

    [TestMethod]
    public void AFileThatNeverHeldItReadsAsOn()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"Widget\": { \"Enabled\": true, \"ShowOnTaskbar\": true, \"AutoPause\": true } }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.IsTrue(store.Current.Widget.MovedToOn);
    }

    [TestMethod]
    public void AFileThatHoldsItOffKeepsIt()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"Widget\": { \"MovedToOn\": false } }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.IsFalse(store.Current.Widget.MovedToOn);
    }

    [TestMethod]
    public void ItSurvivesASaveAndALoadEitherWay()
    {
        var store = new JsonSettingsStore(SettingsPath, _log);
        store.Update(s => s.Widget = s.Widget with { MovedToOn = false });
        Assert.IsFalse(new JsonSettingsStore(SettingsPath, _log).Current.Widget.MovedToOn);

        store.Update(s => s.Widget = s.Widget with { MovedToOn = true });
        Assert.IsTrue(new JsonSettingsStore(SettingsPath, _log).Current.Widget.MovedToOn);
    }
}
