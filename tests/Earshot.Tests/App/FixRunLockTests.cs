using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
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
        Assert.IsFalse(log.Entries.Any(e => e.Level == Earshot.Contracts.LogLevel.Warn), "A held lock is refused as held, not as a lock that could not be opened: " +
            string.Join(" | ", log.Entries.Select(e => e.Message)));
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

    // The tray takes the lock on one thread and may let it go from another after an await. The lock must still be free for the
    // next run: before, the release threw on the wrong thread, the throw was swallowed, and the mutex stayed held for as long as
    // the taking thread lived, so every later Fix and pairing in that tray was refused.
    [TestMethod]
    public void ALockLetGoFromAnotherThreadThanTheOneThatTookItIsFreeForTheNextRun()
    {
        string name = UniqueName();
        var log = new CapturingLog();
        FixRunLock? taken = null;
        using var takenEvent = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var taker = new Thread(() =>
        {
            taken = FixRunLock.TryAcquire(name, log);
            takenEvent.Set();
            finish.Wait();
        });
        taker.Start();
        try
        {
            Assert.IsTrue(takenEvent.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsNotNull(taken);

            taken.Dispose();

            FixRunLock? next = null;
            Task.Run(() => next = FixRunLock.TryAcquire(name, log)).GetAwaiter().GetResult();
            Assert.IsNotNull(next, "The lock stayed held after it was let go: " + string.Join(" | ", log.Entries.Select(e => e.Message)));
            next.Dispose();
        }
        finally
        {
            finish.Set();
            taker.Join();
        }
    }

    // A throw the lock does not expect from making the mutex (here ArgumentException and InvalidOperationException, neither of
    // which the named catch takes) ends in an answer, not a throw or a wait that never ends: the lock counts as held and the code
    // is logged. A long name does not do this on Windows, which takes a 400-character name, so the throw is made on purpose.
    [TestMethod]
    [DataRow("argument")]
    [DataRow("invalid-operation")]
    public void AnUnexpectedThrowFromMakingTheMutexIsCountedAsHeldWithoutHangingOrThrowing(string kind)
    {
        var log = new CapturingLog();
        Exception thrown = kind == "argument" ? new ArgumentException("refused") : new InvalidOperationException("refused");
        FixRunLock.CreateMutex create = (bool initiallyOwned, string name, out bool createdNew, MutexSecurity security) => throw thrown;
        Task<FixRunLock?> asked = Task.Run(() => FixRunLock.TryAcquire(UniqueName(), log, create));

        Assert.IsTrue(asked.Wait(TimeSpan.FromSeconds(10)), "TryAcquire did not answer.");
        Assert.IsNull(asked.Result);
        Assert.IsTrue(log.Entries.Any(e => e.Message.Contains("HRESULT 0x" + thrown.HResult.ToString("X8", CultureInfo.InvariantCulture), StringComparison.Ordinal)),
            string.Join(" | ", log.Entries.Select(e => e.Message)));
    }

    // The name gives this account full control and Administrators wait-and-release, and nothing to Everyone, so another
    // account that is not an administrator cannot open and hold it.
    [TestMethod]
    public void TheLockIsOpenToThisAccountAndAdministratorsOnly()
    {
        string name = UniqueName();
        using FixRunLock? held = FixRunLock.TryAcquire(name, new CapturingLog());
        Assert.IsNotNull(held);
        using Mutex opened = MutexAcl.OpenExisting(name, MutexRights.ReadPermissions);

        var rules = opened.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<MutexAccessRule>().ToList();

        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        using WindowsIdentity self = WindowsIdentity.GetCurrent();
        Assert.IsFalse(rules.Any(r => r.IdentityReference.Equals(world)), "Everyone has no access.");
        Assert.IsTrue(rules.Any(r => r.IdentityReference.Equals(admins) && r.MutexRights.HasFlag(MutexRights.Synchronize | MutexRights.Modify)));
        Assert.IsTrue(rules.Any(r => r.IdentityReference.Equals(self.User) && r.MutexRights.HasFlag(MutexRights.FullControl)));
        Assert.HasCount(2, rules, string.Join(", ", rules.Select(r => r.IdentityReference + " " + r.MutexRights)));
    }

    // The same thread asking twice is a second run, not a re-entry of the first: the tray's UI thread starts both.
    [TestMethod]
    public void TheSameThreadAskingTwiceIsRefusedTheSecondTime()
    {
        string name = UniqueName();
        var log = new CapturingLog();

        using FixRunLock? first = FixRunLock.TryAcquire(name, log);
        using FixRunLock? second = FixRunLock.TryAcquire(name, log);

        Assert.IsNotNull(first);
        Assert.IsNull(second);
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
