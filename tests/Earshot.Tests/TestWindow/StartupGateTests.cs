using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The window shows a blocking screen and offers nothing else for five
// reasons. Each one is proved here, either as a pure decision (StartupGate.Evaluate never touches
// the real environment, token or mutex) or, where the fact-gathering itself is worth trusting,
// against something real: a real named mutex, a real temporary folder tree.
[TestClass]
public sealed class StartupGateTests
{
    [TestMethod]
    public void RunningElevatedWinsOverEverythingElse()
    {
        StartupRefusal refusal = StartupGate.Evaluate(
            isElevated: true, safeModeVariableSet: true, dataRootVariableSet: true, sandboxRequested: true, sandboxArgumentWithoutValidFolder: false,
            anotherInstanceRunning: true, solutionFound: false, powerShell51Found: false);

        Assert.AreEqual(StartupRefusal.RunningElevated, refusal);
    }

    [TestMethod]
    public void SafeModeOrDataRootRefusesWithoutSandbox()
    {
        Assert.AreEqual(StartupRefusal.SandboxEnvironmentVariableSet, StartupGate.Evaluate(
            isElevated: false, safeModeVariableSet: true, dataRootVariableSet: false, sandboxRequested: false, sandboxArgumentWithoutValidFolder: false,
            anotherInstanceRunning: false, solutionFound: true, powerShell51Found: true));

        Assert.AreEqual(StartupRefusal.SandboxEnvironmentVariableSet, StartupGate.Evaluate(
            isElevated: false, safeModeVariableSet: false, dataRootVariableSet: true, sandboxRequested: false, sandboxArgumentWithoutValidFolder: false,
            anotherInstanceRunning: false, solutionFound: true, powerShell51Found: true));
    }

    [TestMethod]
    public void SandboxArgumentLetsTheSafetyVariablesThrough()
    {
        StartupRefusal refusal = StartupGate.Evaluate(
            isElevated: false, safeModeVariableSet: true, dataRootVariableSet: true, sandboxRequested: true, sandboxArgumentWithoutValidFolder: false,
            anotherInstanceRunning: false, solutionFound: true, powerShell51Found: true);

        Assert.AreEqual(StartupRefusal.None, refusal);
    }

    // The defect this guards against: --sandbox with no folder starting REAL mode with the
    // environment refusal bypassed. sandboxArgumentWithoutValidFolder must refuse outright,
    // before the environment-variable check ever gets a chance to see sandboxRequested as a
    // reason to let it through: a caller that could not parse a folder must never also pass
    // sandboxRequested true.
    [TestMethod]
    public void SandboxArgumentWithoutAFolderRefusesEvenWithTheSafetyVariablesSet()
    {
        StartupRefusal refusal = StartupGate.Evaluate(
            isElevated: false, safeModeVariableSet: true, dataRootVariableSet: true, sandboxRequested: false,
            sandboxArgumentWithoutValidFolder: true, anotherInstanceRunning: false, solutionFound: true, powerShell51Found: true);

        Assert.AreEqual(StartupRefusal.SandboxRequestedWithoutFolder, refusal);
    }

    [TestMethod]
    public void SandboxArgumentWithoutAFolderRefusesEvenWithNoSafetyVariablesSet()
    {
        StartupRefusal refusal = StartupGate.Evaluate(
            isElevated: false, safeModeVariableSet: false, dataRootVariableSet: false, sandboxRequested: false,
            sandboxArgumentWithoutValidFolder: true, anotherInstanceRunning: false, solutionFound: true, powerShell51Found: true);

        Assert.AreEqual(StartupRefusal.SandboxRequestedWithoutFolder, refusal);
    }

    // M6, the other half: --sandbox followed by only whitespace is just as unusable a folder as
    // nothing at all.
    private static readonly string[] SandboxWithWhitespaceFolder = { "--sandbox", "   " };
    private static readonly string[] SandboxWithEmptyFolder = { "--sandbox", string.Empty };

    [TestMethod]
    public void SandboxFolderRejectsAWhitespaceOnlyValue()
    {
        Assert.IsNull(StartupGate.SandboxFolder(SandboxWithWhitespaceFolder));
        Assert.IsNull(StartupGate.SandboxFolder(SandboxWithEmptyFolder));
    }

    [TestMethod]
    public void AnotherInstanceRefusesWhenNotElevatedAndNotSandboxed()
    {
        StartupRefusal refusal = StartupGate.Evaluate(
            isElevated: false, safeModeVariableSet: false, dataRootVariableSet: false, sandboxRequested: false, sandboxArgumentWithoutValidFolder: false,
            anotherInstanceRunning: true, solutionFound: true, powerShell51Found: true);

        Assert.AreEqual(StartupRefusal.AnotherInstanceRunning, refusal);
    }

    [TestMethod]
    public void MissingSolutionRefuses()
    {
        StartupRefusal refusal = StartupGate.Evaluate(
            isElevated: false, safeModeVariableSet: false, dataRootVariableSet: false, sandboxRequested: false, sandboxArgumentWithoutValidFolder: false,
            anotherInstanceRunning: false, solutionFound: false, powerShell51Found: true);

        Assert.AreEqual(StartupRefusal.SolutionNotFound, refusal);
    }

    [TestMethod]
    public void MissingPowerShell51Refuses()
    {
        StartupRefusal refusal = StartupGate.Evaluate(
            isElevated: false, safeModeVariableSet: false, dataRootVariableSet: false, sandboxRequested: false, sandboxArgumentWithoutValidFolder: false,
            anotherInstanceRunning: false, solutionFound: true, powerShell51Found: false);

        Assert.AreEqual(StartupRefusal.WindowsPowerShell51Missing, refusal);
    }

    [TestMethod]
    public void EverythingClearLetsTheWindowOpen()
    {
        StartupRefusal refusal = StartupGate.Evaluate(
            isElevated: false, safeModeVariableSet: false, dataRootVariableSet: false, sandboxRequested: false, sandboxArgumentWithoutValidFolder: false,
            anotherInstanceRunning: false, solutionFound: true, powerShell51Found: true);

        Assert.AreEqual(StartupRefusal.None, refusal);
    }

    [TestMethod]
    public void EveryRefusalHasItsOwnMessage()
    {
        var messages = new HashSet<string>(StringComparer.Ordinal);
        foreach (StartupRefusal refusal in new[]
        {
            StartupRefusal.RunningElevated, StartupRefusal.SandboxRequestedWithoutFolder, StartupRefusal.SandboxEnvironmentVariableSet,
            StartupRefusal.AnotherInstanceRunning, StartupRefusal.SolutionNotFound,
            StartupRefusal.WindowsPowerShell51Missing,
        })
        {
            string message = StartupGate.Message(refusal);
            Assert.IsFalse(string.IsNullOrWhiteSpace(message), refusal + " has no message.");
            Assert.IsTrue(messages.Add(message), refusal + " shares its message with another refusal.");
        }
    }

    private static readonly string[] NoArguments = Array.Empty<string>();
    private static readonly string[] OtherArgumentOnly = { "--other" };
    private static readonly string[] SandboxWithFolder = { "--sandbox", @"C:\temp\sandbox" };
    private static readonly string[] SandboxWithNoFolder = { "--sandbox" };

    [TestMethod]
    public void HasSandboxArgumentOnlyEverFindsTheLiteralSwitch()
    {
        Assert.IsFalse(StartupGate.HasSandboxArgument(NoArguments));
        Assert.IsFalse(StartupGate.HasSandboxArgument(OtherArgumentOnly));
        Assert.IsTrue(StartupGate.HasSandboxArgument(SandboxWithFolder));
    }

    [TestMethod]
    public void SandboxFolderReadsNullWhenTheSwitchIsMissingOrHasNothingAfterIt()
    {
        Assert.IsNull(StartupGate.SandboxFolder(SandboxWithNoFolder));
        Assert.IsNull(StartupGate.SandboxFolder(OtherArgumentOnly));
    }

    // The three shapes that used to read as a usable folder even though none of them named one: a
    // relative path (resolved against whatever the process's own current directory happened to
    // be), a following switch (just as much non-whitespace text as a real path), and a folder
    // nested inside one of the four real roots SandboxOptions.ChildEnvironment redirects away from
    // (which left the "sandboxed" child writing to this machine's real data after all).
    [TestMethod]
    public void SandboxFolderRejectsARelativePath()
    {
        string[] args = { "--sandbox", @"relative\sandbox" };
        Assert.IsNull(StartupGate.SandboxFolder(args));
    }

    [TestMethod]
    public void SandboxFolderRejectsWhatLooksLikeAnotherSwitch()
    {
        string[] args = { "--sandbox", "--other" };
        Assert.IsNull(StartupGate.SandboxFolder(args));
    }

    [TestMethod]
    public void SandboxFolderRejectsAFolderInsideAProtectedRoot()
    {
        using var protectedRoot = new TempFolder();
        string nested = Path.Combine(protectedRoot.Path, "nested", "sandbox");
        Directory.CreateDirectory(nested);
        string[] args = { "--sandbox", nested };

        Assert.IsNull(StartupGate.SandboxFolder(args, new[] { protectedRoot.Path }));
    }

    [TestMethod]
    public void SandboxFolderRejectsTheProtectedRootItself()
    {
        using var protectedRoot = new TempFolder();
        string[] args = { "--sandbox", protectedRoot.Path };

        Assert.IsNull(StartupGate.SandboxFolder(args, new[] { protectedRoot.Path }));
    }

    // The one-argument overload must actually consult RealProtectedRoots(), not an empty list: a
    // folder nested under this machine's own real %LOCALAPPDATA% is refused even though the path
    // shape alone (absolute, well formed) would otherwise pass.
    [TestMethod]
    public void TheRealSandboxFolderOverloadUsesThisMachinesRealProtectedRoots()
    {
        string nested = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "earshot-startupgate-tests-should-never-exist");
        string[] args = { "--sandbox", nested };

        Assert.IsNull(StartupGate.SandboxFolder(args));
    }

    [TestMethod]
    public void SandboxFolderRejectsAFolderThatDoesNotExist()
    {
        using var parent = new TempFolder();
        string missing = Path.Combine(parent.Path, "does-not-exist");
        string[] args = { "--sandbox", missing };

        Assert.IsNull(StartupGate.SandboxFolder(args, Array.Empty<string>()));
    }

    [TestMethod]
    public void SandboxFolderAcceptsAnExistingEmptyFolderOutsideEveryProtectedRoot()
    {
        using var folder = new TempFolder();
        string[] args = { "--sandbox", folder.Path };

        Assert.AreEqual(folder.Path, StartupGate.SandboxFolder(args, Array.Empty<string>()));
    }

    // Reusing a folder an earlier --sandbox run already redirected LOCALAPPDATA/APPDATA/
    // ProgramData/ProgramFiles into must stay usable, since a folder that starts out empty always
    // ends up holding exactly these once the window has run in it once.
    [TestMethod]
    public void SandboxFolderAcceptsAFolderThatAlreadyHoldsOnlyItsOwnFourSubFolders()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "local"));
        Directory.CreateDirectory(Path.Combine(folder.Path, "roaming"));
        string[] args = { "--sandbox", folder.Path };

        Assert.AreEqual(folder.Path, StartupGate.SandboxFolder(args, Array.Empty<string>()));
    }

    [TestMethod]
    public void SandboxFolderRejectsAFolderThatHoldsAnythingOutsideItsOwnSandboxLayout()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "local"));
        File.WriteAllText(Path.Combine(folder.Path, "notes.txt"), "not part of the sandbox layout");
        string[] args = { "--sandbox", folder.Path };

        Assert.IsNull(StartupGate.SandboxFolder(args, Array.Empty<string>()));
    }

    // Real temporary folders: a repository root is found by walking up to Earshot.slnx, and a
    // folder with no such ancestor is not found.
    [TestMethod]
    public void FindSolutionAboveFindsARealSolutionFile()
    {
        using var repo = new TempFolder();
        File.WriteAllText(Path.Combine(repo.Path, "Earshot.slnx"), "<Solution/>");
        string nested = Path.Combine(repo.Path, "a", "b", "c");
        Directory.CreateDirectory(nested);

        Assert.IsTrue(StartupGate.FindSolutionAbove(nested, out string? found));
        Assert.AreEqual(repo.Path, found);
    }

    [TestMethod]
    public void FindSolutionAboveFailsWithoutOne()
    {
        using var repo = new TempFolder();
        Assert.IsFalse(StartupGate.FindSolutionAbove(repo.Path, out string? found));
        Assert.IsNull(found);
    }

    // A real named mutex, under a name of the test's own so it can never collide with a real
    // window on the machine running this. The second attempt at the same name is exactly what a
    // second copy of the window sees.
    [TestMethod]
    public void SecondAttemptAtTheSameMutexNameFindsItTaken()
    {
        string name = "Local\\EarshotTestWindowTests-" + Guid.NewGuid().ToString("N");
        using Mutex first = StartupGate.TryAcquireSingleInstance(name, out bool firstAcquired);
        using Mutex second = StartupGate.TryAcquireSingleInstance(name, out bool secondAcquired);

        Assert.IsTrue(firstAcquired, "The first attempt at a fresh name must succeed.");
        Assert.IsFalse(secondAcquired, "A second attempt at the same name must see it already taken.");
    }

    [TestMethod]
    public void AFreshMutexNameIsNotTakenByAnEarlierUnrelatedOne()
    {
        string name = "Local\\EarshotTestWindowTests-" + Guid.NewGuid().ToString("N");
        using Mutex mutex = StartupGate.TryAcquireSingleInstance(name, out bool acquired);
        Assert.IsTrue(acquired);
    }
}
