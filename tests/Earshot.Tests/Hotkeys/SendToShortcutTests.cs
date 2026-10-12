using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Hotkeys;

// The send-to shortcut (Ctrl+Alt+Shift+S): on by default for a new install and for a settings file written before it
// existed, registered with that chord, clearable and staying cleared across a restart, and reported when another program
// holds it. Read through the real settings store and the real manager.
[TestClass]
public sealed class SendToShortcutTests
{
    private const int SendToId = HotkeyManager.HotkeyIdBase + (int)HotkeyAction.SendTo;

    private const string FileWithoutTheMember =
        "{ \"SchemaVersion\": 1, \"Hotkeys\": { \"Enabled\": true, \"SwitchToPc\": \"Ctrl+Alt+Shift+A\", \"SwitchToPhone\": \"Ctrl+Alt+Shift+D\", \"OpenCard\": \"Ctrl+Alt+Shift+E\" } }";

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
    public void ANewInstallHasTheShortcutOnBesideTheOtherThree()
    {
        HotkeySettings defaults = HotkeySettings.Defaults;

        Assert.AreEqual("Ctrl+Alt+Shift+S", defaults.SendTo);
        Assert.AreEqual("Ctrl+Alt+Shift+S", new EarshotSettings().Hotkeys.SendTo);
        Assert.AreEqual("Ctrl+Alt+Shift+A", defaults.SwitchToPc);
        Assert.AreEqual("Ctrl+Alt+Shift+D", defaults.SwitchToPhone);
        Assert.AreEqual("Ctrl+Alt+Shift+E", defaults.OpenCard);
        Assert.AreEqual(defaults.SendTo, defaults.TextFor(HotkeyAction.SendTo));
    }

    [TestMethod]
    public void AFileWrittenBeforeTheMemberExistedGetsTheDefaultAndKeepsTheOthers()
    {
        HotkeySettings hotkeys = Load(FileWithoutTheMember);

        Assert.AreEqual(HotkeySettings.DefaultSendTo, hotkeys.SendTo);
        Assert.AreEqual("Ctrl+Alt+Shift+A", hotkeys.SwitchToPc);
        Assert.AreEqual("Ctrl+Alt+Shift+E", hotkeys.OpenCard);
        Assert.IsTrue(hotkeys.Enabled);
    }

    [TestMethod]
    public void AClearedShortcutStaysClearedWhenTheFileIsReadAndAfterASaveAndRestart()
    {
        HotkeySettings read = Load("{ \"SchemaVersion\": 1, \"Hotkeys\": { \"Enabled\": true, \"SwitchToPc\": \"Ctrl+Alt+Shift+A\", \"SwitchToPhone\": \"Ctrl+Alt+Shift+D\", \"OpenCard\": \"Ctrl+Alt+Shift+E\", \"SendTo\": \"\" } }");
        Assert.AreEqual(string.Empty, read.SendTo, "A member the file holds, empty, is the owner's.");

        var temp = new TempFolder();
        var log = new CapturingLog();
        string path = temp.File("settings.json");
        File.WriteAllText(path, FileWithoutTheMember);

        var first = new JsonSettingsStore(path, log);
        Assert.AreEqual(HotkeySettings.DefaultSendTo, first.Current.Hotkeys.SendTo);
        first.Update(s => s.Hotkeys.SendTo = string.Empty);

        var second = new JsonSettingsStore(path, log);
        Assert.AreEqual(string.Empty, second.Current.Hotkeys.SendTo, "The default came back after a restart.");

        second.Update(s => s.Hotkeys.SwitchToPc = "Ctrl+Alt+P");
        Assert.AreEqual(string.Empty, new JsonSettingsStore(path, log).Current.Hotkeys.SendTo);
    }

    [TestMethod]
    [DataRow("ToggleConnection")]
    [DataRow("SwitchToPc")]
    [DataRow("OpenCard")]
    public void ATypedChordThatMatchesTheDefaultKeepsItAndTheDefaultStepsAside(string member)
    {
        HotkeySettings hotkeys = Load(
            "{ \"SchemaVersion\": 1, \"Hotkeys\": { \"Enabled\": true, \"SwitchToPhone\": \"Ctrl+Alt+Shift+D\", \"OpenCard\": \"Ctrl+Alt+Shift+E\", \"SwitchToPc\": \"Ctrl+Alt+Shift+A\", \""
            + member + "\": \"Ctrl+Alt+Shift+S\" } }");

        Assert.AreEqual("Ctrl+Alt+Shift+S", hotkeys.TextFor(member switch
        {
            "ToggleConnection" => HotkeyAction.ToggleConnection,
            "SwitchToPc" => HotkeyAction.SwitchToPc,
            _ => HotkeyAction.OpenCard,
        }));
        Assert.AreEqual(string.Empty, hotkeys.SendTo);
    }

    [TestMethod]
    public void TheShippedSerialiserWritesTheMember()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new EarshotSettings(), SettingsJsonContext.Default.EarshotSettings);

        using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual("Ctrl+Alt+Shift+S", parsed.RootElement.GetProperty("Hotkeys").GetProperty("SendTo").GetString());
    }

    [TestMethod]
    public void TheDefaultChordIsRegisteredWithControlAltShiftS()
    {
        var native = new FakeNativeHotkeys();
        using var manager = new HotkeyManager(new FakeMessageWindow(), native, new CapturingLog());

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(HotkeySettings.Defaults);

        NativeCall call = native.Calls.Single(c => c.Id == SendToId);
        Assert.AreEqual("RegisterHotKey", call.Method);
        Assert.AreEqual(0x53u, call.VirtualKey, "S.");
        Assert.AreEqual((uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift) | 0x4000u, call.Modifiers);
        Assert.AreEqual(HotkeyRegistrationState.Registered, outcomes.Single(o => o.Action == HotkeyAction.SendTo).State);
    }

    [TestMethod]
    public void AClearedShortcutIsNotRegistered()
    {
        var native = new FakeNativeHotkeys();
        using var manager = new HotkeyManager(new FakeMessageWindow(), native, new CapturingLog());
        var settings = new HotkeySettings { SendTo = string.Empty };

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(settings);

        Assert.AreEqual(HotkeyRegistrationState.NotSet, outcomes.Single(o => o.Action == HotkeyAction.SendTo).State);
        Assert.IsFalse(native.Calls.Any(c => c.Id == SendToId));
    }

    [TestMethod]
    public void AChordAnotherProgramHoldsIsReportedWithWindowsOwnCode()
    {
        var native = new FakeNativeHotkeys();
        native.SetResult(SendToId, NativeCallResult.Failure(1409));
        using var manager = new HotkeyManager(new FakeMessageWindow(), native, new CapturingLog());

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(HotkeySettings.Defaults);

        HotkeyRegistrationOutcome sendTo = outcomes.Single(o => o.Action == HotkeyAction.SendTo);
        Assert.AreEqual(HotkeyRegistrationState.AlreadyHeld, sendTo.State);
        Assert.AreEqual("Ctrl+Alt+Shift+S is already in use by another program, so it was not set. Windows reported error 1409.", sendTo.Message);
        Assert.AreEqual(HotkeyRegistrationState.Registered, outcomes.Single(o => o.Action == HotkeyAction.OpenCard).State);
    }

    [TestMethod]
    public void ThePressRaisesTheSendToAction()
    {
        var window = new FakeMessageWindow();
        using var manager = new HotkeyManager(window, new FakeNativeHotkeys(), new CapturingLog());
        manager.Apply(HotkeySettings.Defaults);
        var raised = new List<HotkeyAction>();
        manager.Activated += (_, e) => raised.Add(e.Action);

        window.Raise(Earshot.Interop.NativeMethods.WM_HOTKEY, SendToId, 0);

        Assert.AreEqual(1, raised.Count);
        Assert.AreEqual(HotkeyAction.SendTo, raised[0]);
    }

    [TestMethod]
    public void TheBindingModelSetsClearsAndRefusesAClashWithAnotherCommand()
    {
        var settings = HotkeySettings.Defaults;
        var model = new HotkeyBindingModel(settings, () => []);

        Assert.AreEqual("Ctrl+Alt+Shift+S", model.Chord(HotkeyAction.SendTo));
        Assert.IsNull(model.Set(HotkeyAction.SendTo, "Ctrl+Alt+Shift+P"));
        Assert.AreEqual("Ctrl+Alt+Shift+P", settings.SendTo);

        string? clash = model.Set(HotkeyAction.SendTo, "Ctrl+Alt+Shift+E");
        Assert.IsNotNull(clash, "The card shortcut already holds that chord.");
        Assert.AreEqual("Ctrl+Alt+Shift+P", settings.SendTo, "A refusal stores nothing.");

        model.Clear(HotkeyAction.SendTo);
        Assert.AreEqual(string.Empty, settings.SendTo);
        Assert.AreEqual(string.Empty, model.Chord(HotkeyAction.SendTo));
    }
}
