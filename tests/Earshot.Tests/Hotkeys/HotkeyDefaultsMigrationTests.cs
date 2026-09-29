using System.Text.Json;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Hotkeys;

// How a settings file written before the two switch shortcuts existed picks up their defaults, read through the
// real store (which is what start-up reads), never a hand-built object. The rule under test: a member the file
// does not contain takes its default, a member it does contain is the owner's and is never replaced, and "shortcuts
// on" is only inferred for a file that could not have expressed a wish (off, with nothing typed, and neither new
// member present).
[TestClass]
public sealed class HotkeyDefaultsMigrationTests
{
    private const string OldFileTemplate =
        "{ \"SchemaVersion\": 1, \"Hotkeys\": { \"Enabled\": ENABLED, \"ToggleConnection\": \"CONNECT\", \"ToggleAudioProtection\": \"\", \"ToggleBlockAtBoot\": \"\", \"SpeakStatus\": \"\" } }";

    private static HotkeySettings Load(string content, out CapturingLog log)
    {
        var temp = new TempFolder();
        log = new CapturingLog();
        string path = temp.File("settings.json");
        File.WriteAllText(path, content);
        var store = new JsonSettingsStore(path, log);
        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        return store.Current.Hotkeys;
    }

    private static string OldFile(bool enabled, string connect) =>
        OldFileTemplate.Replace("ENABLED", enabled ? "true" : "false", StringComparison.Ordinal).Replace("CONNECT", connect, StringComparison.Ordinal);

    // The file every earlier build wrote to a machine where the owner never touched shortcuts.
    [TestMethod]
    public void AnUntouchedOldFileGetsBothDefaultsAndShortcutsOn()
    {
        HotkeySettings hotkeys = Load(OldFile(enabled: false, connect: ""), out _);

        Assert.IsTrue(hotkeys.Enabled, "Off with nothing typed carries no wish, so the defaults must take effect.");
        Assert.AreEqual(HotkeySettings.DefaultSwitchToPc, hotkeys.SwitchToPc);
        Assert.AreEqual(HotkeySettings.DefaultSwitchToPhone, hotkeys.SwitchToPhone);
    }

    // A chord the owner typed is theirs, on or off.
    [TestMethod]
    public void ATypedChordIsNeverReplacedAndItsOnOffChoiceIsKept()
    {
        HotkeySettings on = Load(OldFile(enabled: true, connect: "Ctrl+Alt+C"), out _);
        Assert.AreEqual("Ctrl+Alt+C", on.ToggleConnection);
        Assert.IsTrue(on.Enabled);
        Assert.AreEqual(HotkeySettings.DefaultSwitchToPc, on.SwitchToPc);

        HotkeySettings off = Load(OldFile(enabled: false, connect: "Ctrl+Alt+C"), out _);
        Assert.AreEqual("Ctrl+Alt+C", off.ToggleConnection);
        Assert.IsFalse(off.Enabled, "An owner who typed a chord and left shortcuts off chose that.");
        Assert.AreEqual(HotkeySettings.DefaultSwitchToPc, off.SwitchToPc, "The defaults are still filled in, ready for when they turn shortcuts on.");
    }

    // Once the file has the members (a save writes every member), an empty text is the owner clearing it and stays
    // cleared, and Enabled false stays false. Without this the default would come back at every start.
    [TestMethod]
    public void AClearedDefaultAndAnOffSwitchSurviveASaveAndReload()
    {
        var temp = new TempFolder();
        var log = new CapturingLog();
        string path = temp.File("settings.json");
        File.WriteAllText(path, OldFile(enabled: false, connect: ""));

        var first = new JsonSettingsStore(path, log);
        Assert.IsTrue(first.Current.Hotkeys.Enabled);
        first.Update(s =>
        {
            s.Hotkeys.SwitchToPc = string.Empty;
            s.Hotkeys.Enabled = false;
        });

        var second = new JsonSettingsStore(path, log);
        Assert.AreEqual(string.Empty, second.Current.Hotkeys.SwitchToPc);
        Assert.AreEqual(HotkeySettings.DefaultSwitchToPhone, second.Current.Hotkeys.SwitchToPhone);
        Assert.IsFalse(second.Current.Hotkeys.Enabled, "The migration ran again on a file that already had the members.");
    }

    [TestMethod]
    public void AFileThatHoldsTheMembersIsTakenExactlyAsWritten()
    {
        const string content =
            "{ \"SchemaVersion\": 1, \"Hotkeys\": { \"Enabled\": false, \"SwitchToPc\": \"\", \"SwitchToPhone\": \"Ctrl+Alt+P\" } }";

        HotkeySettings hotkeys = Load(content, out _);

        Assert.IsFalse(hotkeys.Enabled);
        Assert.AreEqual(string.Empty, hotkeys.SwitchToPc);
        Assert.AreEqual("Ctrl+Alt+P", hotkeys.SwitchToPhone);
    }

    // The owner had typed a chord that is the same as a default. Registering both would show a clash they did not
    // cause, so the new default steps aside and the typed one is kept.
    [TestMethod]
    public void ADefaultThatMatchesATypedChordStepsAside()
    {
        HotkeySettings hotkeys = Load(OldFile(enabled: true, connect: "Ctrl+Alt+Shift+A"), out _);

        Assert.AreEqual("Ctrl+Alt+Shift+A", hotkeys.ToggleConnection);
        Assert.AreEqual(string.Empty, hotkeys.SwitchToPc);
        Assert.AreEqual(HotkeySettings.DefaultSwitchToPhone, hotkeys.SwitchToPhone);

        var window = new FakeMessageWindow();
        var native = new FakeNativeHotkeys();
        using var manager = new HotkeyManager(window, native, new CapturingLog());
        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(hotkeys);
        Assert.IsFalse(outcomes.Any(o => o.State == HotkeyRegistrationState.DuplicateInSettings));
    }

    // Existing behaviour that must survive: a file with no Hotkeys member reads as the defaults, and an explicit null
    // is a corrupt file (the whole file resets), as before.
    [TestMethod]
    public void NoHotkeysMemberReadsAsTheDefaultsAndANullResetsTheFile()
    {
        HotkeySettings none = Load("{ \"SchemaVersion\": 1 }", out _);
        Assert.IsTrue(none.Enabled);
        Assert.AreEqual(HotkeySettings.DefaultSwitchToPc, none.SwitchToPc);

        var temp = new TempFolder();
        string path = temp.File("settings.json");
        File.WriteAllText(path, "{ \"Hotkeys\": null }");
        var store = new JsonSettingsStore(path, new CapturingLog());
        Assert.AreEqual(SettingsLoadStatus.ResetAfterCorruption, store.LastLoadStatus);
        Assert.AreEqual(HotkeySettings.DefaultSwitchToPc, store.Current.Hotkeys.SwitchToPc);
    }

    // The shipped serialiser writes both members, so the docs and the round trip are one thing.
    [TestMethod]
    public void TheShippedSerialiserWritesBothNewMembers()
    {
        string json = JsonSerializer.Serialize(new EarshotSettings(), SettingsJsonContext.Default.EarshotSettings);

        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement hotkeys = parsed.RootElement.GetProperty("Hotkeys");
        Assert.AreEqual("Ctrl+Alt+Shift+A", hotkeys.GetProperty("SwitchToPc").GetString());
        Assert.AreEqual("Ctrl+Alt+Shift+D", hotkeys.GetProperty("SwitchToPhone").GetString());
        Assert.IsTrue(hotkeys.GetProperty("Enabled").GetBoolean());
    }
}
