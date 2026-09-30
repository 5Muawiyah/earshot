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

    // ----- a file that could not be read is not a file that is missing -----

    // Another program holding an installed file open with no sharing: a standard user can do this to any file in Program Files
    // that they can read. The real check, on a real tree, with a real handle.
    [TestMethod]
    public void AFileAnotherProgramHoldsOpenIsUnreadableNotMissingOrDifferent()
    {
        using var tree = new Tree();
        using var held = new FileStream(Path.Combine(tree.Folder, "Earshot.exe"), FileMode.Open, FileAccess.Read, FileShare.None);

        InstalledFilesReport report = InstalledFileCheck.Check(tree.Folder);

        Assert.IsFalse(report.Ok);
        Assert.AreEqual("Earshot.exe", report.Bad.Single());
        Assert.AreEqual("Earshot.exe", report.Unreadable.Single(), "It is there; it could not be opened.");
        Assert.IsTrue(report.SomethingCouldNotBeRead);
        StepOutcome step = report.Steps.Single(s => s.Step == "verify-installed:Earshot.exe");
        Assert.AreEqual(unchecked((int)0x80070020), step.Code, "The raw code is a sharing violation: " + step.CodeName);
    }

    [TestMethod]
    public void AMissingFileAndAChangedFileAreNotUnreadable()
    {
        using var tree = new Tree();
        tree.Write("Earshot.dll", "dll tampered");
        File.Delete(Path.Combine(tree.Folder, "runtimes", "native.txt"));

        InstalledFilesReport report = InstalledFileCheck.Check(tree.Folder);

        Assert.HasCount(2, report.Bad);
        Assert.IsEmpty(report.Unreadable, "A file that is gone, and one that is read and differs, are findings about what is installed.");
        Assert.IsFalse(report.SomethingCouldNotBeRead);
    }

    [TestMethod]
    public void AFileListAnotherProgramHoldsOpenIsUnreadableButOneThatIsMissingOrNotJsonIsNot()
    {
        using var locked = new Tree();
        using (new FileStream(Path.Combine(locked.Folder, FileManifest.FileName), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            InstalledFilesReport report = InstalledFileCheck.Check(locked.Folder);
            Assert.IsTrue(report.ManifestUnusable);
            Assert.AreEqual(FileManifest.FileName, report.Unreadable.Single());
        }

        using var none = new Tree(manifest: false);
        Assert.IsEmpty(InstalledFileCheck.Check(none.Folder).Unreadable, "A list that is not there is missing.");

        using var damaged = new Tree();
        damaged.Write(FileManifest.FileName, "not json");
        Assert.IsEmpty(InstalledFileCheck.Check(damaged.Folder).Unreadable, "A list that is not valid is damaged, not unreadable.");
    }

    // The case a standard user can cause: the installed program held open makes the hash fail and the version unreadable.
    // Nothing may be elevated on the strength of that; above all not the running copy's setup. Through the real check and
    // the real reader on a real tree.
    [TestMethod]
    public void AnInstalledProgramAnotherProgramHoldsOpenIsNeverRepairedFromTheRunningCopy()
    {
        using var tree = new Tree();
        string exe = Path.Combine(tree.Folder, "Earshot.exe");
        using var held = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None);

        InstalledFilesReport files = InstalledFileCheck.Check(tree.Folder);
        Earshot.Boot.InstalledFile read = new Earshot.Boot.InstalledFileReader().Read(exe);
        RepairPlan plan = RepairPlanner.Decide(Usable, runningCopyIsNewer: false, files, read.Version);

        Assert.AreEqual(RepairRoute.CouldNotRead, plan.Route, plan.Why);
        Assert.AreNotEqual(RepairRoute.SetUpFromThisCopy, plan.Route);
        Assert.AreNotEqual(RepairVerb.FromThisCopy, plan.Verb);
    }

    // ----- the route -----

    private static readonly InstalledFilesReport Matching = new(true, false, [], []);

    private static readonly InstalledFilesReport OneBad = new(false, false, ["Earshot.dll"], []);

    private static readonly InstalledFilesReport NoList = new(false, true, [], []);

    private static readonly InstalledFilesReport OneUnreadable = new(false, false, ["Earshot.exe"], []) { Unreadable = ["Earshot.exe"] };

    private static readonly InstallAssessment Usable = new(InstallState.Usable, "installed");

    private static InstallAssessment Unusable(InstallProblem problem) => new(InstallState.Unusable, "unusable", problem);

    [TestMethod]
    public void OnlyNothingInstalledAnAbsentProgramOrAFolderAUserCanWriteGoThroughThisCopysSetup()
    {
        RepairPlan nothing = RepairPlanner.Decide(new InstallAssessment(InstallState.Nothing, "none"), runningCopyIsNewer: false, files: null, new Version(1, 2, 2, 0));
        Assert.AreEqual(RepairRoute.SetUpFromThisCopy, nothing.Route);
        Assert.AreEqual(RepairVerb.FromThisCopy, nothing.Verb);

        foreach (InstallProblem problem in new[] { InstallProblem.ProgramAbsent, InstallProblem.FolderNotTrusted })
        {
            RepairPlan plan = RepairPlanner.Decide(Unusable(problem), runningCopyIsNewer: false, files: null, new Version(1, 2, 2, 0));
            Assert.AreEqual(RepairRoute.SetUpFromThisCopy, plan.Route, problem.ToString());
            Assert.AreEqual(RepairVerb.FromThisCopy, plan.Verb);
        }
    }

    // A folder whose permissions could not be read says nothing about who may write to it, so the running copy, which may
    // be in a folder the signed-in user can write, is not made the elevated program on the strength of it.
    [TestMethod]
    public void AFolderWhoseSecurityCouldNotBeReadIsNotSetUpFromThisCopyAndNothingIsLaunched()
    {
        foreach (InstalledFilesReport? files in new InstalledFilesReport?[] { Matching, OneBad, NoList, null })
        {
            RepairPlan plan = RepairPlanner.Decide(Unusable(InstallProblem.FolderNotRead), runningCopyIsNewer: false, files, new Version(1, 2, 2, 0));

            Assert.AreEqual(RepairRoute.CouldNotRead, plan.Route);
            Assert.AreNotEqual(RepairVerb.FromThisCopy, plan.Verb);
        }
    }

    [TestMethod]
    public void AProgramNotConfirmedAbsentIsNotSetUpFromThisCopy()
    {
        RepairPlan plan = RepairPlanner.Decide(Unusable(InstallProblem.ProgramNotConfirmedAbsent), runningCopyIsNewer: false, files: null, installedVersion: null);

        Assert.AreEqual(RepairRoute.CouldNotRead, plan.Route);
    }

    [TestMethod]
    public void ACopyNewerThanTheInstalledOneIsSentToUpdateAndNeverElevated()
    {
        foreach (InstalledFilesReport? files in new InstalledFilesReport?[] { Matching, OneBad, NoList, null })
        {
            RepairPlan plan = RepairPlanner.Decide(Usable, runningCopyIsNewer: true, files, new Version(1, 2, 0, 0));

            Assert.AreEqual(RepairRoute.UpdateInstead, plan.Route);
            Assert.AreNotEqual(RepairVerb.FromThisCopy, plan.Verb, "The newer copy is not the elevated program.");
        }
    }

    [TestMethod]
    public void FilesThatAllMatchMeanTheInstalledProgramRepairsItselfAndNothingIsDownloaded()
    {
        RepairPlan modern = RepairPlanner.Decide(Usable, false, Matching, RepairPlanner.RepairVerbSince.ToVersion());
        Assert.AreEqual(RepairRoute.InstalledProgram, modern.Route);
        Assert.AreEqual(RepairVerb.Repair, modern.Verb);

        RepairPlan later = RepairPlanner.Decide(Usable, false, Matching, new Version(1, 3, 0, 0));
        Assert.AreEqual(RepairVerb.Repair, later.Verb);

        foreach (Version olderVersion in new[] { new Version(1, 1, 0, 0), new Version(1, 2, 0, 0), new Version(1, 2, 1, 0) })
        {
            RepairPlan older = RepairPlanner.Decide(Usable, false, Matching, olderVersion);
            Assert.AreEqual(RepairRoute.InstalledProgram, older.Route);
            Assert.AreEqual(RepairVerb.Install, older.Verb, olderVersion + ": a program from before the repair verb is asked through the install verb.");
        }

        RepairPlan unreadableVersion = RepairPlanner.Decide(Usable, false, Matching, installedVersion: null);
        Assert.AreEqual(RepairRoute.InstalledProgram, unreadableVersion.Route);
        Assert.AreEqual(RepairVerb.Install, unreadableVersion.Verb, "A version that cannot be read is not assumed to know a verb.");
    }

    // The first published 1.2.1 answers "Unknown command: repair" with exit 64 (proved by running it), and a later build of
    // 1.2.1 carries the same number, so the verb cannot be told from the version at 1.2.1. A threshold that drops to 1.2.1
    // asks a program without the verb for it, after the administrator prompt.
    [TestMethod]
    public void TheRepairVerbIsNotAssumedBeforeVersion122BecauseThePublished121HasNone()
    {
        Assert.AreEqual(new ReleaseVersion(1, 2, 2), RepairPlanner.RepairVerbSince);
        Assert.IsTrue(RepairPlanner.RepairVerbSince > new ReleaseVersion(1, 2, 1));

        RepairPlan published121 = RepairPlanner.Decide(Usable, false, Matching, new Version(1, 2, 1, 0));

        Assert.AreEqual(RepairVerb.Install, published121.Verb, "1.2.1 is an older program than the one that has the repair verb.");
    }

    [TestMethod]
    public void AMissingOrChangedFileOrNoFileListMeansTheReleaseOfTheInstalledVersionIsDownloaded()
    {
        foreach (InstalledFilesReport files in new[] { OneBad, NoList })
        {
            RepairPlan plan = RepairPlanner.Decide(Usable, false, files, new Version(1, 2, 1, 0));

            Assert.AreEqual(RepairRoute.DownloadThenUpdate, plan.Route);
            Assert.AreEqual(new ReleaseVersion(1, 2, 1), plan.Version);
        }

        RepairPlan unchecked_ = RepairPlanner.Decide(Usable, false, files: null, new Version(1, 2, 1, 0));
        Assert.AreEqual(RepairRoute.DownloadThenUpdate, unchecked_.Route, "Files that were not checked are not trusted.");
    }

    [TestMethod]
    public void AFileThatCouldNotBeReadMeansTryAgainAndNothingIsDownloadedOrElevated()
    {
        RepairPlan plan = RepairPlanner.Decide(Usable, false, OneUnreadable, new Version(1, 2, 1, 0));

        Assert.AreEqual(RepairRoute.CouldNotRead, plan.Route);
        StringAssert.Contains(plan.Why, "Earshot.exe");
    }

    // The version names the release to fetch. Without it there is nothing to fetch and nothing to decide from, which is not
    // the same as the files being missing: it does not mean the running copy's setup repairs the install.
    [TestMethod]
    public void WhenTheInstalledVersionCannotBeReadAndAFileDiffersNothingIsElevatedFromTheRunningCopy()
    {
        RepairPlan plan = RepairPlanner.Decide(Usable, false, OneBad, installedVersion: null);

        Assert.AreEqual(RepairRoute.CouldNotRead, plan.Route);
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
