using System.Text.Json.Nodes;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What older builds wrote still loads, and what an older build left on disk (the claim, the proof and the battery
// set-up records) is neither read, written nor deleted by this one.
[TestClass]
public sealed class SettingsMigrationTests : IDisposable
{
    private TempFolder _temp = null!;
    private CapturingLog _log = null!;

    [TestInitialize]
    public void Setup()
    {
        _temp = new TempFolder();
        _log = new CapturingLog();
    }

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("settings.json");

    // A settings file as the previous release wrote it: every current member present, with invented values.
    [TestMethod]
    public void ASettingsFileFromTheLastReleaseLoadsWithItsOwnValues()
    {
        new JsonSettingsStore(SettingsPath, _log).Update(s => s.Widget = s.Widget with { Enabled = true });
        JsonNode root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        root["DeviceMatch"] = "Invented buds";
        root["ProtectAudioQuality"] = false;
        root["HandBackOnShutdownAndSleep"] = true;
        root["CheckForUpdatesAutomatically"] = true;
        root["PauseWhenAirPodsLeave"] = false;
        JsonObject widget = root["Widget"]!.AsObject();
        widget["Enabled"] = false;
        widget["ShowOnTaskbar"] = false;
        widget["OtherDeviceLabel"] = "Tablet";
        widget["AutoPause"] = false;
        widget["LowBatteryAlert"] = false;
        widget["LowBatteryThresholdPercent"] = 40;
        widget["CaseOpenCard"] = false;
        widget["LeftClickConnects"] = true;

        // The members v1.4 added were not in the last release's file.
        widget.Remove("CaseOpenCardOn");
        widget.Remove("CaseOpenCardCloseSeconds");
        widget.Remove("CaseOpenCardDisplays");
        File.WriteAllText(SettingsPath, root.ToJsonString());

        var store = new JsonSettingsStore(SettingsPath, _log);

        EarshotSettings loaded = store.Current;
        Assert.AreEqual("Invented buds", loaded.DeviceMatch);
        Assert.IsFalse(loaded.ProtectAudioQuality);
        Assert.IsTrue(loaded.HandBackOnShutdownAndSleep);
        Assert.IsTrue(loaded.CheckForUpdatesAutomatically);
        Assert.IsFalse(loaded.PauseWhenAirPodsLeave);
        Assert.IsTrue(loaded.Widget.Enabled, "Every older consumer is off, but the case-open card is on, so the watcher runs (see the next test).");
        Assert.IsFalse(loaded.Widget.ShowOnTaskbar);
        Assert.AreEqual("Tablet", loaded.Widget.OtherDeviceLabel);
        Assert.IsFalse(loaded.Widget.AutoPause);
        Assert.IsFalse(loaded.Widget.LowBatteryAlert);
        Assert.AreEqual(40, loaded.Widget.LowBatteryThresholdPercent);
        Assert.IsFalse(loaded.Widget.CaseOpenCard, "A member the widget no longer reads is still kept, as the file wrote it.");
        Assert.IsTrue(loaded.Widget.CaseOpenCardOn, "The file has no CaseOpenCardOn, so the case-open card is on.");
        Assert.IsTrue(loaded.Widget.LeftClickConnects);
    }

    // The first release's file held only these members; every member added since reads as its default.
    [TestMethod]
    public void ASettingsFileFromTheFirstReleaseGetsTheDefaultsForTheRest()
    {
        File.WriteAllText(SettingsPath,
            """{"SchemaVersion":1,"DeviceMatch":"AirPods","ProtectAudioQuality":false,"ProtectAudioNoticeShown":true,"OpenOnStartup":false,"PinnedAddress":""}""");

        var store = new JsonSettingsStore(SettingsPath, _log);

        EarshotSettings loaded = store.Current;
        Assert.IsFalse(loaded.ProtectAudioQuality, "What the file said stands.");
        Assert.IsTrue(loaded.ProtectAudioNoticeShown);
        Assert.IsFalse(loaded.OpenOnStartup);
        WidgetSettings defaults = WidgetSettings.Default;
        Assert.AreEqual(defaults, loaded.Widget, "No Widget member: the widget's own defaults.");
        Assert.AreEqual(defaults.LowBatteryThresholdPercent, loaded.Widget.LowBatteryThresholdPercent);
        Assert.IsTrue(loaded.PauseWhenAirPodsLeave);
        Assert.IsFalse(loaded.CheckForUpdatesAutomatically);
    }

    // Up to v1.3 the case-open card could never show and its switch was hidden, so an old file's CaseOpenCard, on or off, was
    // never the owner's choice of this card: a file without the v1.4 member has the card on (the owner's decision for
    // existing installs), with its close and display choices at their defaults. The old member is kept as written.
    [TestMethod]
    public void AnOlderFileHasTheCaseOpenCardOnWhateverItsOldMemberSaid()
    {
        foreach (bool old in new[] { false, true })
        {
            new JsonSettingsStore(SettingsPath, _log).Update(s => s.Widget = s.Widget with { Enabled = true });
            JsonNode root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
            JsonObject widget = root["Widget"]!.AsObject();
            widget.Remove("CaseOpenCardOn");
            widget.Remove("CaseOpenCardCloseSeconds");
            widget.Remove("CaseOpenCardDisplays");
            widget["CaseOpenCard"] = old;
            File.WriteAllText(SettingsPath, root.ToJsonString());

            WidgetSettings loaded = new JsonSettingsStore(SettingsPath, _log).Current.Widget;

            Assert.IsTrue(loaded.CaseOpenCardOn, "On, with the old member " + old + ".");
            Assert.AreEqual(old, loaded.CaseOpenCard, "The old member is kept as the file wrote it.");
            Assert.AreEqual(CaseOpenCardClose.UntilCaseCloses, loaded.CaseOpenCardCloseSeconds);
            Assert.IsEmpty(loaded.CaseOpenCardDisplays);
        }
    }

    // The owner's decision: the case-open card is on for an existing install and the passive watcher runs whenever Earshot
    // runs. An old file with every consumer off saved Enabled=false; loading it must recompute Enabled from the consumers,
    // or the card is on in the setting but inert (nothing feeds it) until some unrelated toggle rewrites Enabled.
    [TestMethod]
    public void AnOldFileWithEveryConsumerOffLoadsWithTheWatcherOnForTheCaseOpenCard()
    {
        new JsonSettingsStore(SettingsPath, _log).Update(s => s.Widget = s.Widget with { Enabled = true });
        JsonNode root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        JsonObject widget = root["Widget"]!.AsObject();
        widget["Enabled"] = false;
        widget["ShowOnTaskbar"] = false;
        widget["AutoPause"] = false;
        widget["LowBatteryAlert"] = false;
        widget.Remove("CaseOpenCardOn");
        File.WriteAllText(SettingsPath, root.ToJsonString());

        WidgetSettings loaded = new JsonSettingsStore(SettingsPath, _log).Current.Widget;

        Assert.IsTrue(loaded.CaseOpenCardOn);
        Assert.IsTrue(loaded.Enabled, "Enabled reflects CaseOpenCardOn at load.");
    }

    // With the card and the fully charged notice chosen off as well, nothing needs the watcher and Enabled stays false.
    [TestMethod]
    public void AFileWithEveryConsumerOffIncludingTheCaseOpenCardKeepsTheWatcherOff()
    {
        new JsonSettingsStore(SettingsPath, _log).Update(s => s.Widget = s.Widget with { Enabled = true });
        JsonNode root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        JsonObject widget = root["Widget"]!.AsObject();
        widget["Enabled"] = false;
        widget["ShowOnTaskbar"] = false;
        widget["AutoPause"] = false;
        widget["LowBatteryAlert"] = false;
        widget["CaseOpenCardOn"] = false;
        widget["FullyChargedNotice"] = false;
        File.WriteAllText(SettingsPath, root.ToJsonString());

        Assert.IsFalse(new JsonSettingsStore(SettingsPath, _log).Current.Widget.Enabled);
    }

    // Once v1.4 has saved the owner's own choice, it stands: off stays off, and the close and display choices load as saved.
    [TestMethod]
    public void TheCaseOpenCardAsSavedByThisBuildLoadsAsSaved()
    {
        string id = @"\\?\DISPLAY#AAA0001#5&1a2b3c4d&0&UID100#{monitor-interface}";
        new JsonSettingsStore(SettingsPath, _log).Update(s => s.Widget = s.Widget with
        {
            CaseOpenCardOn = false, CaseOpenCardCloseSeconds = 30, CaseOpenCardDisplays = [id],
        });

        WidgetSettings loaded = new JsonSettingsStore(SettingsPath, _log).Current.Widget;

        Assert.IsFalse(loaded.CaseOpenCardOn);
        Assert.AreEqual(30, loaded.CaseOpenCardCloseSeconds);
        CollectionAssert.AreEqual(new[] { id }, loaded.CaseOpenCardDisplays);
    }

    // A member this build never heard of (an earlier build's, or a later one's) is skipped, not an error.
    [TestMethod]
    public void AMemberNoBuildReadsAnyMoreIsSkipped()
    {
        new JsonSettingsStore(SettingsPath, _log).Update(s => s.Widget = s.Widget with { Enabled = true });
        JsonNode root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        root["SomethingFromAnotherBuild"] = "x";
        root["Widget"]!.AsObject()["BatterySetupSomethingOld"] = JsonNode.Parse("""{"left":100}""");
        File.WriteAllText(SettingsPath, root.ToJsonString());

        var store = new JsonSettingsStore(SettingsPath, _log);

        Assert.IsTrue(store.Current.Widget.Enabled);
        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
    }

    // The owner's claim, proof and set-up records stay exactly where they are: the widget reads none of them,
    // writes none of them and deletes none of them, however many messages it hears.
    [TestMethod]
    public void TheOldClaimProofAndSetupRecordsAreNeverReadWrittenOrDeleted()
    {
        using var dataRoot = new EnvironmentVariableScope("EARSHOT_DATA_ROOT", _temp.Path);
        Paths paths = Paths.Current;
        string setupFolder = Path.Combine(paths.WidgetFolder, "setup");
        Directory.CreateDirectory(setupFolder);
        string claim = paths.WidgetClaimFile;
        string proof = Path.Combine(paths.WidgetFolder, "proof.json");
        string record = Path.Combine(setupFolder, "setup-invented.json");
        File.WriteAllText(claim, "{ \"claim\": \"invented\" }");
        File.WriteAllText(proof, "{ \"proof\": \"invented\" }");
        File.WriteAllText(record, "{ \"record\": \"invented\" }");
        DateTime claimStamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(claim, claimStamp);
        File.SetLastWriteTimeUtc(proof, claimStamp);
        File.SetLastWriteTimeUtc(record, claimStamp);
        string[] before = Directory.GetFiles(paths.WidgetFolder, "*", SearchOption.AllDirectories).Order().ToArray();

        var settings = new JsonSettingsStore(SettingsPath, _log);
        settings.Update(s => s.Widget = s.Widget with { Enabled = true });
        var registry = new ServiceRegistry(_log, settings, action => action(), safeMode: true);
        var source = new FakeAdvertisementSource();
        WidgetStatusService? service = CompositionRoot.BuildWidget(registry, () => null, TimeProvider.System, () => source, new FakePairedModelSource());
        Assert.IsNotNull(service);
        service.Start();
        for (int i = 0; i < 5; i++)
        {
            source.Raise(new AdvertisementSample(
                ProximityParser.AppleCompanyId, WidgetFixtures.Proximity(batteryA: 0x56, batteryB: 0x0A), -50, DateTimeOffset.UtcNow, 1));
        }

        service.Close();

        string[] after = Directory.GetFiles(paths.WidgetFolder, "*", SearchOption.AllDirectories).Order().ToArray();
        CollectionAssert.AreEqual(before, after, "Nothing was added or removed.");
        Assert.AreEqual("{ \"claim\": \"invented\" }", File.ReadAllText(claim));
        Assert.AreEqual("{ \"proof\": \"invented\" }", File.ReadAllText(proof));
        Assert.AreEqual("{ \"record\": \"invented\" }", File.ReadAllText(record));
        Assert.AreEqual(claimStamp, File.GetLastWriteTimeUtc(claim), "Not even touched.");
        Assert.AreEqual(claimStamp, File.GetLastWriteTimeUtc(proof));
        Assert.AreEqual(claimStamp, File.GetLastWriteTimeUtc(record));
    }
}
