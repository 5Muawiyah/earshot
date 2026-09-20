using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// test-gui.md section 9.2: resume.txt is parsed, never executed. Every case here hands
// ResumeFile.TryParse a real file on disk and checks only that it read and validated the line's
// four values against the filesystem, exactly as Write-ResumeInstruction would have written it;
// none of these ever start a process.
[TestClass]
public sealed class ResumeFileTests
{
    private static readonly string RepoRoot = RepositoryLocator.RepositoryRoot();
    private static readonly string ScriptPath = Path.Combine(RepoRoot, "tools", "live-tests", "08-AcceptancePowerCycle.ps1");
    private static readonly string RealExePath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string[] KnownScripts = { "08-AcceptancePowerCycle.ps1" };

    private string _liveTestRoot = null!;

    [TestInitialize]
    public void CreateScratchLiveTestRoot()
    {
        _liveTestRoot = Path.Combine(Path.GetTempPath(), "earshot-resumefile-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_liveTestRoot);
        Assert.IsTrue(File.Exists(ScriptPath), "fixture script missing: " + ScriptPath);
        Assert.IsTrue(File.Exists(RealExePath), "fixture exe missing: " + RealExePath);
    }

    [TestCleanup]
    public void DeleteScratchLiveTestRoot()
    {
        if (Directory.Exists(_liveTestRoot))
        {
            Directory.Delete(_liveTestRoot, recursive: true);
        }
    }

    private string RunRoot() => Path.Combine(_liveTestRoot, "20260920T120000Z");

    private string WriteResumeTxt(string line)
    {
        string testFolder = Path.Combine(RunRoot(), "08-acceptance-power-cycle");
        Directory.CreateDirectory(testFolder);
        string path = Path.Combine(testFolder, "resume.txt");
        File.WriteAllText(path, line);
        return path;
    }

    private string ValidLine(string? variantSuffix = null) =>
        "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + ScriptPath + "\" -ExePath \"" + RealExePath +
        "\" -RunRoot \"" + RunRoot() + "\" -Resume" + variantSuffix;

    [TestMethod]
    public void AWellFormedLineParsesToTheFourValuesItNames()
    {
        string path = WriteResumeTxt(ValidLine());

        bool parsed = ResumeFile.TryParse(path, RepoRoot, _liveTestRoot, KnownScripts, out ResumeInstruction? instruction, out string? reason);

        Assert.IsTrue(parsed, reason);
        Assert.AreEqual(ScriptPath, instruction!.ScriptPath);
        Assert.AreEqual(RealExePath, instruction.ExePath);
        Assert.AreEqual(RunRoot(), instruction.RunRoot);
        Assert.IsNull(instruction.Variant);
    }

    [TestMethod]
    public void AVariantSuffixParsesToTheVariantNumber()
    {
        string path = WriteResumeTxt(ValidLine(" -Variant 3"));

        bool parsed = ResumeFile.TryParse(path, RepoRoot, _liveTestRoot, KnownScripts, out ResumeInstruction? instruction, out string? reason);

        Assert.IsTrue(parsed, reason);
        Assert.AreEqual(3, instruction!.Variant);
    }

    [TestMethod]
    public void AMissingFileFailsWithoutStartingAnything()
    {
        string path = Path.Combine(RunRoot(), "08-acceptance-power-cycle", "resume.txt");

        bool parsed = ResumeFile.TryParse(path, RepoRoot, _liveTestRoot, KnownScripts, out ResumeInstruction? instruction, out string? reason);

        Assert.IsFalse(parsed);
        Assert.IsNull(instruction);
        StringAssert.Contains(reason, "does not exist");
    }

    [TestMethod]
    public void ASecondLineOfAnyKindFailsTheOneLineShape()
    {
        string path = WriteResumeTxt(ValidLine() + "\nsomething else");

        bool parsed = ResumeFile.TryParse(path, RepoRoot, _liveTestRoot, KnownScripts, out ResumeInstruction? instruction, out string? reason);

        Assert.IsFalse(parsed);
        StringAssert.Contains(reason, "exactly one line");
    }

    [TestMethod]
    public void AScriptNotAmongTheSixteenShippedScriptsFails()
    {
        string otherScript = Path.Combine(RepoRoot, "tools", "live-tests", "01-A2dpOneShot.ps1");
        string line = "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + otherScript + "\" -ExePath \"" + RealExePath +
            "\" -RunRoot \"" + RunRoot() + "\" -Resume";
        string path = WriteResumeTxt(line);

        bool parsed = ResumeFile.TryParse(path, RepoRoot, _liveTestRoot, KnownScripts, out ResumeInstruction? instruction, out string? reason);

        Assert.IsFalse(parsed);
        StringAssert.Contains(reason, "one of the 16 shipped scripts");
    }

    [TestMethod]
    public void ARunRootThatIsNotTwoLevelsAboveResumeTxtFails()
    {
        string line = "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + ScriptPath + "\" -ExePath \"" + RealExePath +
            "\" -RunRoot \"" + _liveTestRoot + "\" -Resume";
        string path = WriteResumeTxt(line);

        bool parsed = ResumeFile.TryParse(path, RepoRoot, _liveTestRoot, KnownScripts, out ResumeInstruction? instruction, out string? reason);

        Assert.IsFalse(parsed);
        StringAssert.Contains(reason, "-RunRoot is not the folder two above it");
    }

    [TestMethod]
    public void ARunRootOutsideTheLiveTestRootFails()
    {
        string elsewhere = Path.Combine(Path.GetTempPath(), "earshot-resumefile-elsewhere-" + Guid.NewGuid().ToString("N"), "20260920T120000Z");
        string testFolder = Path.Combine(elsewhere, "08-acceptance-power-cycle");
        Directory.CreateDirectory(testFolder);
        try
        {
            string line = "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + ScriptPath + "\" -ExePath \"" + RealExePath +
                "\" -RunRoot \"" + elsewhere + "\" -Resume";
            string path = Path.Combine(testFolder, "resume.txt");
            File.WriteAllText(path, line);

            bool parsed = ResumeFile.TryParse(path, RepoRoot, _liveTestRoot, KnownScripts, out ResumeInstruction? instruction, out string? reason);

            Assert.IsFalse(parsed);
            StringAssert.Contains(reason, "does not lie under");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(elsewhere)!, recursive: true);
        }
    }

    [TestMethod]
    public void AnExePathThatDoesNotExistFails()
    {
        string missingExe = Path.Combine(_liveTestRoot, "no-such-earshot.exe");
        string line = "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + ScriptPath + "\" -ExePath \"" + missingExe +
            "\" -RunRoot \"" + RunRoot() + "\" -Resume";
        string path = WriteResumeTxt(line);

        bool parsed = ResumeFile.TryParse(path, RepoRoot, _liveTestRoot, KnownScripts, out ResumeInstruction? instruction, out string? reason);

        Assert.IsFalse(parsed);
        StringAssert.Contains(reason, "-ExePath does not exist");
    }

    [TestMethod]
    public void ALineNotMatchingWriteResumeInstructionsFormatFails()
    {
        string path = WriteResumeTxt("powershell -File \"" + ScriptPath + "\" -Resume");

        bool parsed = ResumeFile.TryParse(path, RepoRoot, _liveTestRoot, KnownScripts, out ResumeInstruction? instruction, out string? reason);

        Assert.IsFalse(parsed);
        StringAssert.Contains(reason, "does not match Write-ResumeInstruction's own format");
    }
}
