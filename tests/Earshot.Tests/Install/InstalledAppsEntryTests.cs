using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Install;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Earshot.Tests.Install;

// The Installed apps entry, written under a temporary key of the current user (HKCU\Software\EarshotTests\<guid>)
// that stands in for HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall, and deleted afterwards. Nothing here
// touches the machine hive.
[TestClass]
public sealed class InstalledAppsEntryTests
{
    private const string InstallFolder = @"C:\Program Files\Earshot";

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = @"Software\EarshotTests\" + Guid.NewGuid().ToString("N");
            Registry = new RegistryUninstallRoot(Microsoft.Win32.Registry.CurrentUser, Path);
        }

        public string Path { get; }

        public RegistryUninstallRoot Registry { get; }

        public RegistryKey? OpenEntry() =>
            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Path + @"\" + InstalledAppsEntry.KeyName, writable: false);

        public void Dispose() => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
    }

    [TestMethod]
    public void TheValuesNameTheProgramItsPublisherAndTheVerbsThatManageIt()
    {
        IReadOnlyDictionary<string, object> v = InstalledAppsEntry.Build(InstallFolder, "1.5.0", 150_000_000, new DateOnly(2026, 10, 12));

        Assert.AreEqual("Earshot", v["DisplayName"]);
        Assert.AreEqual("1.5.0", v["DisplayVersion"]);
        Assert.AreEqual("5Muawiyah", v["Publisher"]);
        Assert.AreEqual("\"" + InstallFolder + "\\Earshot.exe\",0", v["DisplayIcon"]);
        Assert.AreEqual(InstallFolder, v["InstallLocation"]);
        Assert.AreEqual("\"" + InstallFolder + "\\Earshot.exe\" uninstall-ui", v["UninstallString"]);
        Assert.AreEqual("\"" + InstallFolder + "\\Earshot.exe\" repair-ui", v["ModifyPath"]);
        Assert.AreEqual(146_485, v["EstimatedSize"], "Kilobytes, rounded up.");
        Assert.AreEqual(1, v["NoRepair"]);
        Assert.AreEqual("20261012", v["InstallDate"]);
    }

    [TestMethod]
    public void AFolderWithSpacesIsQuotedInEveryCommand()
    {
        IReadOnlyDictionary<string, object> v = InstalledAppsEntry.Build(@"D:\Program Files\My Earshot", "1.5.0", 1, new DateOnly(2026, 1, 2));

        foreach (string name in new[] { "UninstallString", "ModifyPath" })
        {
            string command = (string)v[name];
            Assert.StartsWith("\"D:\\Program Files\\My Earshot\\Earshot.exe\" ", command, name);
        }

        Assert.AreEqual("\"D:\\Program Files\\My Earshot\\Earshot.exe\",0", v["DisplayIcon"]);
    }

    [TestMethod]
    public void AFolderWithAQuoteOrNoDriveIsRefusedSoNoCommandCanBeBroken()
    {
        Assert.ThrowsExactly<ArgumentException>(() => InstalledAppsEntry.Build("C:\\Evil\" & calc \"", "1.5.0", 1, new DateOnly(2026, 1, 2)));
        Assert.ThrowsExactly<ArgumentException>(() => InstalledAppsEntry.Build(@"relative\Earshot", "1.5.0", 1, new DateOnly(2026, 1, 2)));
    }

    [TestMethod]
    public void WritingThenReadingBackGivesTheSameValuesWithTheRightKinds()
    {
        using var root = new TempRoot();
        IReadOnlyDictionary<string, object> values = InstalledAppsEntry.Build(InstallFolder, "1.5.0", 2048, new DateOnly(2026, 10, 12));

        StepOutcome step = InstalledAppsEntry.Write(root.Registry, values);

        Assert.IsTrue(step.Ok, step.Detail);
        using RegistryKey? key = root.OpenEntry();
        Assert.IsNotNull(key);
        Assert.AreEqual("Earshot", key.GetValue("DisplayName"));
        Assert.AreEqual(RegistryValueKind.DWord, key.GetValueKind("EstimatedSize"));
        Assert.AreEqual(2, key.GetValue("EstimatedSize"));
        Assert.AreEqual(RegistryValueKind.DWord, key.GetValueKind("NoRepair"));
        Assert.AreEqual(RegistryValueKind.String, key.GetValueKind("UninstallString"));
        Assert.AreEqual("\"" + InstallFolder + "\\Earshot.exe\" repair-ui", key.GetValue("ModifyPath"));
    }

    [TestMethod]
    public void AnUpdateRewritesTheVersionAndLeavesNoValueBehind()
    {
        using var root = new TempRoot();
        Assert.IsTrue(InstalledAppsEntry.Write(root.Registry, InstalledAppsEntry.Build(InstallFolder, "1.4.0", 1024, new DateOnly(2026, 10, 1))).Ok);
        using (RegistryKey stray = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(root.Path + @"\" + InstalledAppsEntry.KeyName))
        {
            stray.SetValue("StrayValue", "old");
        }

        Assert.IsTrue(InstalledAppsEntry.Write(root.Registry, InstalledAppsEntry.Build(InstallFolder, "1.5.0", 1024, new DateOnly(2026, 10, 12))).Ok);

        using RegistryKey? entry = root.OpenEntry();
        Assert.IsNotNull(entry);
        Assert.AreEqual("1.5.0", entry.GetValue("DisplayVersion"));
        Assert.IsNull(entry.GetValue("StrayValue"), "The entry is replaced, not merged.");
    }

    [TestMethod]
    public void RemovingTheEntryDeletesItAndAMissingEntryIsNotAFailure()
    {
        using var root = new TempRoot();
        Assert.IsTrue(InstalledAppsEntry.Write(root.Registry, InstalledAppsEntry.Build(InstallFolder, "1.5.0", 1024, new DateOnly(2026, 10, 12))).Ok);

        StepOutcome removed = InstalledAppsEntry.Remove(root.Registry);
        StepOutcome again = InstalledAppsEntry.Remove(root.Registry);

        Assert.IsTrue(removed.Ok, removed.Detail);
        Assert.IsNull(root.OpenEntry());
        Assert.IsTrue(again.Ok, "Nothing to remove is already the wanted state.");
    }

    [TestMethod]
    public void TheMachineRootIsTheDocumentedUninstallKey()
    {
        string path = RegistryUninstallRoot.MachinePath;
        string key = InstalledAppsEntry.KeyName;
        Assert.AreEqual(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", path);
        Assert.AreEqual("Earshot", key);
    }

    [TestMethod]
    public void MeasureSumsTheListedFilesAndTheManifestAndReadsNoVersionFromAFileWithNone()
    {
        string folder = Path.Combine(Path.GetTempPath(), "EarshotEntry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "Earshot.exe"), new byte[3000]);
            File.WriteAllBytes(Path.Combine(folder, "a.dll"), new byte[2000]);
            File.WriteAllText(Path.Combine(folder, FileManifest.FileName),
                "{\"SchemaVersion\":1,\"Files\":[{\"Path\":\"Earshot.exe\",\"Sha256\":\"" + new string('A', 64) + "\"},{\"Path\":\"a.dll\",\"Sha256\":\"" + new string('B', 64) + "\"}]}");

            InstalledAppsEntry.Measured m = InstalledAppsEntry.Measure(folder);

            Assert.IsGreaterThanOrEqualTo(5000, m.SizeBytes, "Both listed files and the manifest are counted.");
            Assert.AreEqual("", m.Version, "A file with no version resource reports none.");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
