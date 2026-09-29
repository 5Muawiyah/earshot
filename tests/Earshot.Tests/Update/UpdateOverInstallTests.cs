using System.Diagnostics;
using System.Security.Cryptography;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// What an update does to a machine that already has Earshot, established by running the real install and uninstall
// actions against temporary folders, a fake folder security and an in-memory Task Scheduler. The update hands over
// to install (the staged Earshot.exe run with the install verb), so "setup over an existing install" is install
// run a second time from a new release folder. The data folders are laid out as EARSHOT_DATA_ROOT lays them out, so
// the settings, the battery set-up data and the logs sit beside the machine folder exactly as they do for a real
// user. Nothing here touches Program Files, ProgramData, the real task namespace or a device node.
[TestClass]
public sealed class UpdateOverInstallTests
{
    private const string OwnersSettings =
        "{ \"SchemaVersion\": 1, \"DeviceMatch\": \"AirPods\", \"PinnedAddress\": \"0A1B2C3D4E8C\", \"CheckForUpdatesAutomatically\": true, \"HandBackOnShutdownAndSleep\": false }";

    private sealed class World : IDisposable
    {
        private readonly TempFolder _temp = new();

        public World()
        {
            Data = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? _temp.File("data") : null);
            Install = _temp.File(Path.Combine("ProgramFiles", "Earshot"));
            Directory.CreateDirectory(Path.GetDirectoryName(Install)!);
            Directory.CreateDirectory(Data.RoamingFolder);
            Directory.CreateDirectory(Data.LocalFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(Data.MachineFolder)!);
        }

        public Paths Data { get; }

        private string[] DataFolders => [Data.RoamingFolder, Data.LocalFolder];

        public string Install { get; }

        public FakeFolderSecurity Folders { get; } = new();

        public FakeTaskRegistrar Tasks { get; } = new();

        public FakeNodeApi Nodes { get; } = RecordedNodes.Table();

        public RebootDeleteRecorder Reboot { get; } = new();

        public CapturingLog Log { get; } = new();

        public string NewRelease(string name, string version, params string[] extraFiles)
        {
            string folder = _temp.File(Path.Combine("unzip", name, "Earshot"));
            Directory.CreateDirectory(Path.Combine(folder, "runtimes"));
            File.WriteAllText(Path.Combine(folder, "Earshot.exe"), "exe " + version);
            File.WriteAllText(Path.Combine(folder, "Earshot.dll"), "dll " + version);
            File.WriteAllText(Path.Combine(folder, "runtimes", "native.txt"), "native " + version);
            var listed = new List<string> { "Earshot.exe", "Earshot.dll", "runtimes/native.txt" };
            foreach (string extra in extraFiles)
            {
                File.WriteAllText(Path.Combine(folder, extra), extra + " " + version);
                listed.Add(extra);
            }

            IEnumerable<string> entries = listed.Select(relative =>
                "    { \"Path\": \"" + relative + "\", \"Sha256\": \"" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, relative.Replace('/', '\\'))))) + "\" }");
            File.WriteAllText(Path.Combine(folder, FileManifest.FileName), "{\r\n  \"SchemaVersion\": 1,\r\n  \"Files\": [\r\n" + string.Join(",\r\n", entries) + "\r\n  ]\r\n}\r\n");
            return folder;
        }

        public InstallResult RunInstall(string source) =>
            new InstallActions(new InstallLayout(source, Install, Data.MachineFolder), Folders, Nodes, Tasks, NoLookup, Log)
                .Run(new InstallRequest(TestUsers.Sid, RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer, TaskPrincipalMode.System));

        public InstallResult RunUninstall(string source) =>
            new UninstallActions(new InstallLayout(source, Install, Data.MachineFolder), Folders, Nodes, Tasks, Reboot, Log).Run();

        // The owner's own data: settings, the battery set-up (the widget's claim), logs and a live-test record.
        public void WriteOwnersData()
        {
            File.WriteAllText(Data.SettingsFile, OwnersSettings);
            Directory.CreateDirectory(Data.WidgetFolder);
            File.WriteAllText(Data.WidgetClaimFile, "{ \"claim\": \"invented battery set-up values\" }");
            Directory.CreateDirectory(Data.LogFolder);
            File.WriteAllText(Data.LogFile, "an earlier session\r\n");
            Directory.CreateDirectory(Data.LiveTestFolder);
            File.WriteAllText(Path.Combine(Data.LiveTestFolder, "evidence.json"), "{}");
        }

        // Every file under the roaming and local data folders, with its hash: what must not change.
        public SortedDictionary<string, string> OwnersDataSnapshot()
        {
            var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string root in DataFolders)
            {
                foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    snapshot[file] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
                }
            }

            return snapshot;
        }

        public string ReadInstalled(string relative) => File.ReadAllText(Path.Combine(Install, relative.Replace('/', '\\')));

        private static AccountLookup NoLookup(string account) =>
            throw new AssertFailedException("No account name should be looked up: " + account);

        public void Dispose() => _temp.Dispose();
    }

    private static readonly string[] ReplacedTaskCalls = ["delete-task Gate", "delete-task Protect", "delete-task BootBlock", "delete-folder Earshot", "create-folder Earshot"];

    private static readonly string[] LayoutMembers = ["InstallFolder", "MachineFolder", "SourceFolder"];

    private static string Fail(InstallResult result) =>
        string.Join(Environment.NewLine, result.Steps.Where(s => !s.Ok).Select(GateActions.Describe));

    private static string[] LeftOvers(string install) =>
        Directory.GetDirectories(Path.GetDirectoryName(install)!)
            .Where(d => Path.GetFileName(d).StartsWith("Earshot.", StringComparison.Ordinal))
            .ToArray();

    [TestMethod]
    public void InstallOverAnExistingInstallReplacesTheFilesAndKeepsTheOwnersSettingsAndBatterySetUp()
    {
        using var w = new World();
        string v1 = w.NewRelease("v1", "1.1.0", "old-only.dll");
        string v2 = w.NewRelease("v2", "1.2.0", "new-only.dll");
        Assert.AreEqual(GateExitCode.Success, w.RunInstall(v1).Outcome);
        w.WriteOwnersData();
        Assert.IsTrue(new GateStore(w.Data.MachineFolder).WriteConfig(new GateConfig { BlockAtBoot = false }).Ok, "The owner turned Block at boot off.");
        string deviceBefore = File.ReadAllText(new GateStore(w.Data.MachineFolder).DeviceFile);
        SortedDictionary<string, string> dataBefore = w.OwnersDataSnapshot();
        Assert.AreEqual("exe 1.1.0", w.ReadInstalled("Earshot.exe"));

        InstallResult update = w.RunInstall(v2);

        Assert.AreEqual(GateExitCode.Success, update.Outcome, Fail(update));
        Assert.AreEqual("exe 1.2.0", w.ReadInstalled("Earshot.exe"));
        Assert.AreEqual("dll 1.2.0", w.ReadInstalled("Earshot.dll"));
        Assert.AreEqual("native 1.2.0", w.ReadInstalled("runtimes/native.txt"));
        Assert.AreEqual("new-only.dll 1.2.0", w.ReadInstalled("new-only.dll"));
        Assert.IsFalse(File.Exists(Path.Combine(w.Install, "old-only.dll")), "A file the new release dropped is gone.");
        Assert.IsEmpty(LeftOvers(w.Install), "No staging or old-copy folder is left beside the install folder.");

        // The owner's data is what it was, byte for byte.
        CollectionAssert.AreEqual(dataBefore.ToArray(), w.OwnersDataSnapshot().ToArray(), "Nothing under the roaming and local data folders changed.");
        var settings = new JsonSettingsStore(w.Data.SettingsFile, new CapturingLog());
        Assert.AreEqual(SettingsLoadStatus.Loaded, settings.LastLoadStatus);
        Assert.IsTrue(settings.Current.CheckForUpdatesAutomatically);
        Assert.IsFalse(settings.Current.HandBackOnShutdownAndSleep);
        Assert.AreEqual("0A1B2C3D4E8C", settings.Current.PinnedAddress);

        // The machine folder keeps the pinned device and the owner's Block at boot choice, and the tasks are back.
        var store = new GateStore(w.Data.MachineFolder);
        Assert.AreEqual(deviceBefore, File.ReadAllText(store.DeviceFile), "The pinned device is the same.");
        Assert.IsFalse(store.ReadConfig().Value!.BlockAtBoot, "Block at boot stays as the owner set it.");
        CollectionAssert.AreEquivalent(TaskPlan.TaskNames.Select(TaskPlan.TaskPath).ToArray(), w.Tasks.Tasks.Keys.ToArray());
        CollectionAssert.IsSubsetOf(ReplacedTaskCalls, w.Tasks.Calls);
    }

    // The reason the tray closes before the hand-over: the install swaps the whole install folder, and Windows will
    // not rename a folder while a process has a file open in it or has it as its current folder. A program that is
    // only running from the folder does not stop it; a double-clicked one does, by its working directory. The
    // failure leaves the old install exactly as it was.
    [TestMethod]
    public void AnInstallFolderSomeProcessHasFilesOpenInMakesTheUpdateFailAndLeavesTheOldInstallWorking()
    {
        using var w = new World();
        string v1 = w.NewRelease("v1", "1.1.0");
        string v2 = w.NewRelease("v2", "1.2.0");
        Assert.AreEqual(GateExitCode.Success, w.RunInstall(v1).Outcome);
        w.WriteOwnersData();
        SortedDictionary<string, string> dataBefore = w.OwnersDataSnapshot();

        InstallResult blocked;
        using (new FileStream(Path.Combine(w.Install, "Earshot.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            blocked = w.RunInstall(v2);
        }

        Assert.AreEqual(GateExitCode.Failed, blocked.Outcome);
        Assert.IsTrue(blocked.Steps.Any(s => s.Step == "move-old-install" && !s.Ok), "The folder swap is what failed: " + Fail(blocked));
        Assert.AreEqual("exe 1.1.0", w.ReadInstalled("Earshot.exe"), "The old install is untouched.");
        Assert.IsEmpty(LeftOvers(w.Install), "The half-made copy is removed.");
        CollectionAssert.AreEqual(dataBefore.ToArray(), w.OwnersDataSnapshot().ToArray());
        Assert.AreEqual(3, w.Tasks.Tasks.Count, "The tasks were not touched, so the boot block still works.");

        Assert.AreEqual(GateExitCode.Success, w.RunInstall(v2).Outcome, "Once nothing holds the folder, the same update goes through.");
        Assert.AreEqual("exe 1.2.0", w.ReadInstalled("Earshot.exe"));
    }

    [TestMethod]
    public void AProgramWhoseCurrentFolderIsTheInstallFolderBlocksTheUpdateUntilItEnds()
    {
        using var w = new World();
        string v1 = w.NewRelease("v1", "1.1.0");
        string v2 = w.NewRelease("v2", "1.2.0");
        Assert.AreEqual(GateExitCode.Success, w.RunInstall(v1).Outcome);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 60 127.0.0.1")
        {
            WorkingDirectory = w.Install,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using Process running = Process.Start(start)!;
        try
        {
            Thread.Sleep(300);

            InstallResult blocked = w.RunInstall(v2);

            Assert.AreEqual(GateExitCode.Failed, blocked.Outcome, "A tray started by double-click has its install folder as its current folder.");
            Assert.IsTrue(blocked.Steps.Any(s => s.Step == "move-old-install" && !s.Ok));
        }
        finally
        {
            running.Kill(entireProcessTree: true);
            running.WaitForExit();
        }

        InstallResult after = w.RunInstall(v2);

        Assert.AreEqual(GateExitCode.Success, after.Outcome, Fail(after));
        Assert.AreEqual("exe 1.2.0", w.ReadInstalled("Earshot.exe"));
    }

    [TestMethod]
    public void UninstallLeavesTheOwnersSettingsBatterySetUpAndLogsAloneAndAReinstallFindsThem()
    {
        using var w = new World();
        string v1 = w.NewRelease("v1", "1.1.0");
        Assert.AreEqual(GateExitCode.Success, w.RunInstall(v1).Outcome);
        w.WriteOwnersData();
        SortedDictionary<string, string> dataBefore = w.OwnersDataSnapshot();

        InstallResult removed = w.RunUninstall(v1);

        Assert.AreEqual(GateExitCode.Success, removed.Outcome, Fail(removed));
        Assert.IsFalse(Directory.Exists(w.Data.MachineFolder), "The machine folder (device, Block at boot, protection record) is removed.");
        Assert.IsFalse(Directory.Exists(w.Install), "The program folder is removed.");
        Assert.IsEmpty(w.Tasks.Tasks);
        CollectionAssert.AreEqual(dataBefore.ToArray(), w.OwnersDataSnapshot().ToArray(), "Uninstall never reads or removes the roaming and local data folders.");

        Assert.AreEqual(GateExitCode.Success, w.RunInstall(v1).Outcome);
        Assert.IsTrue(new JsonSettingsStore(w.Data.SettingsFile, new CapturingLog()).Current.CheckForUpdatesAutomatically, "A reinstall finds the settings as they were.");
        CollectionAssert.AreEqual(dataBefore.ToArray(), w.OwnersDataSnapshot().ToArray());
    }

    [TestMethod]
    public void TheInstallLayoutHasNoPathToTheRoamingOrLocalDataFolders()
    {
        // By construction, not only by behaviour: what install and uninstall are given to act on.
        string[] members = typeof(InstallLayout).GetProperties().Select(p => p.Name).Order().ToArray();

        CollectionAssert.AreEqual(LayoutMembers, members);
    }
}
