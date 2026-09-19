using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests;
using Earshot.Voice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Voice;

// Settings loading, against the real JsonSettingsStore, the same store TrayContext reads from
// (App\TrayContext.cs). Two of the task's required acceptance points: an older settings file with no
// voiceOver member loads as defaults, and an explicit null resets per the store's existing policy
// (tests\Earshot.Tests\Infra\JsonSettingsStoreTests.cs already proves and documents that policy for
// Hotkeys; this proves VoiceOver follows the same uniform rule rather than being a special case).
[TestClass]
public sealed class VoiceOverSettingsLoadTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("settings.json");

    [TestMethod]
    public void AnOlderSettingsFileWithNoVoiceOverMemberLoadsAsDefaults()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"DeviceMatch\": \"Beats\" }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.AreEqual(VoiceOverSettings.Default, store.Current.VoiceOver);
        Assert.IsFalse(store.Current.VoiceOver.Enabled);
    }

    [TestMethod]
    [DataRow("{ \"VoiceOver\": null }")]
    [DataRow("{ \"VoiceOver\": { \"Enabled\": null } }")]
    public void VoiceOverNullResetsTheWholeFileLikeEveryOtherNonNullableMember(string content)
    {
        File.WriteAllText(SettingsPath, content);

        var store = new JsonSettingsStore(SettingsPath, _log);

        // Same policy as Hotkeys (Infra\JsonSettingsStoreTests.cs, HotkeysNullNeverThrowsAtStartup): the
        // store has no per-member recovery, so RespectNullableAnnotations turns an explicit null for this
        // non-nullable member into a JsonException during TryRead, and the whole file resets to defaults
        // rather than throwing at start-up.
        Assert.AreEqual(SettingsLoadStatus.ResetAfterCorruption, store.LastLoadStatus);
        Assert.IsNotNull(store.Current.VoiceOver);
        Assert.AreEqual(VoiceOverSettings.Default, store.Current.VoiceOver);
    }

    // Pins the exact member names the shipped build writes, through the real store (JsonSettingsStore,
    // backed by the source-generated SettingsJsonContext), not a hand-typed sample: the workshop SPEC's
    // own JSON sample (section 6) is camelCase, and a camelCase block is silently ignored on read as
    // unknown members (RespectNullableAnnotations only resets the file for an explicit null on a
    // non-nullable member, never for a member name in the wrong case), so documentation that copied the
    // spec's sample would be wrong for this product.
    [TestMethod]
    public void TheWrittenJsonUsesThesePascalCaseVoiceOverMemberNames()
    {
        var store = new JsonSettingsStore(SettingsPath, _log);
        store.Update(s => s.VoiceOver = s.VoiceOver with { VoiceName = "Zira" });

        string json = File.ReadAllText(SettingsPath);

        foreach (string member in new[]
        {
            "\"VoiceOver\"",
            "\"Enabled\"",
            "\"SpeakFailures\"",
            "\"VoiceName\"",
            "\"Rate\"",
            "\"Volume\"",
            "\"RepeatGapMilliseconds\"",
            "\"ShutdownWaitMilliseconds\"",
            "\"FailuresBeforeGivingUp\"",
        })
        {
            Assert.IsTrue(json.Contains(member, StringComparison.Ordinal),
                member + " is not present in the JSON the shipped build wrote: " + json);
        }

        // The exact camelCase spelling the workshop SPEC's own sample uses, so this fails loudly if this
        // product ever regresses to matching that sample instead of the shipped, PascalCase store.
        Assert.IsFalse(json.Contains("\"voiceOver\"", StringComparison.Ordinal),
            "voiceOver (camelCase) must not be the property name written; the shipped store reads and writes PascalCase.");
    }
}
