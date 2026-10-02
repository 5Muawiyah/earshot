using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Hotkeys;

// The card shortcut (Ctrl+Alt+Shift+E): on by default for a new install and for a settings file written before it
// existed, registered with that chord, clearable and staying cleared across a restart, and reported on the card the
// way the other two are when another program holds it. Read through the real settings store and the real manager.
[TestClass]
public sealed class CardShortcutTests
{
    private const int CardId = HotkeyManager.HotkeyIdBase + (int)HotkeyAction.OpenCard;

    private const string FileWithoutTheMember =
        "{ \"SchemaVersion\": 1, \"Hotkeys\": { \"Enabled\": true, \"SwitchToPc\": \"Ctrl+Alt+Shift+A\", \"SwitchToPhone\": \"Ctrl+Alt+Shift+D\" } }";

    private static HotkeySettings Load(string content)
    {
        var temp = new TempFolder();
        string path = temp.File("settings.json");
        File.WriteAllText(path, content);
        var store = new JsonSettingsStore(path, new CapturingLog());
        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        return store.Current.Hotkeys;
    }

    [TestMethod]
    public void ANewInstallHasTheCardShortcutOn()
    {
        Assert.AreEqual("Ctrl+Alt+Shift+E", HotkeySettings.Defaults.OpenCard);
        Assert.AreEqual("Ctrl+Alt+Shift+E", new EarshotSettings().Hotkeys.OpenCard);
    }

    [TestMethod]
    public void AFileWrittenBeforeTheMemberExistedGetsTheDefaultAndKeepsTheOthers()
    {
        HotkeySettings hotkeys = Load(FileWithoutTheMember);

        Assert.AreEqual(HotkeySettings.DefaultOpenCard, hotkeys.OpenCard);
        Assert.AreEqual("Ctrl+Alt+Shift+A", hotkeys.SwitchToPc);
        Assert.AreEqual("Ctrl+Alt+Shift+D", hotkeys.SwitchToPhone);
        Assert.IsTrue(hotkeys.Enabled);
    }

    // The file the build before this one wrote to a machine where shortcuts were never touched: shortcuts "off" with
    // nothing typed. The switch migration turns them on, and the card shortcut comes with them.
    [TestMethod]
    public void TheOldestFileGetsAllThreeDefaults()
    {
        HotkeySettings hotkeys = Load("{ \"SchemaVersion\": 1, \"Hotkeys\": { \"Enabled\": false, \"ToggleConnection\": \"\" } }");

        Assert.IsTrue(hotkeys.Enabled);
        Assert.AreEqual(HotkeySettings.DefaultOpenCard, hotkeys.OpenCard);
    }

    [TestMethod]
    public void AClearedShortcutStaysClearedWhenTheFileIsReadAndAfterASaveAndRestart()
    {
        HotkeySettings read = Load("{ \"SchemaVersion\": 1, \"Hotkeys\": { \"Enabled\": true, \"SwitchToPc\": \"Ctrl+Alt+Shift+A\", \"SwitchToPhone\": \"Ctrl+Alt+Shift+D\", \"OpenCard\": \"\" } }");
        Assert.AreEqual(string.Empty, read.OpenCard, "A member the file holds, empty, is the owner's.");

        var temp = new TempFolder();
        var log = new CapturingLog();
        string path = temp.File("settings.json");
        File.WriteAllText(path, FileWithoutTheMember);

        var first = new JsonSettingsStore(path, log);
        Assert.AreEqual(HotkeySettings.DefaultOpenCard, first.Current.Hotkeys.OpenCard);
        first.Update(s => s.Hotkeys.OpenCard = string.Empty);

        var second = new JsonSettingsStore(path, log);
        Assert.AreEqual(string.Empty, second.Current.Hotkeys.OpenCard, "The default came back after a restart.");

        // And a save that changes something else does not bring it back either.
        second.Update(s => s.Hotkeys.SwitchToPc = "Ctrl+Alt+P");
        Assert.AreEqual(string.Empty, new JsonSettingsStore(path, log).Current.Hotkeys.OpenCard);
    }

    [TestMethod]
    public void ATypedChordThatMatchesTheDefaultKeepsItAndTheDefaultStepsAside()
    {
        HotkeySettings hotkeys = Load("{ \"SchemaVersion\": 1, \"Hotkeys\": { \"Enabled\": true, \"ToggleConnection\": \"Ctrl+Alt+Shift+E\", \"SwitchToPc\": \"Ctrl+Alt+Shift+A\", \"SwitchToPhone\": \"Ctrl+Alt+Shift+D\" } }");

        Assert.AreEqual("Ctrl+Alt+Shift+E", hotkeys.ToggleConnection);
        Assert.AreEqual(string.Empty, hotkeys.OpenCard);
    }

    [TestMethod]
    public void TheShippedSerialiserWritesTheMember()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new EarshotSettings(), SettingsJsonContext.Default.EarshotSettings);

        using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual("Ctrl+Alt+Shift+E", parsed.RootElement.GetProperty("Hotkeys").GetProperty("OpenCard").GetString());
    }

    [TestMethod]
    public void TheDefaultChordIsRegisteredWithControlAltShiftE()
    {
        var window = new FakeMessageWindow();
        var native = new FakeNativeHotkeys();
        using var manager = new HotkeyManager(window, native, new CapturingLog());

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(HotkeySettings.Defaults);

        NativeCall call = native.Calls.Single(c => c.Id == CardId);
        Assert.AreEqual("RegisterHotKey", call.Method);
        Assert.AreEqual(0x45u, call.VirtualKey, "E.");
        Assert.AreEqual((uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift) | 0x4000u, call.Modifiers);
        Assert.AreEqual(HotkeyRegistrationState.Registered, outcomes.Single(o => o.Action == HotkeyAction.OpenCard).State);
    }

    [TestMethod]
    public void AClearedShortcutIsNotRegistered()
    {
        var native = new FakeNativeHotkeys();
        using var manager = new HotkeyManager(new FakeMessageWindow(), native, new CapturingLog());
        var settings = new HotkeySettings { OpenCard = string.Empty };

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(settings);

        Assert.AreEqual(HotkeyRegistrationState.NotSet, outcomes.Single(o => o.Action == HotkeyAction.OpenCard).State);
        Assert.IsFalse(native.Calls.Any(c => c.Id == CardId));
    }

    [TestMethod]
    public void AChordAnotherProgramHoldsIsReportedWithWindowsOwnCode()
    {
        var native = new FakeNativeHotkeys();
        native.SetResult(CardId, NativeCallResult.Failure(1409));
        using var manager = new HotkeyManager(new FakeMessageWindow(), native, new CapturingLog());

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(HotkeySettings.Defaults);

        HotkeyRegistrationOutcome card = outcomes.Single(o => o.Action == HotkeyAction.OpenCard);
        Assert.AreEqual(HotkeyRegistrationState.AlreadyHeld, card.State);
        Assert.AreEqual("Ctrl+Alt+Shift+E is already in use by another program, so it was not set. Windows reported error 1409.", card.Message);
        Assert.AreEqual(HotkeyRegistrationState.Registered, outcomes.Single(o => o.Action == HotkeyAction.SwitchToPc).State);
    }

    [TestMethod]
    public void ThePressRaisesTheOpenCardAction()
    {
        var window = new FakeMessageWindow();
        using var manager = new HotkeyManager(window, new FakeNativeHotkeys(), new CapturingLog());
        manager.Apply(HotkeySettings.Defaults);
        var raised = new List<HotkeyAction>();
        manager.Activated += (_, e) => raised.Add(e.Action);

        window.Raise(Earshot.Interop.NativeMethods.WM_HOTKEY, CardId, 0);

        CollectionAssert.AreEqual(new[] { HotkeyAction.OpenCard }, raised);
    }

    // The settings page binds the row to this model: set, a clash with another command, and clear.
    [TestMethod]
    public void TheBindingModelSetsClearsAndRefusesAClashForTheCardShortcut()
    {
        var settings = new HotkeySettings();
        var model = new HotkeyBindingModel(settings, () => []);

        Assert.AreEqual(HotkeySettings.DefaultOpenCard, model.Chord(HotkeyAction.OpenCard));
        Assert.IsNull(model.Set(HotkeyAction.OpenCard, "Ctrl+Alt+K"));
        Assert.AreEqual("Ctrl+Alt+K", settings.OpenCard);
        StringAssert.Contains(model.Set(HotkeyAction.OpenCard, "Ctrl+Alt+Shift+A")!, "already set for another command");
        Assert.AreEqual("Ctrl+Alt+K", settings.OpenCard, "A refused chord changes nothing.");

        model.Clear(HotkeyAction.OpenCard);
        Assert.AreEqual(string.Empty, settings.OpenCard);
    }
}
