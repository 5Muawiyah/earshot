using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot.App;

// Tells the tray that a Fix connection run has ended, whoever started it. A run the tray started is seen through the process it
// started; a run started by Setup.exe or by the install script is not, and it can end with a new pair Earshot does not protect
// (an adoption that failed). Without this the card about that pair waits for the next sign-in.
//
// The tray creates a named auto-reset event (Listen) and waits on it. The fix verb sets it as it ends (Raise), however it was
// started. The event is in the session's Local namespace, which a program started by an elevated Setup.exe in the same session
// reaches; under Administrator protection that program is another account, so Administrators may set the event as well as the
// signed-in user. Anything allowed to set it can only make the tray run its read-only start checks again, which is harmless.
// https://learn.microsoft.com/en-us/dotnet/api/system.threading.eventwaithandleacl.create
// https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces
//
// A run the tray started raises the event itself and also ends as a process the tray is watching, so the tray hears twice. The
// two are one: a second notice within CoalesceWindow of the first is dropped.
internal sealed class FixEndedSignal : IDisposable
{
    public const string BaseName = @"Local\Earshot.FixEnded";

    // Long enough to cover the gap between the event and the process ending; short enough that a second run, which takes far
    // longer than this to ask its questions, is always heard.
    internal static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private readonly Action _recheck;
    private readonly TimeProvider _time;
    private readonly ILog _log;
    private EventWaitHandle? _event;
    private RegisteredWaitHandle? _wait;
    private DateTimeOffset? _lastNotice;

    private FixEndedSignal(Action recheck, TimeProvider time, ILog log)
    {
        _recheck = recheck;
        _time = time;
        _log = log;
    }

    // The event name for these folders. A run against a redirected data folder has a name of its own.
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

    // Creates the event and starts waiting. recheck runs on a pool thread each time the event is set, and when Notify is called;
    // the caller posts it to the UI thread. Null, logged, when the event could not be created: the tray still runs, and a run
    // started outside it is then seen at the next sign-in or when a new device appears, as before.
    public static FixEndedSignal? Listen(string name, Action recheck, TimeProvider time, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(recheck);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        var signal = new FixEndedSignal(recheck, time, log);
        EventWaitHandle created;
        try
        {
            using WindowsIdentity self = WindowsIdentity.GetCurrent();
            var security = new EventWaitHandleSecurity();
            if (self.User is { } user)
            {
                security.AddAccessRule(new EventWaitHandleAccessRule(user, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            }

            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
            created = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            log.Error("The Fix connection ended event could not be created, so a run started outside the tray is not noticed when it ends.", ex);
            return null;
        }

        signal._event = created;
        signal._wait = ThreadPool.RegisterWaitForSingleObject(
            created, (_, _) => signal.Notify("the Fix connection ended event"), state: null, Timeout.Infinite, executeOnlyOnce: false);
        return signal;
    }

    // Sets the tray's event. True when a tray was listening. Called by the fix verb as it ends; the event is only opened, so a
    // fix with no tray running does nothing and says nothing.
    public static bool Raise(string name, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            if (!EventWaitHandleAcl.TryOpenExisting(name, EventWaitHandleRights.Modify, out EventWaitHandle? ended))
            {
                return false;
            }

            using (ended)
            {
                ended.Set();
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            log.Warn("fix: the tray could not be told the run ended (" + ex.GetType().Name + ", HRESULT 0x" +
                ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ").", ex);
            return false;
        }
    }

    // A run the tray started has ended as a process. Coalesced with the event: whichever comes first rechecks, the other within
    // the window does not.
    public void Notify(string source)
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_lastNotice is { } last && now - last < CoalesceWindow)
            {
                return;
            }

            _lastNotice = now;
        }

        _log.Info("A Fix connection run ended (" + source + ").");
        try
        {
            _recheck();
        }
        catch (Exception ex)
        {
            _log.Error("The work after a Fix connection run ended failed (" + ex.GetType().Name + ").", ex);
        }
    }

    // Waits for a callback that is already running, so nothing is posted to a tray that has closed.
    public void Dispose()
    {
        if (_wait is { } wait)
        {
            using var unregistered = new ManualResetEvent(false);
            if (wait.Unregister(unregistered))
            {
                unregistered.WaitOne();
            }
        }

        _event?.Dispose();
    }
}
