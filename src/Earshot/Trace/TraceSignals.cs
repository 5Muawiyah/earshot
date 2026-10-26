using System.Diagnostics;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Earshot.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Earshot.Tracing;

// The three named events the tray creates before it asks for the administrator prompt, and the helper opens. The tray makes
// them because an event made by the elevated helper would carry a descriptor the tray's unelevated token cannot open.
// "Local\" is the namespace of the terminal services session, which the tray and its elevated helper share. The helper may run
// as another account than the tray (an administrator's credentials typed on a standard user's desktop, or Administrator
// protection), so each event grants the signed-in user full control and Administrators wait and set.
// https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces
// https://learn.microsoft.com/en-us/dotnet/api/system.threading.eventwaithandleacl.create
internal static class TraceEventNames
{
    public static string Stop(string nonce) => @"Local\Earshot.Trace.Stop." + Checked(nonce);

    public static string Done(string nonce) => @"Local\Earshot.Trace.Done." + Checked(nonce);

    public static string Copied(string nonce) => @"Local\Earshot.Trace.Copied." + Checked(nonce);

    private static string Checked(string nonce)
    {
        if (!BoundaryValidation.IsNonce(nonce))
        {
            throw new ArgumentException("A trace event name needs a 32 character lower-case hex nonce.", nameof(nonce));
        }

        return nonce;
    }
}

// The helper's side.
internal sealed class TraceSignals : ITraceSignals, IDisposable
{
    private readonly TraceRequest _request;
    private EventWaitHandle? _stop;
    private EventWaitHandle? _done;
    private EventWaitHandle? _copied;
    private readonly ILog _log;
    private Process? _tray;
    private ManualResetEvent? _trayExit;

    public TraceSignals(TraceRequest request, ILog log)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(log);
        _request = request;
        _log = log;
    }

    public string? Open()
    {
        try
        {
            _stop = System.Threading.EventWaitHandleAcl.OpenExisting(TraceEventNames.Stop(_request.Nonce), System.Security.AccessControl.EventWaitHandleRights.Synchronize);
            _done = System.Threading.EventWaitHandleAcl.OpenExisting(TraceEventNames.Done(_request.Nonce), System.Security.AccessControl.EventWaitHandleRights.Modify);
            _copied = System.Threading.EventWaitHandleAcl.OpenExisting(TraceEventNames.Copied(_request.Nonce), System.Security.AccessControl.EventWaitHandleRights.Synchronize);
            _tray = Process.GetProcessById(_request.TrayPid);

            // A process handle is waitable (it is signalled when the process ends), so it is wrapped in an event object
            // that does not own it and waited on beside the stop event.
            // https://learn.microsoft.com/en-us/windows/win32/sync/using-process-and-thread-objects
            _trayExit = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_tray.SafeHandle.DangerousGetHandle(), ownsHandle: false) };
            return null;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or IOException or SecurityException or System.ComponentModel.Win32Exception)
        {
            return ex.GetType().Name + " (HRESULT 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "): " + ex.Message;
        }
    }

    public bool TrayRunning()
    {
        try
        {
            return _tray is not null && !_tray.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Warn("trace: the tray process could not be queried (" + ex.GetType().Name + ", HRESULT 0x" +
                ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "), so it counts as gone.");
            return false;
        }
    }

    public TraceStopReason WaitForStop(TimeSpan max)
    {
        if (_stop is null || _trayExit is null)
        {
            throw new InvalidOperationException("Open() has not succeeded.");
        }

        int index = WaitHandle.WaitAny([_stop, _trayExit], max);
        return index switch
        {
            0 => TraceStopReason.StopRequested,
            1 => TraceStopReason.TrayExited,
            _ => TraceStopReason.TimedOut,
        };
    }

    // The stop event is manual-reset, so a Stop pressed before this helper existed is still set.
    public bool StopAsked() => _stop?.WaitOne(0) == true;

    public void SignalDone() => _done?.Set();

    public bool WaitCopied(TimeSpan max)
    {
        if (_copied is null)
        {
            return false;
        }

        // The tray exiting is as good as an answer: nobody is left to copy.
        if (_trayExit is null)
        {
            return _copied.WaitOne(max);
        }

        return WaitHandle.WaitAny([_copied, _trayExit], max) == 0;
    }

    public void Dispose()
    {
        _stop?.Dispose();
        _done?.Dispose();
        _copied?.Dispose();
        _trayExit?.Dispose();
        _tray?.Dispose();
    }
}

// The tray's side: it creates the events and later sets stop and copied and waits on done.
internal interface ITrayTraceSignals : IDisposable
{
    void RequestStop();

    // True when the helper said done; false when the token was cancelled first.
    bool WaitDone(CancellationToken ct);

    void SetCopied();
}

internal interface ITrayTraceSignalsFactory
{
    ITrayTraceSignals Create(string nonce);
}

internal sealed class TrayTraceSignals : ITrayTraceSignals
{
    private readonly EventWaitHandle _stop;
    private readonly EventWaitHandle _done;
    private readonly EventWaitHandle _copied;

    private TrayTraceSignals(EventWaitHandle stop, EventWaitHandle done, EventWaitHandle copied)
    {
        _stop = stop;
        _done = done;
        _copied = copied;
    }

    public static TrayTraceSignals Create(string nonce)
    {
        EventWaitHandle? stop = null;
        EventWaitHandle? done = null;
        try
        {
            stop = Make(TraceEventNames.Stop(nonce));
            done = Make(TraceEventNames.Done(nonce));
            EventWaitHandle copied = Make(TraceEventNames.Copied(nonce));
            return new TrayTraceSignals(stop, done, copied);
        }
        catch
        {
            stop?.Dispose();
            done?.Dispose();
            throw;
        }
    }

    // https://learn.microsoft.com/en-us/dotnet/api/system.threading.eventwaithandleacl.create
    private static EventWaitHandle Make(string name)
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
        EventWaitHandle handle = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, name, out bool created, security);
        if (!created)
        {
            handle.Dispose();
            throw new InvalidOperationException("The event " + name + " already exists.");
        }

        return handle;
    }

    public void RequestStop() => _stop.Set();

    public bool WaitDone(CancellationToken ct) => WaitHandle.WaitAny([_done, ct.WaitHandle]) == 0;

    public void SetCopied() => _copied.Set();

    public void Dispose()
    {
        _stop.Dispose();
        _done.Dispose();
        _copied.Dispose();
    }
}

internal sealed class TrayTraceSignalsFactory : ITrayTraceSignalsFactory
{
    public ITrayTraceSignals Create(string nonce) => TrayTraceSignals.Create(nonce);
}
