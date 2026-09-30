using System.Security.Cryptography;
using System.Text;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// The check of the installed files against the published list, run for real against a temporary install tree (no fake at
// the boundary: the hashing reads real files), and the choice of route that follows from it.
[TestClass]
public sealed class RepairCheckTests
{
    private sealed class Tree : IDisposable
    {
        private readonly TempFolder _temp = new();

        public Tree(bool manifest = true, bool listProgram = true)
        {
            Folder = _temp.File("Earshot");
            Directory.CreateDirectory(Path.Combine(Folder, "runtimes"));
            Write("Earshot.exe", "exe 1.2.0");
            Write("Earshot.dll", "dll 1.2.0");
            Write(Path.Combine("runtimes", "native.txt"), "native 1.2.0");
            if (manifest)
            {
                WriteManifest(listProgram);
            }
        }

        public string Folder { get; }

        public void Write(string relative, string text) => File.WriteAllText(Path.Combine(Folder, relative), text);

        public void WriteManifest(bool listProgram)
        {
            var listed = new List<string> { "Earshot.dll", "runtimes/native.txt" };
            if (listProgram)
            {
                listed.Insert(0, "Earshot.exe");
            }

            IEnumerable<string> entries = listed.Select(relative =>
                "    { \"Path\": \"" + relative + "\", \"Sha256\": \"" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Folder, relative.Replace('/', '\\'))))) + "\" }");
            File.WriteAllText(Path.Combine(Folder, FileManifest.FileName), "{\r\n  \"SchemaVersion\": 1,\r\n  \"Files\": [\r\n" + string.Join(",\r\n", entries) + "\r\n  ]\r\n}\r\n", Encoding.UTF8);
        }

        public void Dispose() => _temp.Dispose();
    }

    [TestMethod]
    public void EveryListedFileMatchingIsOkAndAFileNobodyListedIsIgnored()
    {
        using var tree = new Tree();
        tree.Write("user-left-this.txt", "not part of the release");

        InstalledFilesReport report = InstalledFileCheck.Check(tree.Folder);

        Assert.IsTrue(report.Ok, string.Join("; ", report.Steps.Select(GateActions.Describe)));
        Assert.IsFalse(report.ManifestUnusable);
        Assert.IsEmpty(report.Bad);
        Assert.IsTrue(report.Steps.Any(s => s.Step == "verify-installed" && s.Ok));
    }

    [TestMethod]
    public void EveryChangedOrMissingFileIsNamedNotOnlyTheFirst()
    {
        using var tree = new Tree();
        tree.Write("Earshot.dll", "dll tampered");
        File.Delete(Path.Combine(tree.Folder, "runtimes", "native.txt"));

        InstalledFilesReport report = InstalledFileCheck.Check(tree.Folder);

        Assert.IsFalse(report.Ok);
        Assert.IsFalse(report.ManifestUnusable);
        CollectionAssert.AreEquivalent(new[] { "Earshot.dll", Path.Combine("runtimes", "native.txt") }, report.Bad.ToArray());
        StepOutcome changed = report.Steps.Single(s => s.Step == "verify-installed:Earshot.dll");
        Assert.IsFalse(changed.Ok);
        StepOutcome missing = report.Steps.Single(s => s.Step.StartsWith("verify-installed:runtimes", StringComparison.Ordinal));
        Assert.AreEqual(unchecked((int)0x80070002), missing.Code, "The raw code of a missing file is on record: " + missing.CodeName);
    }

    [TestMethod]
    public void ANoFileListOrOneThatDoesNotListTheProgramIsNotUsable()
    {
        using var none = new Tree(manifest: false);
        InstalledFilesReport missing = InstalledFileCheck.Check(none.Folder);
        Assert.IsFalse(missing.Ok);
        Assert.IsTrue(missing.ManifestUnusable);
        Assert.IsFalse(missing.Steps.Single().Ok);

        using var noProgram = new Tree(listProgram: false);
        InstalledFilesReport unlisted = InstalledFileCheck.Check(noProgram.Folder);
        Assert.IsFalse(unlisted.Ok, "Earshot.exe must be in the list, or the check proves nothing about the program.");
        Assert.IsTrue(unlisted.ManifestUnusable);

        using var damaged = new Tree();
        damaged.Write(FileManifest.FileName, "not json");
        Assert.IsTrue(InstalledFileCheck.Check(damaged.Folder).ManifestUnusable);
    }

    // ----- the route -----

    private static readonly InstalledFilesReport Matching = new(true, false, [], []);

    private static readonly InstalledFilesReport OneBad = new(false, false, ["Earshot.dll"], []);

    private static readonly InstalledFilesReport NoList = new(false, true, [], []);

    [TestMethod]
    public void NothingInstalledAnUnusableInstallAndANewerRunningCopyAllGoThroughThisCopysSetup()
    {
        foreach (InstallState state in new[] { InstallState.Nothing, InstallState.Unusable })
        {
            RepairPlan plan = RepairPlanner.Decide(state, runningCopyIsNewer: false, files: null, new Version(1, 2, 1, 0));
            Assert.AreEqual(RepairRoute.SetUpFromThisCopy, plan.Route, state.ToString());
            Assert.AreEqual(RepairVerb.FromThisCopy, plan.Verb);
        }

        RepairPlan newer = RepairPlanner.Decide(InstallState.Usable, runningCopyIsNewer: true, Matching, new Version(1, 2, 1, 0));
        Assert.AreEqual(RepairRoute.SetUpFromThisCopy, newer.Route, "A copy newer than the install brings it up to date through its own setup.");
    }

    [TestMethod]
    public void FilesThatAllMatchMeanTheInstalledProgramRepairsItselfAndNothingIsDownloaded()
    {
        RepairPlan modern = RepairPlanner.Decide(InstallState.Usable, false, Matching, RepairPlanner.RepairVerbSince.ToVersion());
        Assert.AreEqual(RepairRoute.InstalledProgram, modern.Route);
        Assert.AreEqual(RepairVerb.Repair, modern.Verb);

        foreach (Version olderVersion in new[] { new Version(1, 2, 1, 0), new Version(1, 2, 9, 0) })
        {
            RepairPlan older = RepairPlanner.Decide(InstallState.Usable, false, Matching, olderVersion);
            Assert.AreEqual(RepairRoute.InstalledProgram, older.Route);
            Assert.AreEqual(RepairVerb.Install, older.Verb, olderVersion + ": a program from before the repair verb is asked through the install verb.");
        }

        RepairPlan unreadableVersion = RepairPlanner.Decide(InstallState.Usable, false, Matching, installedVersion: null);
        Assert.AreEqual(RepairRoute.InstalledProgram, unreadableVersion.Route);
        Assert.AreEqual(RepairVerb.Install, unreadableVersion.Verb, "A version that cannot be read is not assumed to know a verb.");
    }

    [TestMethod]
    public void AMissingOrChangedFileOrNoFileListMeansTheReleaseOfTheInstalledVersionIsDownloaded()
    {
        foreach (InstalledFilesReport files in new[] { OneBad, NoList })
        {
            RepairPlan plan = RepairPlanner.Decide(InstallState.Usable, false, files, new Version(1, 2, 1, 0));

            Assert.AreEqual(RepairRoute.DownloadThenUpdate, plan.Route);
            Assert.AreEqual(new ReleaseVersion(1, 2, 1), plan.Version);
        }

        RepairPlan unchecked_ = RepairPlanner.Decide(InstallState.Usable, false, files: null, new Version(1, 2, 1, 0));
        Assert.AreEqual(RepairRoute.DownloadThenUpdate, unchecked_.Route, "Files that were not checked are not trusted.");
    }

    [TestMethod]
    public void WhenTheInstalledVersionCannotBeReadTheFilesCannotBeFetchedSoThisCopysSetupRepairs()
    {
        RepairPlan plan = RepairPlanner.Decide(InstallState.Usable, false, OneBad, installedVersion: null);

        Assert.AreEqual(RepairRoute.SetUpFromThisCopy, plan.Route);
        Assert.IsNull(plan.Version);
    }

    [TestMethod]
    public void TheFileVersionOfARealProgramIsReadAndAMissingOneIsNone()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Version? version = new Earshot.Boot.InstalledFileReader().Read(cmd).Version;

        Assert.IsNotNull(version, "The installed program's file version is read from the file itself.");
        Assert.IsGreaterThan(0, version.Major);

        using var temp = new TempFolder();
        Assert.IsNull(new Earshot.Boot.InstalledFileReader().Read(Path.Combine(temp.Path, "Earshot.exe")).Version);
    }
}
