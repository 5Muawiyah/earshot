using System.Diagnostics;
using Earshot.App;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// One Fix connection (or Pair AirPods run) at a time: a named mutex taken by the fix verb for the whole run.
[TestClass]
public sealed class FixRunLockTests
{
    private static string UniqueName() => FixRunLock.BaseName + ".test-" + Guid.NewGuid().ToString("N");

    [TestMethod]
    public void ASecondAcquireWhileTheFirstIsHeldFindsItTaken()
    {
        string name = UniqueName();
        var log = new CapturingLog();

        using FixRunLock? first = FixRunLock.TryAcquire(name, log);
        Assert.IsNotNull(first);

        // A mutex is owned by a thread and re-entered by it, so the second taker is another thread, as another program is.
        FixRunLock? second = null;
        Task.Run(() => second = FixRunLock.TryAcquire(name, log)).GetAwaiter().GetResult();

        Assert.IsNull(second, "The lock was taken twice.");
    }

    [TestMethod]
    public void AReleasedLockCanBeTakenAgain()
    {
        string name = UniqueName();
        var log = new CapturingLog();
        FixRunLock? first = FixRunLock.TryAcquire(name, log);
        Assert.IsNotNull(first);
        first.Dispose();
        first.Dispose();

        using FixRunLock? again = FixRunLock.TryAcquire(name, log);

        Assert.IsNotNull(again);
    }

    [TestMethod]
    public void ARunAgainstARedirectedDataFolderHasANameOfItsOwn()
    {
        Paths real = Paths.FromEnvironment(_ => null);
        Paths redirectedA = Paths.FromEnvironment(n => n == Paths.DataRootVariable ? @"C:\Temp\earshot-a" : null);
        Paths redirectedB = Paths.FromEnvironment(n => n == Paths.DataRootVariable ? @"C:\Temp\earshot-b" : null);

        Assert.AreEqual(@"Global\Earshot.Fix.Run", FixRunLock.NameFor(real));
        Assert.AreNotEqual(FixRunLock.NameFor(real), FixRunLock.NameFor(redirectedA));
        Assert.AreNotEqual(FixRunLock.NameFor(redirectedA), FixRunLock.NameFor(redirectedB));
        Assert.AreEqual(FixRunLock.NameFor(redirectedA), FixRunLock.NameFor(Paths.FromEnvironment(n => n == Paths.DataRootVariable ? @"C:\TEMP\Earshot-A" : null)));
    }

    // The real program: the lock is held here, so a Fix started now ends AlreadyRunning without planning, asking or changing
    // anything. Safe mode and a temporary data folder, whose lock name is the redirected one.
    [TestMethod]
    public void ASecondFixRunWhileOneHoldsTheLockEndsAlreadyRunning()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Earshot.exe");
        if (!File.Exists(exe))
        {
            Assert.Inconclusive("The test build has no Earshot.exe beside the tests: " + exe);
        }

        using var temp = new TempFolder();
        string root = temp.File("data");
        using var safe = new EnvironmentVariableScope(Paths.SafeModeVariable, "1");
        using var rootScope = new EnvironmentVariableScope(Paths.DataRootVariable, root);
        string name = FixRunLock.NameFor(Paths.Current);
        string output = temp.File("fix-out.txt");

        int heldCode = RunFix(exe, output, holdLock: name);
        string heldText = File.ReadAllText(output);
        int freeCode = RunFix(exe, output, holdLock: null);

        Assert.AreEqual(ExitCodes.AlreadyRunning, heldCode, heldText);
        StringAssert.Contains(heldText, FixRunLock.FixAlreadyRunningMessage);
        Assert.AreEqual(ExitCodes.PlanOnly, freeCode, "With the lock free the same run plans as before.");
    }

    private static int RunFix(string exe, string output, string? holdLock)
    {
        using FixRunLock? held = holdLock is null ? null : FixRunLock.TryAcquire(holdLock, new CapturingLog());
        Assert.AreEqual(holdLock is not null, held is not null);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("fix");
        info.ArgumentList.Add("--out");
        info.ArgumentList.Add(output);
        using Process process = Process.Start(info)!;
        Assert.IsTrue(process.WaitForExit(TimeSpan.FromSeconds(60)), "fix did not end.");
        return process.ExitCode;
    }
}
