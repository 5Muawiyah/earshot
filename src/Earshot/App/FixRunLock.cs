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
// A mutex belongs to the thread that took it, so the run that takes it releases it from the same thread (Dispose).
//
// The name is open to every user of the PC (Everyone may wait on it), because a Setup.exe run is elevated and under
// Administrator protection that is another account than the signed-in one. Anything on the PC can therefore take the name and
// stop a Fix from starting. That is accepted: a program of the same user could do worse, and the refusal is logged with its
// code, so it is seen rather than mistaken for a fault in Fix.
// https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutexacl.create
// https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces
internal sealed class FixRunLock : IDisposable
{
    public const string BaseName = @"Global\Earshot.Fix.Run";
    public const string FixAlreadyRunningMessage = "Fix connection is already running.";
    public const string PairAlreadyRunningMessage = "Earshot is already pairing or fixing the AirPods.";

    private readonly Mutex _mutex;
    private bool _released;

    private FixRunLock(Mutex mutex)
    {
        _mutex = mutex;
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
    public static FixRunLock? TryAcquire(string name, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(log);
        Mutex mutex;
        try
        {
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
            mutex = MutexAcl.Create(false, name, out _, security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            log.Warn("Fix lock: " + name + " could not be opened (" + ex.GetType().Name + ", HRESULT 0x" +
                ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + "), so it is counted as held.", ex);
            return null;
        }

        try
        {
            if (mutex.WaitOne(0))
            {
                return new FixRunLock(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            log.Warn("Fix lock: the run that held " + name + " ended without releasing it. This run takes it over.");
            return new FixRunLock(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not released from the thread that took it: the process is ending the run anyway, and the system frees the
            // mutex with the process.
        }

        _mutex.Dispose();
    }
}
