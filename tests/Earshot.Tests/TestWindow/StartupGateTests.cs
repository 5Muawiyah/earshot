using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// design.md section 8.1: the window shows a blocking screen and offers nothing else for five
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

    // M6: "--sandbox with no folder starts REAL mode with the environment refusal bypassed."
    // sandboxArgumentWithoutValidFolder must refuse outright, before the environment-variable
    // check ever gets a chance to see sandboxRequested as a reason to let it through: a caller
    // that could not parse a folder must never also pass sandboxRequested true.
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
    public void SandboxArgumentParsing()
    {
        Assert.IsFalse(StartupGate.HasSandboxArgument(NoArguments));
        Assert.IsFalse(StartupGate.HasSandboxArgument(OtherArgumentOnly));
        Assert.IsTrue(StartupGate.HasSandboxArgument(SandboxWithFolder));

        Assert.IsNull(StartupGate.SandboxFolder(SandboxWithNoFolder));
        Assert.AreEqual(@"C:\temp\sandbox", StartupGate.SandboxFolder(SandboxWithFolder));
        Assert.IsNull(StartupGate.SandboxFolder(OtherArgumentOnly));
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
