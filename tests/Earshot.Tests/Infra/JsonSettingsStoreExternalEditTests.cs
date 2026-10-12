using Earshot.Contracts;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Infra;

// A running tray and a second program (the settings command, a hand edit) share settings.json. These pin what each side must not
// lose when their saves cross.
[TestClass]
public sealed class JsonSettingsStoreExternalEditTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("settings.json");

    [TestMethod]
    public void ATraysSaveKeepsAnEditAnotherProgramMadeBeforeTheTrayHadReadItBack()
    {
        var tray = new JsonSettingsStore(SettingsPath, new CapturingLog());
        Assert.AreEqual(20, tray.Current.Widget.LowBatteryThresholdPercent);

        // The settings command saves through a store of its own, and the tray saves something else before its watcher has
        // settled and read that edit.
        var command = new JsonSettingsStore(SettingsPath, new CapturingLog());
        command.Update(s => s.Widget = s.Widget with { LowBatteryThresholdPercent = 60 });
        tray.Update(s => s.CheckForUpdatesAutomatically = true);

        var after = new JsonSettingsStore(SettingsPath, new CapturingLog(), readOnly: true);
        Assert.AreEqual(60, after.Current.Widget.LowBatteryThresholdPercent, "The command's edit was overwritten by the tray's save.");
        Assert.IsTrue(after.Current.CheckForUpdatesAutomatically, "The tray's own change was lost.");
        Assert.AreEqual(60, tray.Current.Widget.LowBatteryThresholdPercent, "The tray did not take the edit up either.");
    }

    [TestMethod]
    public void AnUpdateAfterAnEditIsRaisedOnceWithTheMergedValues()
    {
        var tray = new JsonSettingsStore(SettingsPath, new CapturingLog());
        var seen = new List<EarshotSettings>();
        tray.Changed += (_, s) => seen.Add(s);
        var command = new JsonSettingsStore(SettingsPath, new CapturingLog());
        command.Update(s => s.Widget = s.Widget with { LowBatteryThresholdPercent = 60 });

        tray.Update(s => s.CheckForUpdatesAutomatically = true);

        Assert.HasCount(1, seen);
        Assert.AreEqual(60, seen[0].Widget.LowBatteryThresholdPercent);
        Assert.IsTrue(seen[0].CheckForUpdatesAutomatically);
    }

    [TestMethod]
    public void AnUpdateWhenNobodyElseWroteTheFileStillSavesTheChange()
    {
        var tray = new JsonSettingsStore(SettingsPath, new CapturingLog());

        tray.Update(s => s.CheckForUpdatesAutomatically = true);
        tray.Update(s => s.Widget = s.Widget with { LowBatteryThresholdPercent = 40 });

        var after = new JsonSettingsStore(SettingsPath, new CapturingLog(), readOnly: true);
        Assert.IsTrue(after.Current.CheckForUpdatesAutomatically);
        Assert.AreEqual(40, after.Current.Widget.LowBatteryThresholdPercent);
    }

    [TestMethod]
    public void AReloadThatCannotReadTheFileYetLogsAWarningAndAsksToBeTriedAgain()
    {
        var log = new CapturingLog();
        var tray = new JsonSettingsStore(SettingsPath, log);
        var command = new JsonSettingsStore(SettingsPath, new CapturingLog());
        command.Update(s => s.Widget = s.Widget with { LowBatteryThresholdPercent = 60 });

        SettingsReload result;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = tray.ReloadIfFileChanged();
        }

        Assert.AreEqual(SettingsReload.TryAgain, result);
        Assert.IsTrue(log.Has(LogLevel.Warn, "not read for a reload"), "A file that could not be read for a reload must be logged at Warning.");
        Assert.AreEqual(SettingsReload.Reloaded, tray.ReloadIfFileChanged(), "Once the file can be read, the edit is taken up.");
        Assert.AreEqual(60, tray.Current.Widget.LowBatteryThresholdPercent);
    }

    [TestMethod]
    public void AReloadWithNothingNewReportsUnchanged()
    {
        var tray = new JsonSettingsStore(SettingsPath, new CapturingLog());

        Assert.AreEqual(SettingsReload.Unchanged, tray.ReloadIfFileChanged());
    }
}
