using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot.App;

// One Fix connection, or one Pair AirPods run, at a time on this PC. A Fix started from the tray, from Setup.exe and from the
// install script are three programs, so the answer cannot live in any one of them: it is a named mutex in the Global namespace,
// taken by the fix verb for the whole run and by Pair AirPods in the tray.
//
// A mutex belongs to the thread that took it, and only that thread can release it. The tray takes the lock on its UI thread and
// lets it go after awaits, which need not come back to the same thread; a release from another thread throws, and the mutex then
// stays held for as long as the taking thread lives, which in the tray is until Earshot exits. So the mutex is taken, held and
// released by a thread of the lock's own, which does nothing else, and Dispose asks that thread to release it from whichever
// thread Dispose is called on. Each acquire is its own thread, so the same caller asking twice is refused the second time.
//
// The account that runs Earshot has full control of the name and Administrators may wait on it, because a Setup.exe run is
// elevated and under Administrator protection that is another account than the signed-in one, though always in Administrators.
// No other account can open it, so a standard user signed in elsewhere cannot hold it. A mutex in the Global namespace can still
// be made first by anyone, with a DACL of their own, and then counts as held: that is accepted, and is in the log. A lock that
// cannot be opened is logged with its code; a lock that is held is logged as held, by a run or by whatever else holds the name,
// since which it is cannot be told.
// https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutexacl.create
// https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutex.releasemutex
// https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces
internal sealed class FixRunLock : IDisposable
{
    public const string BaseName = @"Global\Earshot.Fix.Run";
    // What a refusal by the lock can say: the name is held, by a run or by whatever else holds it, which cannot be told apart.
    public const string LockHeldMessage = "Fix connection or Pair AirPods may already be running. Try again in a moment.";

    // The tray's own Pair AirPods is running, which the tray knows.
    public const string PairAlreadyRunningMessage = "Earshot is already pairing the AirPods.";

    // How long Dispose waits for the holding thread to let the mutex go. It does nothing but release, so this is never reached
    // in practice; if it is, the wait is logged and the thread is left to finish on its own.
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(10);

    // The shape of MutexAcl.Create.
    internal delegate Mutex CreateMutex(bool initiallyOwned, string name, out bool createdNew, MutexSecurity security);

    private readonly Thread _holder;
    private readonly ManualResetEventSlim _release;
    private readonly string _name;
    private readonly ILog _log;
    private bool _released;

    private FixRunLock(Thread holder, ManualResetEventSlim release, string name, ILog log)
    {
        _holder = holder;
        _release = release;
        _name = name;
        _log = log;
    }

    // The mutex name for these folders. A run against a redirected data folder (a test or a smoke run) has a name of its own, so
    // it never contends with the real one.
    public static string NameFor(Paths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.DataRoot is null)
        {
            return BaseName;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(paths.DataRoot.ToLowerInvariant()));
        return BaseName + "." + Convert.ToHexString(hash, 0, 8);
    }

    // The lock when it was free, otherwise null. A mutex that its last owner ended while holding is taken over (the run that
    // held it is gone), and the takeover is logged. A mutex that cannot be created or opened at all is reported as held, with
    // the code in the log: not knowing is not a reason to start a second run.
    public static FixRunLock? TryAcquire(string name, ILog log) => TryAcquire(name, log, MutexAcl.Create);

    // The same, with the call that makes the mutex given, so a test can make it throw what the real call never throws on demand.
    internal static FixRunLock? TryAcquire(string name, ILog log, CreateMutex create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(create);
        var taken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);

        // A background thread, so a lock that is still held never keeps the process alive; when the process ends the system
        // frees the mutex as abandoned, and the next run takes it over.
        var holder = new Thread(() => Hold(name, log, create, taken, release)) { IsBackground = true, Name = "Earshot fix lock" };
        holder.Start();
        if (taken.Task.GetAwaiter().GetResult())
        {
            return new FixRunLock(holder, release, name, log);
        }

        holder.Join();
        release.Dispose();
        return null;
    }

    // The holding thread: takes the mutex without waiting, says whether it did, and when it did, holds it until Dispose asks for
    // it back, then releases it from this same thread.
    private static void Hold(string name, ILog log, CreateMutex create, TaskCompletionSource<bool> taken, ManualResetEventSlim release)
    {
        // Whatever ends this thread, the caller that waits for the answer is answered: an unexpected throw counts as held.
        try
        {
            HoldCore(name, log, create, taken, release);
        }
        catch (Exception ex)
        {
            log.Error("Fix lock: " + name + " could not be taken or held (" + Describe(ex) + "), so it is counted as held.", ex);
        }
        finally
        {
            taken.TrySetResult(false);
        }
    }

    private static void HoldCore(string name, ILog log, CreateMutex create, TaskCompletionSource<bool> taken, ManualResetEventSlim release)
    {
        Mutex mutex;
        try
        {
            // MutexAcl.Create opens a mutex that is already there with full access, so the account that runs Earshot is given full
            // control: a second run of the same account then opens it and is refused as held, and a run whose last owner died
            // takes it over. The Administrators entry grants wait and release only. Earshot's own open asks for full access, which
            // that entry does not give, so it lets no Earshot run in: an elevated Setup.exe under Administrator protection runs
            // as another account, cannot open the mutex with full access, and is refused as held. Nothing else is granted.
            var security = new MutexSecurity();
            using (WindowsIdentity self = WindowsIdentity.GetCurrent())
            {
                if (self.User is { } user)
                {
                    security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
                }
            }

            security.AddAccessRule(new MutexAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
            mutex = create(false, name, out _, security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            log.Warn("Fix lock: " + name + " could not be opened (" + Describe(ex) + "), so it is counted as held.", ex);
            taken.SetResult(false);
            return;
        }

        using (mutex)
        {
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                log.Warn("Fix lock: the run that held " + name + " ended without releasing it. This run takes it over.");
                owned = true;
            }

            taken.SetResult(owned);
            if (!owned)
            {
                return;
            }

            release.Wait();
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException ex)
            {
                // Only the owning thread releases, so this is not expected; if it happens the code is in the log, and the system
                // frees the mutex as abandoned when this thread ends, which it does now.
                log.Warn("Fix lock: " + name + " could not be released (" + Describe(ex) + ").", ex);
            }
        }
    }

    private static string Describe(Exception ex) =>
        ex.GetType().Name + ", HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _release.Set();
        if (!_holder.Join(ReleaseWait))
        {
            _log.Warn("Fix lock: " + _name + " was not let go within " + ReleaseWait.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s.");
            return;
        }

        _release.Dispose();
    }
}
