using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Service;

internal enum ServicePresence
{
    Present,
    Missing,

    // The control manager could not say (access denied, or it could not be opened). Never treated as missing.
    Unknown,
}

// What the control manager holds for a service, each field null when its own read failed; Steps say why, with
// the raw Win32 code of every call.
internal sealed record ServiceQuery(
    ServicePresence Presence,
    IReadOnlyList<StepOutcome> Steps,
    uint? State = null,
    uint? ProcessId = null,
    uint? ServiceType = null,
    uint? StartType = null,
    uint? ErrorControl = null,
    string? ImagePath = null,
    string? Account = null,
    string? DisplayName = null,
    uint? PreshutdownTimeoutMs = null,
    string? Sddl = null);

// The service control manager, behind one interface so install, uninstall, the tray's status read and the probe
// run against a fake. Every method returns the Win32 code of its call as a step and never throws for a result the
// control manager gives.
internal interface IServiceControl
{
    // Reads the service with the fewest rights that show it (query, and read control for the access list), which
    // every authenticated user holds.
    ServiceQuery Query(string name);

    // CreateService with the spec's values. An existing service is a failed step (ERROR_SERVICE_EXISTS).
    StepOutcome Create(ServiceSpec spec);

    // ChangeServiceConfig with every value of the spec, so a registration from an earlier install is brought to it.
    StepOutcome Reconfigure(ServiceSpec spec);

    StepOutcome SetDescription(string name, string description);

    StepOutcome SetPreshutdownTimeout(string name, uint milliseconds);

    // Replaces the service's access list with this SDDL.
    StepOutcome SetDacl(string name, string sddl);

    // Ok when it started, or was running already.
    StepOutcome Start(string name);

    // Ok when the stop was accepted, or the service was not running.
    StepOutcome Stop(string name);

    // Ok when the service is marked for deletion (removed when its last handle closes, or at the next restart), or
    // was not there.
    StepOutcome Delete(string name);

    // Polls the state every ServiceSteps.PollInterval until it is the wanted one. wait sleeps between polls and
    // returns false to stop waiting, so a test never sleeps.
    StepOutcome WaitForState(string name, uint state, TimeSpan timeout, Func<TimeSpan, bool> wait);
}

internal static class ServiceSteps
{
    public const string Existing = "service-existing";
    public const string Query = "service-query";
    public const string Create = "service-create";
    public const string Reconfigure = "service-reconfigure";
    public const string Description = "service-description";
    public const string Preshutdown = "service-preshutdown";
    public const string Dacl = "service-dacl";
    public const string Start = "service-start";
    public const string Stop = "service-stop";
    public const string Delete = "service-delete";
    public const string Verify = "service-verify";
    public const string Wait = "service-wait";

    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // The names this file needs that the shared table does not carry. Anything else keeps the shared name.
    public static string Name(uint error) => error switch
    {
        AdvApi32.ERROR_CALL_NOT_IMPLEMENTED => "ERROR_CALL_NOT_IMPLEMENTED",
        AdvApi32.ERROR_SERVICE_REQUEST_TIMEOUT => "ERROR_SERVICE_REQUEST_TIMEOUT",
        AdvApi32.ERROR_SERVICE_ALREADY_RUNNING => "ERROR_SERVICE_ALREADY_RUNNING",
        AdvApi32.ERROR_SERVICE_CANNOT_ACCEPT_CTRL => "ERROR_SERVICE_CANNOT_ACCEPT_CTRL",
        AdvApi32.ERROR_SERVICE_NOT_ACTIVE => "ERROR_SERVICE_NOT_ACTIVE",
        AdvApi32.ERROR_FAILED_SERVICE_CONTROLLER_CONNECT => "ERROR_FAILED_SERVICE_CONTROLLER_CONNECT",
        AdvApi32.ERROR_SERVICE_SPECIFIC_ERROR => "ERROR_SERVICE_SPECIFIC_ERROR",
        AdvApi32.ERROR_SERVICE_MARKED_FOR_DELETE => "ERROR_SERVICE_MARKED_FOR_DELETE",
        AdvApi32.ERROR_SERVICE_EXISTS => "ERROR_SERVICE_EXISTS",
        _ => NativeCodes.Win32(error),
    };

    public static StepOutcome FromWin32(string step, uint error, string? detail = null, bool? ok = null) =>
        new(step, ok ?? error == 0, unchecked((int)error), Name(error), detail);

    public static string StateName(uint? state) => state switch
    {
        AdvApi32.SERVICE_STOPPED => "stopped",
        AdvApi32.SERVICE_START_PENDING => "start pending",
        AdvApi32.SERVICE_STOP_PENDING => "stop pending",
        AdvApi32.SERVICE_RUNNING => "running",
        null => "unknown",
        _ => "state " + state.Value,
    };
}

// The real control manager. Each call opens the manager with the least access it needs and the service with the
// least right it needs, and closes both before it returns.
// https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights
internal sealed unsafe class WindowsServiceControl : IServiceControl
{
    public ServiceQuery Query(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var steps = new List<StepOutcome>();
        using SafeServiceHandle scm = AdvApi32.OpenSCManager(null, null, AdvApi32.SC_MANAGER_CONNECT);
        if (scm.IsInvalid)
        {
            uint error = LastError();
            steps.Add(ServiceSteps.FromWin32(ServiceSteps.Query, error, "The service control manager could not be opened."));
            return new ServiceQuery(ServicePresence.Unknown, steps);
        }

        using SafeServiceHandle service = AdvApi32.OpenService(
            scm, name, AdvApi32.SERVICE_QUERY_CONFIG | AdvApi32.SERVICE_QUERY_STATUS | AdvApi32.READ_CONTROL);
        if (service.IsInvalid)
        {
            uint error = LastError();
            steps.Add(ServiceSteps.FromWin32(ServiceSteps.Query, error,
                error == AdvApi32.ERROR_SERVICE_DOES_NOT_EXIST ? name + " is not registered." : name + " could not be opened."));
            return new ServiceQuery(error == AdvApi32.ERROR_SERVICE_DOES_NOT_EXIST ? ServicePresence.Missing : ServicePresence.Unknown, steps);
        }

        uint? state = null, processId = null, type = null, start = null, errorControl = null, preshutdown = null;
        string? image = null, account = null, display = null, sddl = null;

        byte[] status = new byte[Marshal.SizeOf<SERVICE_STATUS_PROCESS>()];
        if (ReadInto("service-status", steps, status, (byte* b, uint size, out uint needed) => AdvApi32.QueryServiceStatusEx(service, AdvApi32.SC_STATUS_PROCESS_INFO, b, size, out needed), out byte[] statusBytes))
        {
            SERVICE_STATUS_PROCESS process = MemoryMarshal.Read<SERVICE_STATUS_PROCESS>(statusBytes);
            state = process.dwCurrentState;
            processId = process.dwProcessId;
        }

        if (ReadInto("service-config", steps, new byte[1024], (byte* b, uint size, out uint needed) => AdvApi32.QueryServiceConfig(service, b, size, out needed), out byte[] configBytes))
        {
            fixed (byte* p = configBytes)
            {
                QUERY_SERVICE_CONFIGW config = *(QUERY_SERVICE_CONFIGW*)p;
                type = config.dwServiceType;
                start = config.dwStartType;
                errorControl = config.dwErrorControl;
                image = Marshal.PtrToStringUni(config.lpBinaryPathName);
                account = Marshal.PtrToStringUni(config.lpServiceStartName);
                display = Marshal.PtrToStringUni(config.lpDisplayName);
            }
        }

        if (ReadInto("service-preshutdown", steps, new byte[16], (byte* b, uint size, out uint needed) => AdvApi32.QueryServiceConfig2(service, AdvApi32.SERVICE_CONFIG_PRESHUTDOWN_INFO, b, size, out needed), out byte[] preshutdownBytes))
        {
            preshutdown = MemoryMarshal.Read<SERVICE_PRESHUTDOWN_INFO>(preshutdownBytes).dwPreshutdownTimeout;
        }

        if (ReadInto("service-acl", steps, new byte[512], (byte* b, uint size, out uint needed) => AdvApi32.QueryServiceObjectSecurity(service, AdvApi32.DACL_SECURITY_INFORMATION, b, size, out needed), out byte[] aclBytes))
        {
            sddl = SddlOf(aclBytes, steps);
        }

        return new ServiceQuery(ServicePresence.Present, steps, state, processId, type, start, errorControl, image, account, display, preshutdown, sddl);
    }

    public StepOutcome Create(ServiceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        using SafeServiceHandle scm = AdvApi32.OpenSCManager(null, null, AdvApi32.SC_MANAGER_CREATE_SERVICE);
        if (scm.IsInvalid)
        {
            return ServiceSteps.FromWin32(ServiceSteps.Create, LastError(), "The service control manager could not be opened for creating a service.");
        }

        // No account name and no password: the service runs as Local System.
        using SafeServiceHandle service = AdvApi32.CreateService(
            scm, spec.Name, spec.DisplayName, AdvApi32.SERVICE_CHANGE_CONFIG, spec.ServiceType, spec.StartType, spec.ErrorControl,
            spec.ImagePath, null, 0, null, null, null);
        return service.IsInvalid
            ? ServiceSteps.FromWin32(ServiceSteps.Create, LastError(), spec.Name + " was not created.")
            : ServiceSteps.FromWin32(ServiceSteps.Create, 0, spec.Name + " " + spec.ImagePath);
    }

    public StepOutcome Reconfigure(ServiceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        (SafeServiceHandle? scm, SafeServiceHandle? service, StepOutcome? failed) = Open(spec.Name, AdvApi32.SERVICE_CHANGE_CONFIG, ServiceSteps.Reconfigure);
        using (scm)
        using (service)
        {
            if (failed is not null)
            {
                return failed;
            }

            // An empty dependency list and load order group clear what an earlier registration may have set; Local
            // System is named with an empty password, which it takes.
            bool changed = AdvApi32.ChangeServiceConfig(
                service!, spec.ServiceType, spec.StartType, spec.ErrorControl, spec.ImagePath, "", 0, "", spec.Account, "", spec.DisplayName);
            return changed
                ? ServiceSteps.FromWin32(ServiceSteps.Reconfigure, 0, spec.Name + " " + spec.ImagePath)
                : ServiceSteps.FromWin32(ServiceSteps.Reconfigure, LastError(), spec.Name + " was not reconfigured.");
        }
    }

    public StepOutcome SetDescription(string name, string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        (SafeServiceHandle? scm, SafeServiceHandle? service, StepOutcome? failed) = Open(name, AdvApi32.SERVICE_CHANGE_CONFIG, ServiceSteps.Description);
        using (scm)
        using (service)
        {
            if (failed is not null)
            {
                return failed;
            }

            fixed (char* text = description)
            {
                var info = new SERVICE_DESCRIPTIONW { lpDescription = (nint)text };
                return AdvApi32.ChangeServiceConfig2(service!, AdvApi32.SERVICE_CONFIG_DESCRIPTION, &info)
                    ? ServiceSteps.FromWin32(ServiceSteps.Description, 0, description)
                    : ServiceSteps.FromWin32(ServiceSteps.Description, LastError(), "The description was not set.");
            }
        }
    }

    public StepOutcome SetPreshutdownTimeout(string name, uint milliseconds)
    {
        (SafeServiceHandle? scm, SafeServiceHandle? service, StepOutcome? failed) = Open(name, AdvApi32.SERVICE_CHANGE_CONFIG, ServiceSteps.Preshutdown);
        using (scm)
        using (service)
        {
            if (failed is not null)
            {
                return failed;
            }

            var info = new SERVICE_PRESHUTDOWN_INFO { dwPreshutdownTimeout = milliseconds };
            return AdvApi32.ChangeServiceConfig2(service!, AdvApi32.SERVICE_CONFIG_PRESHUTDOWN_INFO, &info)
                ? ServiceSteps.FromWin32(ServiceSteps.Preshutdown, 0, milliseconds + " ms")
                : ServiceSteps.FromWin32(ServiceSteps.Preshutdown, LastError(), "The pre-shutdown time-out was not set.");
        }
    }

    public StepOutcome SetDacl(string name, string sddl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sddl);
        byte[] descriptor;
        try
        {
            var parsed = new RawSecurityDescriptor(sddl);
            descriptor = new byte[parsed.BinaryLength];
            parsed.GetBinaryForm(descriptor, 0);
        }
        catch (ArgumentException ex)
        {
            return StepOutcomes.FromHResult(ServiceSteps.Dacl, ex.HResult, "The access list does not parse: " + ex.Message);
        }

        (SafeServiceHandle? scm, SafeServiceHandle? service, StepOutcome? failed) = Open(name, AdvApi32.WRITE_DAC, ServiceSteps.Dacl);
        using (scm)
        using (service)
        {
            if (failed is not null)
            {
                return failed;
            }

            fixed (byte* p = descriptor)
            {
                return AdvApi32.SetServiceObjectSecurity(service!, AdvApi32.DACL_SECURITY_INFORMATION, p)
                    ? ServiceSteps.FromWin32(ServiceSteps.Dacl, 0, sddl)
                    : ServiceSteps.FromWin32(ServiceSteps.Dacl, LastError(), "The access list was not applied.");
            }
        }
    }

    public StepOutcome Start(string name)
    {
        (SafeServiceHandle? scm, SafeServiceHandle? service, StepOutcome? failed) = Open(name, AdvApi32.SERVICE_START, ServiceSteps.Start);
        using (scm)
        using (service)
        {
            if (failed is not null)
            {
                return failed;
            }

            if (AdvApi32.StartService(service!, 0, 0))
            {
                return ServiceSteps.FromWin32(ServiceSteps.Start, 0, name + " start was accepted.");
            }

            uint error = LastError();
            return ServiceSteps.FromWin32(ServiceSteps.Start, error,
                error == AdvApi32.ERROR_SERVICE_ALREADY_RUNNING ? name + " was running already." : name + " was not started.",
                ok: error == AdvApi32.ERROR_SERVICE_ALREADY_RUNNING);
        }
    }

    public StepOutcome Stop(string name)
    {
        (SafeServiceHandle? scm, SafeServiceHandle? service, StepOutcome? failed) = Open(name, AdvApi32.SERVICE_STOP, ServiceSteps.Stop);
        using (scm)
        using (service)
        {
            if (failed is not null)
            {
                return failed;
            }

            if (AdvApi32.ControlService(service!, AdvApi32.SERVICE_CONTROL_STOP, out _))
            {
                return ServiceSteps.FromWin32(ServiceSteps.Stop, 0, name + " stop was accepted.");
            }

            uint error = LastError();
            return ServiceSteps.FromWin32(ServiceSteps.Stop, error,
                error == AdvApi32.ERROR_SERVICE_NOT_ACTIVE ? name + " was not running." : name + " was not stopped.",
                ok: error == AdvApi32.ERROR_SERVICE_NOT_ACTIVE);
        }
    }

    public StepOutcome Delete(string name)
    {
        (SafeServiceHandle? scm, SafeServiceHandle? service, StepOutcome? failed) = Open(name, AdvApi32.DELETE, ServiceSteps.Delete);
        using (scm)
        using (service)
        {
            if (failed is not null)
            {
                // A service that is not there is gone.
                return failed.Code == (int)AdvApi32.ERROR_SERVICE_DOES_NOT_EXIST ? failed with { Ok = true } : failed;
            }

            if (AdvApi32.DeleteService(service!))
            {
                return ServiceSteps.FromWin32(ServiceSteps.Delete, 0, name + " is deleted.");
            }

            uint error = LastError();
            bool gone = error == AdvApi32.ERROR_SERVICE_MARKED_FOR_DELETE;
            return ServiceSteps.FromWin32(ServiceSteps.Delete, error,
                gone ? name + " was already marked for deletion." : name + " was not deleted.", ok: gone);
        }
    }

    public StepOutcome WaitForState(string name, uint state, TimeSpan timeout, Func<TimeSpan, bool> wait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(wait);
        long polls = timeout <= TimeSpan.Zero ? 0 : (long)Math.Ceiling(timeout / ServiceSteps.PollInterval);
        uint? last = null;
        for (long waited = 0; ; waited++)
        {
            ServiceQuery read = Query(name);
            last = read.State;
            if (read.State == state)
            {
                return ServiceSteps.FromWin32(ServiceSteps.Wait, 0, name + " is " + ServiceSteps.StateName(state) + ".");
            }

            if (read.Presence == ServicePresence.Missing && state == AdvApi32.SERVICE_STOPPED)
            {
                return ServiceSteps.FromWin32(ServiceSteps.Wait, 0, name + " is not registered.");
            }

            if (waited >= polls || !wait(ServiceSteps.PollInterval))
            {
                return ServiceSteps.FromWin32(ServiceSteps.Wait, AdvApi32.ERROR_SERVICE_REQUEST_TIMEOUT,
                    name + " did not reach the " + ServiceSteps.StateName(state) + " state within " +
                    timeout.TotalSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " s; it is " + ServiceSteps.StateName(last) + ".",
                    ok: false);
            }
        }
    }

    private static uint LastError() => unchecked((uint)Marshal.GetLastPInvokeError());

    // The manager and the service opened with the given rights. On a failure the step says so and both handles are
    // still returned for disposal.
    private static (SafeServiceHandle? Scm, SafeServiceHandle? Service, StepOutcome? Failed) Open(string name, uint serviceAccess, string step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        SafeServiceHandle scm = AdvApi32.OpenSCManager(null, null, AdvApi32.SC_MANAGER_CONNECT);
        if (scm.IsInvalid)
        {
            return (scm, null, ServiceSteps.FromWin32(step, LastError(), "The service control manager could not be opened."));
        }

        SafeServiceHandle service = AdvApi32.OpenService(scm, name, serviceAccess);
        return service.IsInvalid
            ? (scm, service, ServiceSteps.FromWin32(step, LastError(), name + " could not be opened."))
            : (scm, service, null);
    }

    // Calls a query that fills a buffer, growing the buffer once to the size the call reports. False, with the
    // failed step recorded, when the read failed.
    private delegate bool BufferQuery(byte* buffer, uint size, out uint needed);

    private static bool ReadInto(string step, List<StepOutcome> steps, byte[] initial, BufferQuery query, out byte[] result)
    {
        byte[] buffer = initial;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool ok;
            uint needed;
            fixed (byte* p = buffer)
            {
                ok = query(p, (uint)buffer.Length, out needed);
            }

            if (ok)
            {
                steps.Add(ServiceSteps.FromWin32(step, 0));
                result = buffer;
                return true;
            }

            uint error = LastError();
            if (error == AdvApi32.ERROR_INSUFFICIENT_BUFFER && needed > buffer.Length && attempt == 0)
            {
                buffer = new byte[needed];
                continue;
            }

            steps.Add(ServiceSteps.FromWin32(step, error));
            break;
        }

        result = [];
        return false;
    }

    // The access list as SDDL, or null with the failure recorded.
    private static string? SddlOf(byte[] descriptor, List<StepOutcome> steps)
    {
        try
        {
            return new RawSecurityDescriptor(descriptor, 0).GetSddlForm(AccessControlSections.Access);
        }
        catch (ArgumentException ex)
        {
            steps.Add(StepOutcomes.FromHResult("service-acl-parse", ex.HResult, ex.Message));
            return null;
        }
    }
}
