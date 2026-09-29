using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Earshot.Boot;
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
    string? Sddl = null)
{
    // The optional settings the plan leaves at their defaults. Each is null when its own read failed.
    // FailureActionCount and FailureCommand: what the control manager does when the service fails. DelayedAutoStart:
    // whether an automatic start waits until the system has settled. TriggerCount: events that start or stop the service.
    // RequiredPrivileges: the privileges the service's token is cut down to (empty when it keeps all it is given).
    // ServiceSidType: whether the service has a security identifier of its own (0 none, 1 unrestricted, 3 restricted).
    public uint? FailureActionCount { get; init; }

    public string? FailureCommand { get; init; }

    public bool? DelayedAutoStart { get; init; }

    public uint? TriggerCount { get; init; }

    public IReadOnlyList<string>? RequiredPrivileges { get; init; }

    public uint? ServiceSidType { get; init; }
}

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

    // Polls until the process that ran the service, by the id read before it was stopped, has gone. A service reports
    // "stopped" before its process has exited, and a folder holding a running image cannot be moved or deleted.
    StepOutcome WaitForProcessExit(uint processId, TimeSpan timeout, Func<TimeSpan, bool> wait);

    // Remove every failure action, and the failure command, so nothing is run or restarted when the service fails.
    StepOutcome ClearFailureActions(string name);

    StepOutcome SetDelayedAutoStart(string name, bool delayed);

    // 0 is no service security identifier.
    StepOutcome SetServiceSidType(string name, uint type);

    // Remove every trigger. Only for a service that has some: with none, the control manager refuses the call.
    StepOutcome ClearTriggers(string name);
}

// The optional-setting levels of QueryServiceConfig2W and ChangeServiceConfig2W that AdvApi32 does not name.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-changeserviceconfig2w
internal static class ServiceConfigLevels
{
    internal const uint FailureActions = 2;
    internal const uint DelayedAutoStart = 3;
    internal const uint ServiceSid = 5;
    internal const uint RequiredPrivileges = 6;
    internal const uint Trigger = 8;
}

// SERVICE_FAILURE_ACTIONSW: the reset period, two strings, the count and the array of actions.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_failure_actionsw
[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_FAILURE_ACTIONSW
{
    public uint dwResetPeriod;
    public nint lpRebootMsg;
    public nint lpCommand;
    public uint cActions;
    public nint lpsaActions;
}

// SC_ACTION: what to do (0 is nothing) and the delay before it, in milliseconds.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-sc_action
[StructLayout(LayoutKind.Sequential)]
internal struct SC_ACTION
{
    public uint Type;
    public uint Delay;
}

// SERVICE_TRIGGER_INFO: the count, the array and a reserved pointer that must be null.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_trigger_info
[StructLayout(LayoutKind.Sequential)]
internal struct SERVICE_TRIGGER_INFO
{
    public uint cTriggers;
    public nint pTriggers;
    public nint pReserved;
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
    public const string ProcessExit = "service-process-exit";
    public const string FailureActions = "service-failure-actions";
    public const string DelayedStart = "service-delayed-start";
    public const string SidType = "service-sid-type";
    public const string Triggers = "service-triggers";

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

        using (NativeBuffer? status = ReadInto("service-status", steps, (uint)sizeof(SERVICE_STATUS_PROCESS),
                   (byte* b, uint size, out uint needed) => AdvApi32.QueryServiceStatusEx(service, AdvApi32.SC_STATUS_PROCESS_INFO, b, size, out needed)))
        {
            if (status is not null)
            {
                SERVICE_STATUS_PROCESS process = *(SERVICE_STATUS_PROCESS*)status.Pointer;
                state = process.dwCurrentState;
                processId = process.dwProcessId;
            }
        }

        // QueryServiceConfigW writes pointers to its strings into the buffer it is given, so the buffer stays where it is,
        // and alive, until the strings have been read out of it: native memory, freed after the reads.
        using (NativeBuffer? configBuffer = ReadInto("service-config", steps, 1024,
                   (byte* b, uint size, out uint needed) => AdvApi32.QueryServiceConfig(service, b, size, out needed)))
        {
            if (configBuffer is not null)
            {
                QUERY_SERVICE_CONFIGW config = *(QUERY_SERVICE_CONFIGW*)configBuffer.Pointer;
                type = config.dwServiceType;
                start = config.dwStartType;
                errorControl = config.dwErrorControl;
                image = Marshal.PtrToStringUni(config.lpBinaryPathName);
                account = Marshal.PtrToStringUni(config.lpServiceStartName);
                display = Marshal.PtrToStringUni(config.lpDisplayName);
            }
        }

        using (NativeBuffer? shutdown = ReadConfig2("service-preshutdown", steps, service, AdvApi32.SERVICE_CONFIG_PRESHUTDOWN_INFO, (uint)sizeof(SERVICE_PRESHUTDOWN_INFO)))
        {
            if (shutdown is not null)
            {
                preshutdown = ((SERVICE_PRESHUTDOWN_INFO*)shutdown.Pointer)->dwPreshutdownTimeout;
            }
        }

        uint? failureActionCount = null, sidType = null, triggerCount = null;
        string? failureCommand = null;
        bool? delayed = null;
        IReadOnlyList<string>? privileges = null;

        using (NativeBuffer? failure = ReadConfig2("service-failure-actions", steps, service, ServiceConfigLevels.FailureActions, (uint)sizeof(SERVICE_FAILURE_ACTIONSW)))
        {
            if (failure is not null)
            {
                SERVICE_FAILURE_ACTIONSW actions = *(SERVICE_FAILURE_ACTIONSW*)failure.Pointer;
                failureActionCount = actions.cActions;
                failureCommand = Marshal.PtrToStringUni(actions.lpCommand);
            }
        }

        using (NativeBuffer? delay = ReadConfig2("service-delayed-start", steps, service, ServiceConfigLevels.DelayedAutoStart, sizeof(uint)))
        {
            if (delay is not null)
            {
                delayed = *(uint*)delay.Pointer != 0;
            }
        }

        using (NativeBuffer? triggers = ReadConfig2("service-triggers", steps, service, ServiceConfigLevels.Trigger, (uint)sizeof(SERVICE_TRIGGER_INFO)))
        {
            if (triggers is not null)
            {
                triggerCount = ((SERVICE_TRIGGER_INFO*)triggers.Pointer)->cTriggers;
            }
        }

        using (NativeBuffer? required = ReadConfig2("service-privileges", steps, service, ServiceConfigLevels.RequiredPrivileges, (uint)sizeof(nint)))
        {
            if (required is not null)
            {
                privileges = ReadMultiString(*(nint*)required.Pointer);
            }
        }

        using (NativeBuffer? sid = ReadConfig2("service-sid-type", steps, service, ServiceConfigLevels.ServiceSid, sizeof(uint)))
        {
            if (sid is not null)
            {
                sidType = *(uint*)sid.Pointer;
            }
        }

        using (NativeBuffer? acl = ReadInto("service-acl", steps, 512,
                   (byte* b, uint size, out uint needed) => AdvApi32.QueryServiceObjectSecurity(service, AdvApi32.DACL_SECURITY_INFORMATION, b, size, out needed)))
        {
            if (acl is not null)
            {
                sddl = SddlOf(new ReadOnlySpan<byte>(acl.Pointer, (int)acl.Size).ToArray(), steps);
            }
        }

        return new ServiceQuery(ServicePresence.Present, steps, state, processId, type, start, errorControl, image, account, display, preshutdown, sddl)
        {
            FailureActionCount = failureActionCount,
            FailureCommand = failureCommand,
            DelayedAutoStart = delayed,
            TriggerCount = triggerCount,
            RequiredPrivileges = privileges,
            ServiceSidType = sidType,
        };
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

    public StepOutcome WaitForProcessExit(uint processId, TimeSpan timeout, Func<TimeSpan, bool> wait)
    {
        ArgumentNullException.ThrowIfNull(wait);
        if (processId == 0)
        {
            return ServiceSteps.FromWin32(ServiceSteps.ProcessExit, 0, "The service had no process to wait for.");
        }

        long polls = timeout <= TimeSpan.Zero ? 0 : (long)Math.Ceiling(timeout / ServiceSteps.PollInterval);
        for (long waited = 0; ; waited++)
        {
            StepOutcome? failed = ProcessIsRunning(processId, out bool running);
            if (failed is not null)
            {
                return failed;
            }

            if (!running)
            {
                return ServiceSteps.FromWin32(ServiceSteps.ProcessExit, 0, "Process " + processId + " has exited.");
            }

            if (waited >= polls || !wait(ServiceSteps.PollInterval))
            {
                return ServiceSteps.FromWin32(ServiceSteps.ProcessExit, AdvApi32.ERROR_SERVICE_REQUEST_TIMEOUT,
                    "Process " + processId + " had not exited after " +
                    timeout.TotalSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " s.", ok: false);
            }
        }
    }

    // Whether a process with this id and the service program's name is running. A process of another name is not the
    // service: its id was given to something else after the service ended. The list of processes is read from the
    // system's own snapshot, which needs no right on any process.
    // https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.getprocessesbyname
    private static StepOutcome? ProcessIsRunning(uint processId, out bool running)
    {
        running = false;
        Process[] candidates = [];
        try
        {
            candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(TaskPlan.ExecutableName));
            running = candidates.Any(p => p.Id == processId);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            uint code = ex is System.ComponentModel.Win32Exception native ? unchecked((uint)native.NativeErrorCode) : unchecked((uint)ex.HResult);
            return ServiceSteps.FromWin32(ServiceSteps.ProcessExit, code, "The list of processes could not be read: " + ex.Message, ok: false);
        }
        finally
        {
            foreach (Process candidate in candidates)
            {
                candidate.Dispose();
            }
        }
    }

    public StepOutcome ClearFailureActions(string name)
    {
        (SafeServiceHandle? scm, SafeServiceHandle? service, StepOutcome? failed) = Open(name, AdvApi32.SERVICE_CHANGE_CONFIG, ServiceSteps.FailureActions);
        using (scm)
        using (service)
        {
            if (failed is not null)
            {
                return failed;
            }

            // A count of zero with an array that is not null deletes the actions and the reset period, and an empty string
            // deletes the command and the reboot message, where a null leaves each as it is.
            fixed (char* empty = "")
            {
                var none = new SC_ACTION { Type = 0, Delay = 0 };
                var info = new SERVICE_FAILURE_ACTIONSW
                {
                    dwResetPeriod = 0, lpRebootMsg = (nint)empty, lpCommand = (nint)empty, cActions = 0, lpsaActions = (nint)(&none),
                };
                return AdvApi32.ChangeServiceConfig2(service!, ServiceConfigLevels.FailureActions, &info)
                    ? ServiceSteps.FromWin32(ServiceSteps.FailureActions, 0, "No failure actions.")
                    : ServiceSteps.FromWin32(ServiceSteps.FailureActions, LastError(), "The failure actions were not cleared.");
            }
        }
    }

    public StepOutcome SetDelayedAutoStart(string name, bool delayed)
    {
        (SafeServiceHandle? scm, SafeServiceHandle? service, StepOutcome? failed) = Open(name, AdvApi32.SERVICE_CHANGE_CONFIG, ServiceSteps.DelayedStart);
        using (scm)
        using (service)
        {
            if (failed is not null)
            {
                return failed;
            }

            uint value = delayed ? 1u : 0u;
            return AdvApi32.ChangeServiceConfig2(service!, ServiceConfigLevels.DelayedAutoStart, &value)
                ? ServiceSteps.FromWin32(ServiceSteps.DelayedStart, 0, delayed ? "Delayed." : "Not delayed.")
                : ServiceSteps.FromWin32(ServiceSteps.DelayedStart, LastError(), "The delayed start setting was not changed.");
        }
    }

    public StepOutcome SetServiceSidType(string name, uint type)
    {
        (SafeServiceHandle? scm, SafeServiceHandle? service, StepOutcome? failed) = Open(name, AdvApi32.SERVICE_CHANGE_CONFIG, ServiceSteps.SidType);
        using (scm)
        using (service)
        {
            if (failed is not null)
            {
                return failed;
            }

            return AdvApi32.ChangeServiceConfig2(service!, ServiceConfigLevels.ServiceSid, &type)
                ? ServiceSteps.FromWin32(ServiceSteps.SidType, 0, "Service security identifier type " + type + ".")
                : ServiceSteps.FromWin32(ServiceSteps.SidType, LastError(), "The service security identifier type was not changed.");
        }
    }

    public StepOutcome ClearTriggers(string name)
    {
        (SafeServiceHandle? scm, SafeServiceHandle? service, StepOutcome? failed) = Open(name, AdvApi32.SERVICE_CHANGE_CONFIG, ServiceSteps.Triggers);
        using (scm)
        using (service)
        {
            if (failed is not null)
            {
                return failed;
            }

            var info = new SERVICE_TRIGGER_INFO { cTriggers = 0, pTriggers = 0, pReserved = 0 };
            return AdvApi32.ChangeServiceConfig2(service!, ServiceConfigLevels.Trigger, &info)
                ? ServiceSteps.FromWin32(ServiceSteps.Triggers, 0, "No triggers.")
                : ServiceSteps.FromWin32(ServiceSteps.Triggers, LastError(), "The triggers were not cleared.");
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

    // Calls a query that fills a buffer, growing the buffer once to the size the call reports. Null, with the failed step
    // recorded, when the read failed; otherwise the caller owns the buffer and frees it after reading from it.
    private delegate bool BufferQuery(byte* buffer, uint size, out uint needed);

    private static NativeBuffer? ReadInto(string step, List<StepOutcome> steps, uint initialSize, BufferQuery query)
    {
        var buffer = new NativeBuffer(initialSize);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool ok = query(buffer.Pointer, buffer.Size, out uint needed);
            if (ok)
            {
                steps.Add(ServiceSteps.FromWin32(step, 0));
                return buffer;
            }

            uint error = LastError();
            if (error == AdvApi32.ERROR_INSUFFICIENT_BUFFER && needed > buffer.Size && attempt == 0)
            {
                buffer.Dispose();
                buffer = new NativeBuffer(needed);
                continue;
            }

            steps.Add(ServiceSteps.FromWin32(step, error));
            break;
        }

        buffer.Dispose();
        return null;
    }

    private static NativeBuffer? ReadConfig2(string step, List<StepOutcome> steps, SafeServiceHandle service, uint level, uint initialSize) =>
        ReadInto(step, steps, initialSize, (byte* b, uint size, out uint needed) => AdvApi32.QueryServiceConfig2(service, level, b, size, out needed));

    // A multi-string is null-terminated strings ended by an empty one. A null pointer is the empty list.
    private static List<string> ReadMultiString(nint pointer)
    {
        var found = new List<string>();
        if (pointer == 0)
        {
            return found;
        }

        for (nint at = pointer; ;)
        {
            string? text = Marshal.PtrToStringUni(at);
            if (string.IsNullOrEmpty(text))
            {
                return found;
            }

            found.Add(text);
            at += (text.Length + 1) * sizeof(char);
        }
    }

    // Native memory a query fills in. QueryServiceConfigW and its siblings put absolute pointers to strings into the
    // buffer they are given, and a managed array can be moved by the garbage collector between the call and the reads of
    // those strings, which leaves the pointers dangling. Native memory never moves.
    private sealed class NativeBuffer : IDisposable
    {
        public NativeBuffer(uint size)
        {
            Size = size == 0 ? 1 : size;
            Pointer = (byte*)NativeMemory.AllocZeroed(Size);
        }

        public byte* Pointer { get; private set; }

        public uint Size { get; }

        public void Dispose()
        {
            if (Pointer != null)
            {
                NativeMemory.Free(Pointer);
                Pointer = null;
            }
        }
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
