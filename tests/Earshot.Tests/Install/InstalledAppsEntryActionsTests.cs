using System.Security.Cryptography;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Install;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Install;

// Install, repair, update and uninstall against temporary folders and a recording registry. The entry is written by the
// install every one of those goes through (update and first install hand over to it; repair is it, run from the install
// folder), so one place is checked and the others follow. Nothing here touches the machine hive.
[TestClass]
public sealed class InstalledAppsEntryActionsTests
{
    private sealed class RecordingRegistry : IUninstallRegistry
    {
        public List<(string SubKey, IReadOnlyDictionary<string, object> Values)> Writes { get; } = [];

        public List<string> Deletes { get; } = [];

        public bool FailWrites { get; set; }

        public bool FailDeletes { get; set; }

        public StepOutcome Write(string subKey, IReadOnlyDictionary<string, object> values)
        {
            if (FailWrites)
            {
                return StepOutcomes.FromHResult("installed-apps-entry", unchecked((int)0x80070005), "denied");
            }

            Writes.Add((subKey, values));
            return new StepOutcome("installed-apps-entry", true, 0, "S_OK", subKey);
        }

        public StepOutcome Delete(string subKey)
        {
            if (FailDeletes)
            {
                return StepOutcomes.FromHResult("installed-apps-entry-remove", unchecked((int)0x80070005), "denied");
            }

            Deletes.Add(subKey);
            return new StepOutcome("installed-apps-entry-remove", true, 0, "S_OK", subKey);
        }
    }

    private sealed class World : IDisposable
    {
        private readonly TempFolder _temp = new();

        public World()
        {
            Source = _temp.File(Path.Combine("unzip", "Earshot"));
            Install = _temp.File(Path.Combine("ProgramFiles", "Earshot"));
            Machine = _temp.File(Path.Combine("ProgramData", "Earshot"));
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Path.GetDirectoryName(Install)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Machine)!);
            File.WriteAllBytes(Path.Combine(Source, "Earshot.exe"), RandomNumberGenerator.GetBytes(4096));
            File.WriteAllBytes(Path.Combine(Source, "Earshot.dll"), RandomNumberGenerator.GetBytes(10_000));
            string[] listed = ["Earshot.exe", "Earshot.dll"];
            IEnumerable<string> entries = listed.Select(relative =>
                "    { \"Path\": \"" + relative + "\", \"Sha256\": \"" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Source, relative)))) + "\" }");
            File.WriteAllText(Path.Combine(Source, FileManifest.FileName),
                "{\r\n  \"SchemaVersion\": 1,\r\n  \"Files\": [\r\n" + string.Join(",\r\n", entries) + "\r\n  ]\r\n}\r\n");
        }

        public string Source { get; }

        public string Install { get; }

        public string Machine { get; }

        public FakeFolderSecurity Folders { get; } = new();

        public FakeTaskRegistrar Tasks { get; } = new();

        public RecordingRegistry Registry { get; } = new();

        public CapturingLog Log { get; } = new();

        public InstallResult RunInstall(string address, Guid container) =>
            new InstallActions(new InstallLayout(Source, Install, Machine), Folders, RecordedNodes.Table(), Tasks, NoLookup, Log)
            {
                AppsEntry = Registry,
            }.Run(new InstallRequest(TestUsers.Sid, address, container, TaskPrincipalMode.System));

        public InstallResult RunUninstall() =>
            new UninstallActions(new InstallLayout(Source, Install, Machine), Folders, RecordedNodes.Table(), Tasks, new RebootDeleteRecorder(), Log).Run();

        private static AccountLookup NoLookup(string account) => throw new AssertFailedException("No account name should be looked up: " + account);

        public void Dispose() => _temp.Dispose();
    }

    [TestMethod]
    public void AnInstallWritesTheEntryForTheFolderItInstalledInto()
    {
        using var w = new World();

        InstallResult result = w.RunInstall(RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer);

        Assert.AreEqual(GateExitCode.Success, result.Outcome);
        Assert.HasCount(1, w.Registry.Writes);
        (string subKey, IReadOnlyDictionary<string, object> values) = w.Registry.Writes[0];
        Assert.AreEqual("Earshot", subKey);
        Assert.AreEqual(w.Install, values["InstallLocation"]);
        Assert.AreEqual("\"" + Path.Combine(w.Install, "Earshot.exe") + "\" uninstall-ui", values["UninstallString"]);
        Assert.IsGreaterThanOrEqualTo(1, (int)values["EstimatedSize"], "The size is summed from the installed files.");
        Assert.IsTrue(result.Steps.Any(s => s.Step == "installed-apps-entry" && s.Ok));
    }

    [TestMethod]
    public void AnUpdateOrRepairRunsTheInstallAgainAndRewritesTheEntry()
    {
        using var w = new World();

        InstallResult first = w.RunInstall(RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer);
        InstallResult second = w.RunInstall(RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer);

        Assert.AreEqual(GateExitCode.Success, first.Outcome);
        Assert.AreEqual(GateExitCode.Success, second.Outcome);
        Assert.HasCount(2, w.Registry.Writes, "Each install run leaves the entry current.");
    }

    [TestMethod]
    public void AnInstallThatStopsBeforeAnythingIsInstalledWritesNoEntry()
    {
        using var w = new World();

        InstallResult result = w.RunInstall(RecordedNodes.IPhoneAddress, RecordedNodes.IPhoneContainer);

        Assert.AreNotEqual(GateExitCode.Success, result.Outcome);
        Assert.IsEmpty(w.Registry.Writes);
    }

    [TestMethod]
    public void AnEntryThatCannotBeWrittenMakesTheInstallPartialAndSaysWhy()
    {
        using var w = new World();
        w.Registry.FailWrites = true;

        InstallResult result = w.RunInstall(RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer);

        Assert.AreEqual(GateExitCode.Partial, result.Outcome);
        StepOutcome step = result.Steps.Single(s => s.Step == "installed-apps-entry");
        Assert.IsFalse(step.Ok);
        Assert.AreEqual(unchecked((int)0x80070005), step.Code, "The raw code is kept.");
    }

    [TestMethod]
    public void AnInstallWithNoRegistryWritesNothingAndStaysAsItWas()
    {
        using var w = new World();

        InstallResult result = new InstallActions(new InstallLayout(w.Source, w.Install, w.Machine), w.Folders, RecordedNodes.Table(), w.Tasks,
            a => throw new AssertFailedException(a), w.Log)
            .Run(new InstallRequest(TestUsers.Sid, RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer, TaskPrincipalMode.System));

        Assert.AreEqual(GateExitCode.Success, result.Outcome);
        Assert.IsFalse(result.Steps.Any(s => s.Step == "installed-apps-entry"));
    }

    [TestMethod]
    public void AnUninstallThatFinishedRemovesTheEntry()
    {
        using var w = new World();
        Assert.AreEqual(GateExitCode.Success, w.RunInstall(RecordedNodes.AirPodsAddress, RecordedNodes.AirPodsContainer).Outcome);
        InstallResult uninstalled = w.RunUninstall();

        InstallResult completed = UninstallActionsEntry.RemoveEntry(uninstalled, w.Registry);

        Assert.AreEqual(GateExitCode.Success, completed.Outcome);
        Assert.HasCount(1, w.Registry.Deletes);
        Assert.AreEqual("Earshot", w.Registry.Deletes[0]);
        Assert.IsTrue(completed.Steps.Any(s => s.Step == "installed-apps-entry-remove" && s.Ok));
    }

    [TestMethod]
    public void AnUninstallThatDidNotFinishKeepsTheEntrySoItCanBeRunAgain()
    {
        using var w = new World();
        var partial = new InstallResult(GateExitCode.Partial, [StepOutcomes.NotAttempted("remove-machine-folder", "kept")]);

        InstallResult completed = UninstallActionsEntry.RemoveEntry(partial, w.Registry);

        Assert.AreEqual(GateExitCode.Partial, completed.Outcome);
        Assert.IsEmpty(w.Registry.Deletes);
    }

    [TestMethod]
    public void TheRealInstallIsBuiltWithTheMachineUninstallKeyAndNoOtherRoot()
    {
        var layout = new InstallLayout(@"C:\x", @"C:\y", @"C:\z");

        IUninstallRegistry? entry = Earshot.Program.CreateInstallActions(layout, new CapturingLog()).AppsEntry;

        Assert.IsInstanceOfType<RegistryUninstallRoot>(entry);
        Assert.AreEqual(RegistryUninstallRoot.MachinePath, ((RegistryUninstallRoot)entry).Path);
    }

    [TestMethod]
    public void AnEntryThatCannotBeRemovedMakesAFinishedUninstallPartial()
    {
        using var w = new World();
        w.Registry.FailDeletes = true;

        InstallResult completed = UninstallActionsEntry.RemoveEntry(new InstallResult(GateExitCode.Success, []), w.Registry);

        Assert.AreEqual(GateExitCode.Partial, completed.Outcome);
        Assert.IsFalse(completed.Steps.Single(s => s.Step == "installed-apps-entry-remove").Ok);
    }
}
