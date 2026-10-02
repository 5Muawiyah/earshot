using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The settings the owner's decisions of 2 October 2026 set: the fully charged notice is on for a new and an existing install, and
// "Check automatically" is off for a new install while an existing file keeps what it saved. Against the real store and its
// source-generated context.
[TestClass]
public sealed class DesignSettingsDefaultsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("settings.json");

    [TestMethod]
    public void TheFullyChargedNoticeIsOnByDefault()
    {
        Assert.IsTrue(WidgetSettings.Default.FullyChargedNotice);
        Assert.IsTrue(new EarshotSettings().Widget.FullyChargedNotice);
        Assert.IsTrue(EarshotSettings.NewInstallDefaults().Widget.FullyChargedNotice, "A new install has it on.");
    }

    [TestMethod]
    public void AnExistingFileThatNeverHeldTheMemberReadsAsOn()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"Widget\": { \"Enabled\": true, \"ShowOnTaskbar\": true, \"AutoPause\": true } }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.IsTrue(store.Current.Widget.FullyChargedNotice, "A file written before the member existed has it on.");
    }

    [TestMethod]
    public void AnExistingFileThatHoldsOffKeepsIt()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"Widget\": { \"FullyChargedNotice\": false } }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.IsFalse(store.Current.Widget.FullyChargedNotice);
    }

    [TestMethod]
    public void TheNoticeSurvivesASaveAndALoad()
    {
        var store = new JsonSettingsStore(SettingsPath, _log);
        store.Update(s => s.Widget = s.Widget with { FullyChargedNotice = false });

        var reloaded = new JsonSettingsStore(SettingsPath, _log);

        Assert.IsFalse(reloaded.Current.Widget.FullyChargedNotice);
    }

    [TestMethod]
    public void CheckAutomaticallyIsOffForANewInstall()
    {
        Assert.IsFalse(new EarshotSettings().CheckForUpdatesAutomatically);
        Assert.IsFalse(EarshotSettings.NewInstallDefaults().CheckForUpdatesAutomatically);
    }

    [TestMethod]
    public void AnExistingFileKeepsItsSavedCheckAutomaticallyOnOrOff()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"CheckForUpdatesAutomatically\": true }");
        Assert.IsTrue(new JsonSettingsStore(SettingsPath, _log).Current.CheckForUpdatesAutomatically, "On stays on.");

        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"CheckForUpdatesAutomatically\": false }");
        Assert.IsFalse(new JsonSettingsStore(SettingsPath, _log).Current.CheckForUpdatesAutomatically, "Off stays off.");

        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1 }");
        Assert.IsFalse(new JsonSettingsStore(SettingsPath, _log).Current.CheckForUpdatesAutomatically, "A file that never held it reads off.");
    }

    [TestMethod]
    public void HandBackAndOpenOnStartupAreOnForANewInstallAndTheOtherDeviceIsIPhone()
    {
        EarshotSettings fresh = EarshotSettings.NewInstallDefaults();
        Assert.IsTrue(fresh.HandBackOnShutdownAndSleep);
        Assert.IsTrue(fresh.OpenOnStartup);
        Assert.AreEqual("iPhone", fresh.Widget.OtherDeviceLabel);
        Assert.IsTrue(fresh.Widget.CaseOpenCardOn, "The case-open card is on.");
    }
}
