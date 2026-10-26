using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot.App;

// Asks the tray to start Record a Bluetooth trace, from a Fix connection run's result window. The fix verb is a program of its
// own, and the trace belongs to the tray (one recording at a time, the Stop button, the helper that watches the tray), so the
// result window only asks: the tray answers the way its settings row does, with the note about what a trace holds and a Record
// button. Nothing is recorded, and no prompt is shown, until the person presses that button in the tray's card.
//
// The tray creates a named auto-reset event (Listen) and waits on it; the fix verb sets it (Raise). Like FixEndedSignal it is in
// the session's Local namespace and Administrators may set it as well as the signed-in user, because a Fix started by an
// elevated Setup.exe may run as another account under Administrator protection. Anything allowed to set it can only make the
// tray show that note, which is harmless.
// https://learn.microsoft.com/en-us/dotnet/api/system.threading.eventwaithandleacl.create
// https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces
internal sealed class TraceRequestSignal : IDisposable
{
    public const string BaseName = @"Local\Earshot.TraceRequested";

    private EventWaitHandle? _event;
    private RegisteredWaitHandle? _wait;

    private TraceRequestSignal()
    {
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

    // Creates the event and starts waiting. requested runs on a pool thread each time the event is set; the caller posts it to
    // the UI thread. Null, logged, when the event could not be created: the result window then says to use the settings row.
    public static TraceRequestSignal? Listen(string name, Action requested, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(log);
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
            log.Error("The trace request event could not be created, so a Fix connection result cannot start a trace.", ex);
            return null;
        }

        var signal = new TraceRequestSignal { _event = created };
        signal._wait = ThreadPool.RegisterWaitForSingleObject(created, (_, _) =>
        {
            log.Info("A Fix connection result asked for a Bluetooth trace.");
            try
            {
                requested();
            }
            catch (Exception ex)
            {
                log.Error("The trace request could not be passed to the tray (" + ex.GetType().Name + ").", ex);
            }
        }, state: null, Timeout.Infinite, executeOnlyOnce: false);
        return signal;
    }

    // Sets the tray's event. True when a tray was listening; false, logged, when none is (or it could not be reached).
    public static bool Raise(string name, ILog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            if (!EventWaitHandleAcl.TryOpenExisting(name, EventWaitHandleRights.Modify, out EventWaitHandle? requested))
            {
                log.Info("fix: no tray is listening for a trace request.");
                return false;
            }

            using (requested)
            {
                requested.Set();
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            log.Warn("fix: the tray could not be asked for a trace (" + ex.GetType().Name + ", HRESULT 0x" +
                ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ").", ex);
            return false;
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
