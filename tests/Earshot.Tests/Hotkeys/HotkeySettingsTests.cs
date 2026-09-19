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
}
