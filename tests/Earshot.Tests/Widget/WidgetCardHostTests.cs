using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase1;
using Earshot.Tests.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The seam the card's pages use, over the real TrayContext: what the settings page reads is what is saved, and each
// write lands in the real settings store by the path the tray menu's own item uses. The data root is a temporary
// folder for each test, so nothing here touches the owner's settings.
[TestClass]
public sealed class WidgetCardHostTests
{
    private static readonly Earshot.App.CardPlace Place = Earshot.App.CardPlace.NearTray;

    // The widget reaches nothing of the update flow, so its own copy of these words must stay the flow's.
    [TestMethod]
    public void TheCardsUpdateWordsAreTheUpdateFlowsWords()
    {
        string[] theirs =
        [
            Earshot.Update.UpdateCopy.CheckRowLabel, Earshot.Update.UpdateCopy.CheckRowButton,
            Earshot.Update.UpdateCopy.CheckAutomaticallyLabel, Earshot.Update.UpdateCopy.UpdateButton,
        ];
        string[] ours = [WidgetCopy.CheckForUpdates, WidgetCopy.CheckButton, WidgetCopy.CheckAutomatically, WidgetCopy.UpdateButton];
        CollectionAssert.AreEqual(theirs, ours);
    }

    [TestMethod]
    public void TheHostReadsWhatIsSavedAndSaysWhichFeaturesStillWaitOnAProvedField()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness(settings: s => s.Widget = s.Widget with { OtherDeviceLabel = "Pixel", LowBatteryThresholdPercent = 40 });

            CardSettingsValues values = tray.Context.WidgetCardHostForTest.ReadSettings();

            Assert.AreEqual(GaugePosition.RightEnd, values.GaugePosition);
            Assert.AreEqual("Pixel", values.OtherDeviceLabel);
            Assert.AreEqual(40, values.LowBatteryPercent);
            Assert.IsTrue(values.PauseWhenAirPodsLeave, "Saved as on by default.");
            Assert.IsFalse(values.HandBack, "Saved as off by default.");
            Assert.IsFalse(values.LeftClickConnects);
            Assert.IsFalse(values.CheckAutomatically, "No automatic checks by default.");
            Assert.AreEqual("Ctrl+Alt+Shift+A", values.ConnectChord);
            Assert.AreEqual("Ctrl+Alt+Shift+D", values.DisconnectChord);
            Assert.AreEqual(Earshot.Update.ReleaseVersion.Running(typeof(Earshot.Update.ReleaseVersion).Assembly)!.Value.ToString(), values.InstalledVersion);
            Assert.IsTrue(values.InEarProofMissing, "The in-ear signal is not known.");
        });
    }

    [TestMethod]
    public void EachSetterWritesItsOwnSetting()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();
            IWidgetCardHost host = tray.Context.WidgetCardHostForTest;

            host.SetGaugePosition(GaugePosition.NextToApps, Place);
            host.SetLowBatteryPercent(60, Place);
            host.SetLeftClickConnects(true, Place);
            host.SetPauseWhenAirPodsLeave(false, Place);
            host.SetCheckAutomatically(true, Place);
            tray.PumpUntilIdle();

            EarshotSettings saved = tray.Settings.Current;
            Assert.AreEqual(GaugePosition.NextToApps, saved.Widget.GaugePosition);
            Assert.AreEqual(60, saved.Widget.LowBatteryThresholdPercent);
            Assert.IsTrue(saved.Widget.LeftClickConnects);
            Assert.IsFalse(saved.PauseWhenAirPodsLeave);
            Assert.IsTrue(saved.CheckForUpdatesAutomatically);
            Assert.IsFalse(saved.HandBackOnShutdownAndSleep, "Nothing else changed.");
            Assert.AreEqual(GaugePosition.NextToApps, host.ReadSettings().GaugePosition, "And the page reads it back.");
        });
    }

    [TestMethod]
    public void TheOtherDevicesNameIsSavedCleaned()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();
            IWidgetCardHost host = tray.Context.WidgetCardHostForTest;

            host.SetOtherDeviceLabel("  Pi​xel‮  ", Place);
            tray.PumpUntilIdle();

            Assert.AreEqual("Pixel", tray.Settings.Current.Widget.OtherDeviceLabel, "The same cleaning the name form applies.");
        });
    }

    // The row that switches a consumer of the watcher keeps the watcher's own flag true to what still needs it.
    [TestMethod]
    public void ThePauseRowKeepsTheWatcherFlagInStepWithWhatNeedsIt()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();
            IWidgetCardHost host = tray.Context.WidgetCardHostForTest;
            Assert.IsFalse(tray.Settings.Current.Widget.Enabled, "The harness starts with every consumer off.");

            host.SetPauseWhenBudComesOut(true, Place);
            Assert.IsTrue(tray.Settings.Current.Widget.Enabled, "A consumer on wakes the watcher.");

            host.SetPauseWhenBudComesOut(false, Place);
            Assert.IsFalse(tray.Settings.Current.Widget.Enabled, "No consumer left: it can rest.");
            tray.PumpUntilIdle();
        });
    }

    [TestMethod]
    public void TheHandBackRowWritesTheSettingLikeTheMenuTickDoes()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();
            IWidgetCardHost host = tray.Context.WidgetCardHostForTest;

            host.SetHandBack(false, Place);
            tray.PumpUntilIdle();

            Assert.IsFalse(tray.Settings.Current.HandBackOnShutdownAndSleep);
            Assert.IsFalse(host.ReadSettings().HandBack);
            Assert.IsTrue(
                tray.Log.Entries.Any(e => e.Message.Contains("sethandback-off", StringComparison.Ordinal)),
                "It was carried to the service's configuration through the menu's own mirror path. The log held: " + string.Join(" | ", tray.Log.Entries.Select(e => e.Message)));

            host.SetHandBack(true, Place);
            tray.PumpUntilIdle();
            Assert.IsTrue(tray.Settings.Current.HandBackOnShutdownAndSleep);
        });
    }

    [TestMethod]
    public void AChordThatWouldRegisterIsSavedAndClearedThroughTheBindingModel()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();
            IWidgetCardHost host = tray.Context.WidgetCardHostForTest;

            string? saved = host.SetShortcut(CardShortcut.Connect, Keys.P, control: true, alt: true, shift: false, Place);

            Assert.IsNull(saved);
            Assert.AreEqual("Ctrl+Alt+P", tray.Settings.Current.Hotkeys.SwitchToPc);
            Assert.AreEqual("Ctrl+Alt+P", host.ReadSettings().ConnectChord);
            Assert.AreEqual("Ctrl+Alt+Shift+D", tray.Settings.Current.Hotkeys.SwitchToPhone, "The other shortcut is untouched.");

            host.ClearShortcut(CardShortcut.Connect, Place);

            Assert.AreEqual(string.Empty, tray.Settings.Current.Hotkeys.SwitchToPc, "A cleared shortcut stays cleared.");
            Assert.AreEqual(string.Empty, host.ReadSettings().ConnectChord);
        });
    }

    [TestMethod]
    public void AChordWindowsKeepsOrAnotherCommandHoldsIsRefusedWithItsReasonAndNothingIsSaved()
    {
        using var temp = new TempFolder();
        using var root = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", temp.Path);
        StaThread.Run(() =>
        {
            using var tray = new UpdateTrayHarness();
            IWidgetCardHost host = tray.Context.WidgetCardHostForTest;

            string? f12 = host.SetShortcut(CardShortcut.Connect, Keys.F12, control: true, alt: false, shift: false, Place);
            string? clash = host.SetShortcut(CardShortcut.Connect, Keys.D, control: true, alt: true, shift: true, Place);
            string? notAChord = host.SetShortcut(CardShortcut.Connect, Keys.P, control: false, alt: false, shift: false, Place);
            string? noName = host.SetShortcut(CardShortcut.Connect, Keys.ProcessKey, control: true, alt: false, shift: false, Place);

            Assert.AreEqual("F12 is kept by Windows for the debugger, so it cannot be a shortcut.", f12);
            StringAssert.Contains(clash!, "already set for another command");
            Assert.IsNotNull(notAChord);
            Assert.AreEqual("That key cannot be used in a shortcut.", noName);
            Assert.AreEqual("Ctrl+Alt+Shift+A", tray.Settings.Current.Hotkeys.SwitchToPc, "A refused chord saves nothing.");
            Assert.AreEqual("Ctrl+Alt+Shift+D", tray.Settings.Current.Hotkeys.SwitchToPhone);
        });
    }
}
