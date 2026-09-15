using System.Text;
using System.Text.RegularExpressions;
using Earshot.Contracts;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Infra;

[TestClass]
public sealed class JsonSettingsStoreTests : IDisposable
{
    private const string ValidJson = "{ \"SchemaVersion\": 1, \"DeviceMatch\": \"Beats\", \"ProtectAudioQuality\": false, \"PinnedAddress\": \"5A6B7C8D9EAF\" }";

    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string SettingsPath => _temp.File("settings.json");

    private JsonSettingsStore Open() => new(SettingsPath, _log);

    private string[] Quarantined() =>
        Directory.GetFiles(_temp.Path, "settings.corrupt-*.json");

    private static void AssertDefaults(EarshotSettings s)
    {
        var d = new EarshotSettings();
        Assert.AreEqual(d.SchemaVersion, s.SchemaVersion);
        Assert.AreEqual(d.DeviceMatch, s.DeviceMatch);
        Assert.AreEqual(d.ProtectAudioQuality, s.ProtectAudioQuality);
        Assert.AreEqual(d.ProtectAudioNoticeShown, s.ProtectAudioNoticeShown);
        Assert.AreEqual(d.OpenOnStartup, s.OpenOnStartup);
        Assert.AreEqual(d.PinnedContainerId, s.PinnedContainerId);
        Assert.AreEqual(d.PinnedAddress, s.PinnedAddress);
    }

    [TestMethod]
    public void MissingFileGivesDefaultsAndSavesThem()
    {
        JsonSettingsStore store = Open();

        Assert.AreEqual(SettingsLoadStatus.CreatedDefaults, store.LastLoadStatus);
        AssertDefaults(store.Current);
        Assert.IsTrue(File.Exists(SettingsPath));
        AssertDefaults(Open().Current);
    }

    [TestMethod]
    public void MissingFolderIsCreated()
    {
        string nested = Path.Combine(_temp.Path, "a", "b", "settings.json");

        var store = new JsonSettingsStore(nested, _log);

        Assert.AreEqual(SettingsLoadStatus.CreatedDefaults, store.LastLoadStatus);
        Assert.IsTrue(File.Exists(nested));
    }

    [TestMethod]
    public void FirstSaveMovesTheTempFileAndLeavesNoBackup()
    {
        JsonSettingsStore store = Open();

        Assert.IsTrue(File.Exists(store.FilePath));
        Assert.IsFalse(File.Exists(store.TempPath));
        Assert.IsFalse(File.Exists(store.BackupPath));
    }

    [TestMethod]
    public void LaterSavesReplaceAtomicallyAndKeepTheOldFileAsBackup()
    {
        JsonSettingsStore store = Open();
        string before = File.ReadAllText(store.FilePath);

        store.Update(s => s.DeviceMatch = "Pods");

        Assert.IsTrue(File.Exists(store.BackupPath));
        Assert.AreEqual(before, File.ReadAllText(store.BackupPath));
        Assert.IsFalse(File.Exists(store.TempPath));
        Assert.IsTrue(File.ReadAllText(store.FilePath).Contains("\"Pods\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UpdateRoundTripsEveryField()
    {
        var container = new Guid("1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D");
        Open().Update(s =>
        {
            s.DeviceMatch = "Owner\u2019s AirPods";
            s.ProtectAudioQuality = false;
            s.ProtectAudioNoticeShown = true;
            s.OpenOnStartup = false;
            s.PinnedContainerId = container;
            s.PinnedAddress = "5A6B7C8D9EAF";
        });

        JsonSettingsStore reopened = Open();
        EarshotSettings s = reopened.Current;

        Assert.AreEqual(SettingsLoadStatus.Loaded, reopened.LastLoadStatus);
        Assert.AreEqual("Owner\u2019s AirPods", s.DeviceMatch);
        Assert.IsFalse(s.ProtectAudioQuality);
        Assert.IsTrue(s.ProtectAudioNoticeShown);
        Assert.IsFalse(s.OpenOnStartup);
        Assert.AreEqual(container, s.PinnedContainerId);
        Assert.AreEqual("5A6B7C8D9EAF", s.PinnedAddress);
    }

    [TestMethod]
    public void UpdateRaisesChangedOnceWithTheNewValues()
    {
        JsonSettingsStore store = Open();
        var seen = new List<EarshotSettings>();
        store.Changed += (sender, s) =>
        {
            Assert.AreSame(store, sender);
            seen.Add(s);
        };

        store.Update(s => s.OpenOnStartup = false);

        Assert.HasCount(1, seen);
        Assert.IsFalse(seen[0].OpenOnStartup);
        Assert.IsFalse(store.Current.OpenOnStartup);
    }

    [TestMethod]
    public void CurrentAndChangedArgumentsAreCopies()
    {
        JsonSettingsStore store = Open();
        EarshotSettings? published = null;
        store.Changed += (_, s) => published = s;

        store.Current.DeviceMatch = "Changed without Update";
        store.Update(s => s.OpenOnStartup = false);
        published!.DeviceMatch = "Changed by a handler";

        Assert.AreEqual("AirPods", store.Current.DeviceMatch);
        Assert.AreEqual("AirPods", Open().Current.DeviceMatch);
    }

    [TestMethod]
    public void UpdateThatThrowsChangesNothing()
    {
        JsonSettingsStore store = Open();
        string before = File.ReadAllText(store.FilePath);
        int changed = 0;
        store.Changed += (_, _) => changed++;

        Assert.ThrowsExactly<InvalidOperationException>(() => store.Update(s =>
        {
            s.DeviceMatch = "Half done";
            throw new InvalidOperationException("mutate failed");
        }));

        Assert.AreEqual("AirPods", store.Current.DeviceMatch);
        Assert.AreEqual(before, File.ReadAllText(store.FilePath));
        Assert.AreEqual(0, changed);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void UpdateRejectsABlankMatchString(string match)
    {
        JsonSettingsStore store = Open();

        Assert.ThrowsExactly<ArgumentException>(() => store.Update(s => s.DeviceMatch = match));

        Assert.AreEqual("AirPods", Open().Current.DeviceMatch);
    }

    [TestMethod]
    public void UnknownMembersAreIgnored()
    {
        File.WriteAllText(SettingsPath, "{ \"DeviceMatch\": \"Beats\", \"Unknown\": 5, \"Nested\": { \"x\": [1, 2] } }");

        JsonSettingsStore store = Open();

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.AreEqual("Beats", store.Current.DeviceMatch);
        Assert.IsTrue(store.Current.ProtectAudioQuality, "A missing member keeps its default.");
        Assert.IsEmpty(Quarantined());
    }

    [TestMethod]
    public void ByteOrderMarkIsAccepted()
    {
        File.WriteAllText(SettingsPath, ValidJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        JsonSettingsStore store = Open();

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.AreEqual("Beats", store.Current.DeviceMatch);
    }

    [TestMethod]
    public void TruncatedFileFallsBackToTheBackup()
    {
        JsonSettingsStore first = Open();
        first.Update(s => s.DeviceMatch = "Backup value");
        first.Update(s => s.DeviceMatch = "Latest value");
        string truncated = File.ReadAllText(SettingsPath)[..20];
        File.WriteAllText(SettingsPath, truncated);

        JsonSettingsStore store = Open();

        Assert.AreEqual(SettingsLoadStatus.RestoredFromBackup, store.LastLoadStatus);
        Assert.AreEqual("Backup value", store.Current.DeviceMatch);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "Restored the previous copy"));

        // The bad file is kept aside, the main file is valid again and the good backup survives.
        string[] quarantined = Quarantined();
        Assert.HasCount(1, quarantined);
        Assert.AreEqual(truncated, File.ReadAllText(quarantined[0]));
        Assert.AreEqual(quarantined[0], store.QuarantinedFile);
        Assert.AreEqual("Backup value", Open().Current.DeviceMatch);
        Assert.IsTrue(File.ReadAllText(store.BackupPath).Contains("Backup value", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EmptyFileWithoutBackupIsQuarantinedAndReset()
    {
        File.WriteAllText(SettingsPath, "");

        JsonSettingsStore store = Open();

        Assert.AreEqual(SettingsLoadStatus.ResetAfterCorruption, store.LastLoadStatus);
        AssertDefaults(store.Current);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "have been reset to defaults"));

        string[] quarantined = Quarantined();
        Assert.HasCount(1, quarantined);
        Assert.AreEqual(0L, new FileInfo(quarantined[0]).Length);
        Assert.MatchesRegex(new Regex(@"settings\.corrupt-\d{14}\.json\z"), quarantined[0]);
        Assert.AreEqual(SettingsLoadStatus.Loaded, Open().LastLoadStatus);
    }

    [TestMethod]
    public void TruncatedFileAndCorruptBackupReset()
    {
        File.WriteAllText(SettingsPath, "{ \"DeviceMatch\": \"Bea");
        File.WriteAllText(SettingsPath + ".bak", "not json");

        JsonSettingsStore store = Open();

        Assert.AreEqual(SettingsLoadStatus.ResetAfterCorruption, store.LastLoadStatus);
        AssertDefaults(store.Current);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "backup also unusable"));
        Assert.HasCount(1, Quarantined());
    }

    [TestMethod]
    [DataRow("{ \"DeviceMatch\": null }")]
    [DataRow("{ \"PinnedAddress\": null }")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{ \"SchemaVersion\": 0 }")]
    [DataRow("{ \"SchemaVersion\": -1 }")]
    [DataRow("{ \"PinnedAddress\": \"5A6b7C8d9Eaf\" }")]
    [DataRow("{ \"PinnedAddress\": \"300E431D048\" }")]
    [DataRow("{ \"PinnedAddress\": \"5A6B:7C8D:9EAF\" }")]
    [DataRow("{ \"PinnedAddress\": \" \" }")]
    [DataRow("{ \"PinnedContainerId\": \"00000000-0000-0000-ffff-ffffffffffff\" }")]
    [DataRow("{ \"SchemaVersion\": \"1\" }")]
    [DataRow("{ \"DeviceMatch\": \"   \" }")]
    [DataRow("{ \"DeviceMatch\": \"Beats\" } trailing")]
    public void UnusableContentIsResetAndKept(string content)
    {
        File.WriteAllText(SettingsPath, content);

        JsonSettingsStore store = Open();

        Assert.AreEqual(SettingsLoadStatus.ResetAfterCorruption, store.LastLoadStatus);
        AssertDefaults(store.Current);
        string[] quarantined = Quarantined();
        Assert.HasCount(1, quarantined);
        Assert.AreEqual(content, File.ReadAllText(quarantined[0]));
    }

    [TestMethod]
    public void WellFormedPinsLoad()
    {
        File.WriteAllText(SettingsPath, "{ \"PinnedAddress\": \"5A6B7C8D9EAF\", \"PinnedContainerId\": \"1a2b3c4d-5e6f-5a7b-8c9d-0e1f2a3b4c5d\" }");

        JsonSettingsStore store = Open();

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.AreEqual("5A6B7C8D9EAF", store.Current.PinnedAddress);
        Assert.AreEqual(new Guid("1A2B3C4D-5E6F-5A7B-8C9D-0E1F2A3B4C5D"), store.Current.PinnedContainerId);
    }

    [TestMethod]
    [DataRow("5A6b7C8d9Eaf")]
    [DataRow("300E431D048")]
    [DataRow("5A6B7C8D9EAF0")]
    [DataRow("not an address")]
    public void UpdateRejectsAMalformedPinnedAddress(string address)
    {
        JsonSettingsStore store = Open();
        string before = File.ReadAllText(store.FilePath);

        Assert.ThrowsExactly<ArgumentException>(() => store.Update(s => s.PinnedAddress = address));

        Assert.AreEqual("", store.Current.PinnedAddress);
        Assert.AreEqual(before, File.ReadAllText(store.FilePath));
    }

    [TestMethod]
    public void UpdateRejectsThePcContainerAsThePin()
    {
        JsonSettingsStore store = Open();

        Assert.ThrowsExactly<ArgumentException>(() => store.Update(s => s.PinnedContainerId = NodeMatch.PcContainer));

        Assert.AreEqual(Guid.Empty, Open().Current.PinnedContainerId);
    }

    [TestMethod]
    public void ANewerSchemaIsReadButNeverRewrittenOrMoved()
    {
        const string newer = "{ \"SchemaVersion\": 2, \"DeviceMatch\": \"Beats\", \"OpenOnStartup\": false, \"SomethingNew\": { \"x\": 1 } }";
        File.WriteAllText(SettingsPath, newer);

        JsonSettingsStore store = Open();

        Assert.AreEqual(SettingsLoadStatus.NewerSchema, store.LastLoadStatus);
        Assert.IsTrue(store.IsReadOnly);
        Assert.AreEqual("Beats", store.Current.DeviceMatch);
        Assert.IsFalse(store.Current.OpenOnStartup);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "from a newer version of Earshot"));

        int changed = 0;
        store.Changed += (_, _) => changed++;
        store.Update(s => s.DeviceMatch = "Changed here");

        Assert.AreEqual("Changed here", store.Current.DeviceMatch);
        Assert.AreEqual(1, changed);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "not saved"));
        Assert.AreEqual(newer, File.ReadAllText(SettingsPath));
        Assert.IsFalse(File.Exists(store.BackupPath));
        Assert.IsFalse(File.Exists(store.TempPath));
        Assert.IsEmpty(Quarantined());
    }

    [TestMethod]
    public void ANewerSchemaWithUnusableValuesUsesDefaultsAndLeavesTheFile()
    {
        const string newer = "{ \"SchemaVersion\": 3, \"DeviceMatch\": \"\" }";
        File.WriteAllText(SettingsPath, newer);

        JsonSettingsStore store = Open();

        Assert.AreEqual(SettingsLoadStatus.NewerSchema, store.LastLoadStatus);
        Assert.IsTrue(store.IsReadOnly);
        AssertDefaults(store.Current);
        Assert.AreEqual(newer, File.ReadAllText(SettingsPath));
        Assert.IsEmpty(Quarantined());
    }

    [TestMethod]
    public void AReadOnlyStoreDoesNotCreateAMissingFile()
    {
        var store = new JsonSettingsStore(SettingsPath, _log, readOnly: true);

        Assert.AreEqual(SettingsLoadStatus.DefaultsReadOnly, store.LastLoadStatus);
        Assert.IsTrue(store.IsReadOnly);
        AssertDefaults(store.Current);

        store.Update(s => s.DeviceMatch = "Beats");
        store.Reload();

        Assert.IsEmpty(Directory.GetFileSystemEntries(_temp.Path));
    }

    [TestMethod]
    public void AReadOnlyStoreNeitherMovesNorRewritesAnUnusableFile()
    {
        JsonSettingsStore writer = Open();
        writer.Update(s => s.DeviceMatch = "Backup value");
        writer.Update(s => s.DeviceMatch = "Latest value");
        File.WriteAllText(SettingsPath, "{ \"DeviceMatch\": \"Lat");
        string[] before = Directory.GetFileSystemEntries(_temp.Path).Order(StringComparer.Ordinal).ToArray();
        string backupBefore = File.ReadAllText(SettingsPath + ".bak");

        var store = new JsonSettingsStore(SettingsPath, _log, readOnly: true);

        Assert.AreEqual(SettingsLoadStatus.UnusableReadOnly, store.LastLoadStatus);
        Assert.AreEqual("Backup value", store.Current.DeviceMatch);
        Assert.IsNull(store.QuarantinedFile);
        CollectionAssert.AreEqual(before, Directory.GetFileSystemEntries(_temp.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.AreEqual("{ \"DeviceMatch\": \"Lat", File.ReadAllText(SettingsPath));
        Assert.AreEqual(backupBefore, File.ReadAllText(SettingsPath + ".bak"));
    }

    [TestMethod]
    public void AReadOnlyStoreReadsAValidFile()
    {
        File.WriteAllText(SettingsPath, ValidJson);

        var store = new JsonSettingsStore(SettingsPath, _log, readOnly: true);

        Assert.AreEqual(SettingsLoadStatus.Loaded, store.LastLoadStatus);
        Assert.AreEqual("Beats", store.Current.DeviceMatch);
        Assert.AreEqual(ValidJson, File.ReadAllText(SettingsPath));
    }

    [TestMethod]
    public void RepeatedCorruptionInOneSecondKeepsEveryFile()
    {
        for (int i = 0; i < 3; i++)
        {
            File.WriteAllText(SettingsPath, "broken " + i);
            Assert.AreEqual(SettingsLoadStatus.ResetAfterCorruption, Open().LastLoadStatus);
        }

        Assert.HasCount(3, Quarantined());
    }

    [TestMethod]
    public void ReloadPicksUpAnExternalEditAndRaisesChanged()
    {
        JsonSettingsStore store = Open();
        int changed = 0;
        store.Changed += (_, _) => changed++;
        File.WriteAllText(SettingsPath, ValidJson);

        store.Reload();

        Assert.AreEqual("Beats", store.Current.DeviceMatch);
        Assert.IsFalse(store.Current.ProtectAudioQuality);
        Assert.AreEqual(1, changed);
    }

    [TestMethod]
    public void LockedFileIsNotOverwritten()
    {
        File.WriteAllText(SettingsPath, ValidJson);
        JsonSettingsStore store;
        using (new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store = Open();
        }

        Assert.AreEqual(SettingsLoadStatus.ReadFailed, store.LastLoadStatus);
        AssertDefaults(store.Current);
        Assert.IsTrue(_log.Entries.Any(e => e.Level == LogLevel.Error));
        Assert.AreEqual(ValidJson, File.ReadAllText(SettingsPath));
        Assert.IsEmpty(Quarantined());
    }
}
