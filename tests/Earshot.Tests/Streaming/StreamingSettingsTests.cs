using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Streaming;

// The Streaming setting, against the real JsonSettingsStore and its source-generated context: the defaults, the
// limits, what an older file reads as, and the exact member names the shipped build writes.
[TestClass]
public sealed class StreamingSettingsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("settings.json");

    [TestMethod]
    public void TheFeatureIsOffByDefault()
    {
        Assert.IsFalse(new EarshotSettings().Streaming.Enabled);
        Assert.IsFalse(StreamingSettings.Default.Enabled);
        Assert.AreEqual("", StreamingSettings.Default.LastDeviceKey);
        Assert.AreEqual(20, StreamingSettings.Default.OpenTimeoutSeconds);
        Assert.AreEqual(5, StreamingSettings.Default.DiscoveryTimeoutSeconds);
    }

    [TestMethod]
    public void AnOlderSettingsFileWithNoStreamingMemberLoadsAsOff()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"DeviceMatch\": \"Beats\" }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.AreEqual(StreamingSettings.Default, store.Current.Streaming);
        Assert.IsFalse(store.Current.Streaming.Enabled);
    }

    // Acceptance test 15. A limit outside its range is replaced by the default, and the replacement is recorded.
    [TestMethod]
    [DataRow(0)]
    [DataRow(4)]
    [DataRow(121)]
    [DataRow(9999)]
    [DataRow(-20)]
    public void SettingsClampAndDefault(int written)
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"Streaming\": { \"Enabled\": true, \"OpenTimeoutSeconds\": " + written + " } }");
        var store = new JsonSettingsStore(SettingsPath, _log);
        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.AreEqual(written, store.Current.Streaming.OpenTimeoutSeconds, "The file is read as written; the limit is applied where it is used.");

        StreamingSettings clamped = store.Current.Streaming.Clamped(out IReadOnlyList<StepOutcome> notes);

        Assert.AreEqual(20, clamped.OpenTimeoutSeconds);
        Assert.IsTrue(clamped.Enabled, "Replacing a limit changes nothing else.");
        Assert.AreEqual(1, notes.Count, "The replacement is recorded, once.");
        Assert.AreEqual("clamp:OpenTimeoutSeconds", notes[0].Step);
        StringAssert.Contains(notes[0].Detail, written.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    [DataRow(5)]
    [DataRow(45)]
    [DataRow(120)]
    public void ALimitInsideItsRangeSurvivesAndNothingIsRecorded(int written)
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"Streaming\": { \"OpenTimeoutSeconds\": " + written + " } }");
        var store = new JsonSettingsStore(SettingsPath, _log);

        StreamingSettings clamped = store.Current.Streaming.Clamped(out IReadOnlyList<StepOutcome> notes);

        Assert.AreEqual(written, clamped.OpenTimeoutSeconds);
        Assert.AreEqual(0, notes.Count);
    }

    [TestMethod]
    public void AMissingLimitReadsAsTheDefault()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"Streaming\": { \"Enabled\": true } }");
        var store = new JsonSettingsStore(SettingsPath, _log);

        StreamingSettings clamped = store.Current.Streaming.Clamped(out IReadOnlyList<StepOutcome> notes);

        Assert.AreEqual(20, clamped.OpenTimeoutSeconds);
        Assert.AreEqual(5, clamped.DiscoveryTimeoutSeconds);
        Assert.AreEqual(0, notes.Count);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(31)]
    public void TheDiscoveryLimitHasItsOwnRange(int written)
    {
        StreamingSettings clamped = (StreamingSettings.Default with { DiscoveryTimeoutSeconds = written }).Clamped(out IReadOnlyList<StepOutcome> notes);

        Assert.AreEqual(5, clamped.DiscoveryTimeoutSeconds);
        Assert.AreEqual("clamp:DiscoveryTimeoutSeconds", notes.Single().Step);
    }

    // The store has no per-member recovery, for this member or any other: a value of the wrong JSON type is a
    // JsonException while the file is read, so the whole file is reset to defaults and that reset is what is recorded.
    // Loading never throws, and the limit still ends up as the default. The same uniform rule the Hotkeys and
    // VoiceOver members follow (Infra\JsonSettingsStoreTests, Voice\VoiceOverSettingsLoadTests).
    [TestMethod]
    [DataRow("{ \"SchemaVersion\": 1, \"Streaming\": { \"OpenTimeoutSeconds\": \"soon\" } }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Streaming\": { \"Enabled\": \"yes\" } }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Streaming\": null }")]
    [DataRow("{ \"SchemaVersion\": 1, \"Streaming\": { \"LastDeviceKey\": null } }")]
    public void AMemberOfTheWrongTypeResetsTheFileAndNeverThrows(string content)
    {
        File.WriteAllText(SettingsPath, content);

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(SettingsLoadStatus.ResetAfterCorruption, store.LastLoadStatus);
        Assert.AreEqual(StreamingSettings.Default, store.Current.Streaming);
        Assert.AreEqual(20, store.Current.Streaming.OpenTimeoutSeconds);
        Assert.IsFalse(store.Current.Streaming.Enabled, "A file that could not be read must never switch the feature on.");
    }

    // Pins the exact member names the shipped build writes, through the real store, not a hand-typed sample. The
    // workshop design's own JSON sample is camelCase, which this build reads as unknown members and ignores.
    [TestMethod]
    public void TheWrittenJsonUsesThesePascalCaseStreamingMemberNames()
    {
        var store = new JsonSettingsStore(SettingsPath, _log);
        store.Update(s => s.Streaming = s.Streaming with { Enabled = true, LastDeviceKey = "0123456789AB" });

        string json = File.ReadAllText(SettingsPath);

        foreach (string member in Sequence.Of("\"Streaming\"", "\"Enabled\"", "\"LastDeviceKey\"", "\"OpenTimeoutSeconds\"", "\"DiscoveryTimeoutSeconds\""))
        {
            Assert.IsTrue(json.Contains(member, StringComparison.Ordinal), member + " is not in the JSON the shipped build wrote: " + json);
        }

        foreach (string member in Sequence.Of("\"streaming\"", "\"lastDeviceId\"", "\"LastDeviceId\"", "\"openTimeoutSeconds\"", "\"askBeforeStopping\"", "\"AskBeforeStopping\""))
        {
            Assert.IsFalse(json.Contains(member, StringComparison.Ordinal), member + " must not be written: " + json);
        }

        var reread = new JsonSettingsStore(SettingsPath, _log);
        Assert.IsTrue(reread.Current.Streaming.Enabled);
        Assert.AreEqual("0123456789AB", reread.Current.Streaming.LastDeviceKey);
    }

    // A camelCase block, as in the workshop sample, is unknown members to this build: ignored, not an error, and
    // above all not a way to switch the feature on by accident.
    [TestMethod]
    public void ACamelCaseBlockIsIgnoredAndLeavesTheFeatureOff()
    {
        File.WriteAllText(SettingsPath, "{ \"SchemaVersion\": 1, \"streaming\": { \"enabled\": true, \"openTimeoutSeconds\": 45 } }");

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.AreEqual(StreamingSettings.Default, store.Current.Streaming);
    }

    // Nothing about a device, a menu or an outcome is ever a setting: only these four members exist.
    [TestMethod]
    public void OnlyTheFourMembersArePersisted()
    {
        string[] members = typeof(StreamingSettings).GetProperties()
            .Where(p => p.GetMethod is { IsStatic: false } && p.Name != "EqualityContract")
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Sequence.Of("DiscoveryTimeoutSeconds", "Enabled", "LastDeviceKey", "OpenTimeoutSeconds"), members);
    }
}
