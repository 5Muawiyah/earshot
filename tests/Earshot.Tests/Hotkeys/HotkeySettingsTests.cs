using System.Text.Json;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Hotkeys;

[TestClass]
public sealed class HotkeySettingsTests
{
    [TestMethod]
    public void SettingsTextForThrowsForUnknownAction()
    {
        HotkeySettings settings = HotkeySettings.Defaults;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => settings.TextFor((HotkeyAction)99));
    }

    [TestMethod]
    public void SettingsTextForReturnsEachActionsOwnText()
    {
        var settings = new HotkeySettings
        {
            Enabled = true,
            ToggleConnection = "Ctrl+Alt+C",
            ToggleAudioProtection = "Ctrl+Alt+A",
            ToggleBlockAtBoot = "Ctrl+Alt+B",
            SpeakStatus = "Ctrl+Alt+S",
        };

        Assert.AreEqual("Ctrl+Alt+C", settings.TextFor(HotkeyAction.ToggleConnection));
        Assert.AreEqual("Ctrl+Alt+A", settings.TextFor(HotkeyAction.ToggleAudioProtection));
        Assert.AreEqual("Ctrl+Alt+B", settings.TextFor(HotkeyAction.ToggleBlockAtBoot));
        Assert.AreEqual("Ctrl+Alt+S", settings.TextFor(HotkeyAction.SpeakStatus));
    }

    [TestMethod]
    public void DefaultsAreOffWithEveryTextEmpty()
    {
        HotkeySettings defaults = HotkeySettings.Defaults;
        Assert.IsFalse(defaults.Enabled);
        Assert.AreEqual(string.Empty, defaults.ToggleConnection);
        Assert.AreEqual(string.Empty, defaults.ToggleAudioProtection);
        Assert.AreEqual(string.Empty, defaults.ToggleBlockAtBoot);
        Assert.AreEqual(string.Empty, defaults.SpeakStatus);
    }

    // Adapted from the spec's Settings_RoundTripThroughJson: Earshot's own settings pipeline serialises
    // through Earshot.Infra.SettingsJsonContext (source-generated, reflection-free) as part of the whole
    // EarshotSettings object, with member names as declared (PascalCase), not the spec's standalone
    // camelCase JsonNamingPolicy. This exercises that real pipeline instead of a policy this build does
    // not use.
    [TestMethod]
    public void SettingsRoundTripThroughEarshotSettingsJson()
    {
        var settings = new EarshotSettings
        {
            Hotkeys = new HotkeySettings
            {
                Enabled = true,
                ToggleConnection = "Ctrl+Alt+C",
                ToggleAudioProtection = "Ctrl+Alt+A",
                ToggleBlockAtBoot = "Ctrl+Alt+B",
                SpeakStatus = "Ctrl+Alt+S",
            },
        };

        string json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.EarshotSettings);
        Assert.IsTrue(json.Contains("\"Hotkeys\"", StringComparison.Ordinal));
        Assert.IsTrue(json.Contains("\"Enabled\": true", StringComparison.Ordinal));

        // The member names, not the exact escaping of "+": the default encoder writes it as +,
        // which is still the same character once parsed, so the values are checked by parsing rather
        // than by a raw substring match on the escaped form.
        Assert.IsTrue(json.Contains("\"ToggleConnection\"", StringComparison.Ordinal));
        Assert.IsTrue(json.Contains("\"ToggleAudioProtection\"", StringComparison.Ordinal));
        Assert.IsTrue(json.Contains("\"ToggleBlockAtBoot\"", StringComparison.Ordinal));
        Assert.IsTrue(json.Contains("\"SpeakStatus\"", StringComparison.Ordinal));

        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement hotkeys = parsed.RootElement.GetProperty("Hotkeys");
        Assert.AreEqual("Ctrl+Alt+C", hotkeys.GetProperty("ToggleConnection").GetString());
        Assert.AreEqual("Ctrl+Alt+A", hotkeys.GetProperty("ToggleAudioProtection").GetString());
        Assert.AreEqual("Ctrl+Alt+B", hotkeys.GetProperty("ToggleBlockAtBoot").GetString());
        Assert.AreEqual("Ctrl+Alt+S", hotkeys.GetProperty("SpeakStatus").GetString());

        EarshotSettings? round = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.EarshotSettings);
        Assert.IsNotNull(round);
        Assert.IsTrue(round!.Hotkeys.Enabled);
        Assert.AreEqual("Ctrl+Alt+C", round.Hotkeys.ToggleConnection);
        Assert.AreEqual("Ctrl+Alt+A", round.Hotkeys.ToggleAudioProtection);
        Assert.AreEqual("Ctrl+Alt+B", round.Hotkeys.ToggleBlockAtBoot);
        Assert.AreEqual("Ctrl+Alt+S", round.Hotkeys.SpeakStatus);
    }

    // Settings are additive: an older settings file written before Hotkeys existed has no such member,
    // and it must read as "off, nothing typed" rather than fail to load.
    [TestMethod]
    public void SettingsMissingHotkeysMemberReadsAsDefaults()
    {
        const string json = "{ \"SchemaVersion\": 1, \"DeviceMatch\": \"AirPods\", \"PinnedAddress\": \"\" }";

        EarshotSettings? settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.EarshotSettings);

        Assert.IsNotNull(settings);
        Assert.IsFalse(settings!.Hotkeys.Enabled);
        Assert.AreEqual(string.Empty, settings.Hotkeys.ToggleConnection);
        Assert.AreEqual(string.Empty, settings.Hotkeys.ToggleAudioProtection);
        Assert.AreEqual(string.Empty, settings.Hotkeys.ToggleBlockAtBoot);
        Assert.AreEqual(string.Empty, settings.Hotkeys.SpeakStatus);
    }

    // Commits b3fd102 and 376c09a found that with init-only members, EarshotSettings' source-generated
    // JSON reader builds VoiceOverSettings/StreamingSettings through one constructor call that sets every
    // member at once, so a member a partial block in the file leaves out comes back as default(T) rather
    // than this type's own documented default: a hand-written block holding only Enabled read every other
    // member as its CLR default. Both were fixed by giving the members setters (the reader then builds
    // the object first, through the parameterless constructor and its field initialisers, and sets only
    // what the file holds). HotkeySettings already has setters (see the class header), so this is the
    // same defect class checked against the real pipeline rather than assumed fixed by inspection: through
    // the real JsonSettingsStore, not just JsonSerializer.Deserialize directly, because Update, Reload and
    // the store's own validation are also built on the same source-generated context and a defect in the
    // generated reader would otherwise only show up the moment something tried to use the loaded value.
    [TestMethod]
    public void PartialHotkeysBlockLeavesEveryOtherShortcutEmptyNotNull()
    {
        using var temp = new TempFolder();
        var log = new CapturingLog();
        string path = temp.File("settings.json");
        File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"Hotkeys\": { \"Enabled\": true } }");

        var store = new JsonSettingsStore(path, log);
        HotkeySettings hotkeys = store.Current.Hotkeys;

        Assert.IsTrue(hotkeys.Enabled, "The member the file did hold must still read back as written.");
        Assert.AreEqual(string.Empty, hotkeys.ToggleConnection);
        Assert.AreEqual(string.Empty, hotkeys.ToggleAudioProtection);
        Assert.AreEqual(string.Empty, hotkeys.ToggleBlockAtBoot);
        Assert.AreEqual(string.Empty, hotkeys.SpeakStatus);

        // Not just "empty is what TextFor happens to return for a null": Apply parses every shortcut text
        // through HotkeyText.TryParse (App\TrayContext.cs's ApplyHotkeys calls this on real settings load),
        // which is where a null (rather than an empty string) would actually throw. Whether HotkeySettings
        // gives Apply a null or an empty string is exactly what this test has to prove, not assume.
        var window = new FakeMessageWindow();
        var native = new FakeNativeHotkeys();
        using var manager = new HotkeyManager(window, native, log);

        IReadOnlyList<HotkeyRegistrationOutcome> outcomes = manager.Apply(hotkeys);

        Assert.AreEqual(Enum.GetValues<HotkeyAction>().Length, outcomes.Count);
    }
}
