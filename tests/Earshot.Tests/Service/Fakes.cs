using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Service;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Service;

// A service control manager in memory. It holds one service the way the real one would after each call, records every
// call in order, and lets a test make one call fail or leave the service in a state the real manager could.
internal sealed class FakeServiceControl : IServiceControl
{
    private readonly Dictionary<string, uint> _failures = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = new();

    public ServiceSpec? Created { get; private set; }

    public List<ServiceSpec> Reconfigured { get; } = new();

    public bool Exists { get; set; }

    public uint State { get; set; } = AdvApi32.SERVICE_STOPPED;

    // What a start leaves the service in, and a stop.
    public uint StateAfterStart { get; set; } = AdvApi32.SERVICE_RUNNING;

    public uint StateAfterStop { get; set; } = AdvApi32.SERVICE_STOPPED;

    // What the read-back reports for these fields; null means the value the spec set.
    public uint? ReadStartType { get; set; }

    public string? ReadImagePath { get; set; }

    public uint? ReadPreshutdown { get; set; }

    public string? ReadSddl { get; set; }

    public bool SddlUnreadable { get; set; }

    // The process id the running service has, and whether that process is gone once the service is stopped.
    public uint ProcessId { get; set; }

    public bool ProcessExitsAfterStop { get; set; } = true;

    // The optional settings a registration holds. A registration from an earlier install may have any of them; the fresh one
    // has none.
    public uint FailureActionCount { get; set; }

    public string? FailureCommand { get; set; }

    public bool DelayedAutoStart { get; set; }

    public uint TriggerCount { get; set; }

    public uint ServiceSidType { get; set; }

    public IReadOnlyList<string> RequiredPrivileges { get; set; } = [];

    // Set by a stop, when the fake's process is to be gone afterwards.
    private bool _processGone;

    // The control manager cannot be read at all (access denied): the service is neither there nor not there.
    public bool Unreadable { get; set; }

    // The control manager becomes unreadable after this many queries (the first ones answer as usual).
    public int? UnreadableAfterQueries { get; set; }

    private int _queries;

    public string? Description { get; private set; }

    public uint? Preshutdown { get; private set; }

    public string? Sddl { get; private set; }

    // The state a service that is there already has before install touches it.
    public void Install(uint state, ServiceSpec spec)
    {
        Exists = true;
        State = state;
        Created = spec;
        Preshutdown = spec.PreshutdownTimeoutMs;
        Sddl = spec.Sddl;
        ProcessId = 4321;
        _processGone = false;
    }

    // The next call to this operation ("create", "start", "stop", "delete", "reconfigure", "description", "preshutdown",
    // "dacl", "failure-actions", "delayed", "sid", "triggers") returns this Win32 error.
    public void Fail(string operation, uint error) => _failures[operation] = error;

    private bool Failed(string operation, out uint error) => _failures.TryGetValue(operation, out error);

    public ServiceQuery Query(string name)
    {
        Calls.Add("query");
        _queries++;
        if (Unreadable || _queries > UnreadableAfterQueries)
        {
            return new ServiceQuery(
                ServicePresence.Unknown, [ServiceSteps.FromWin32(ServiceSteps.Query, 5, "The service control manager could not be opened.")]);
        }

        if (!Exists || Created is null)
        {
            return new ServiceQuery(
                ServicePresence.Missing,
                [ServiceSteps.FromWin32(ServiceSteps.Query, AdvApi32.ERROR_SERVICE_DOES_NOT_EXIST, name + " is not registered.")]);
        }

        ServiceSpec spec = Created;
        return new ServiceQuery(
            ServicePresence.Present, [ServiceSteps.FromWin32(ServiceSteps.Query, 0)], State, State == AdvApi32.SERVICE_STOPPED ? 0 : ProcessId,
            spec.ServiceType, ReadStartType ?? spec.StartType, spec.ErrorControl, ReadImagePath ?? spec.ImagePath, spec.Account,
            spec.DisplayName, ReadPreshutdown ?? Preshutdown, SddlUnreadable ? null : ReadSddl ?? Sddl)
        {
            FailureActionCount = FailureActionCount,
            FailureCommand = FailureCommand,
            DelayedAutoStart = DelayedAutoStart,
            TriggerCount = TriggerCount,
            RequiredPrivileges = RequiredPrivileges,
            ServiceSidType = ServiceSidType,
        };
    }

    public StepOutcome Create(ServiceSpec spec)
    {
        Calls.Add("create");
        if (Failed("create", out uint error))
        {
            return ServiceSteps.FromWin32(ServiceSteps.Create, error, "failed");
        }

        if (Exists)
        {
            return ServiceSteps.FromWin32(ServiceSteps.Create, AdvApi32.ERROR_SERVICE_EXISTS, "exists");
        }

        Exists = true;
        Created = spec;
        Preshutdown = null;
        Sddl = null;
        return ServiceSteps.FromWin32(ServiceSteps.Create, 0, spec.Name);
    }

    public StepOutcome Reconfigure(ServiceSpec spec)
    {
        Calls.Add("reconfigure");
        if (Failed("reconfigure", out uint error))
        {
            return ServiceSteps.FromWin32(ServiceSteps.Reconfigure, error, "failed");
        }

        Created = spec;
        Reconfigured.Add(spec);
        return ServiceSteps.FromWin32(ServiceSteps.Reconfigure, 0, spec.Name);
    }

    public StepOutcome SetDescription(string name, string description)
    {
        Calls.Add("description");
        if (Failed("description", out uint error))
        {
            return ServiceSteps.FromWin32(ServiceSteps.Description, error, "failed");
        }

        Description = description;
        return ServiceSteps.FromWin32(ServiceSteps.Description, 0);
    }

    public StepOutcome SetPreshutdownTimeout(string name, uint milliseconds)
    {
        Calls.Add("preshutdown:" + milliseconds);
        if (Failed("preshutdown", out uint error))
        {
            return ServiceSteps.FromWin32(ServiceSteps.Preshutdown, error, "failed");
        }

        Preshutdown = milliseconds;
        return ServiceSteps.FromWin32(ServiceSteps.Preshutdown, 0);
    }

    public StepOutcome SetDacl(string name, string sddl)
    {
        Calls.Add("dacl:" + sddl);
        if (Failed("dacl", out uint error))
        {
            return ServiceSteps.FromWin32(ServiceSteps.Dacl, error, "failed");
        }

        Sddl = sddl;
        return ServiceSteps.FromWin32(ServiceSteps.Dacl, 0);
    }

    public StepOutcome Start(string name)
    {
        Calls.Add("start");
        if (Failed("start", out uint error))
        {
            return ServiceSteps.FromWin32(ServiceSteps.Start, error, "failed");
        }

        State = StateAfterStart;
        return ServiceSteps.FromWin32(ServiceSteps.Start, 0);
    }

    public StepOutcome Stop(string name)
    {
        Calls.Add("stop");
        if (Failed("stop", out uint error))
        {
            return ServiceSteps.FromWin32(ServiceSteps.Stop, error, "failed");
        }

        State = StateAfterStop;
        _processGone = ProcessExitsAfterStop;
        return ServiceSteps.FromWin32(ServiceSteps.Stop, 0);
    }

    public StepOutcome Delete(string name)
    {
        Calls.Add("delete");
        if (Failed("delete", out uint error))
        {
            bool gone = error == AdvApi32.ERROR_SERVICE_MARKED_FOR_DELETE;
            if (gone)
            {
                Exists = false;
            }

            return ServiceSteps.FromWin32(ServiceSteps.Delete, error, "failed", ok: gone);
        }

        Exists = false;
        return ServiceSteps.FromWin32(ServiceSteps.Delete, 0);
    }

    public StepOutcome WaitForProcessExit(uint processId, TimeSpan timeout, Func<TimeSpan, bool> wait)
    {
        Calls.Add("wait-exit:" + processId);
        return _processGone
            ? ServiceSteps.FromWin32(ServiceSteps.ProcessExit, 0, "Process " + processId + " has exited.")
            : ServiceSteps.FromWin32(ServiceSteps.ProcessExit, AdvApi32.ERROR_SERVICE_REQUEST_TIMEOUT, "Process " + processId + " had not exited.", ok: false);
    }

    public StepOutcome ClearFailureActions(string name)
    {
        Calls.Add("clear-failure-actions");
        if (Failed("failure-actions", out uint error))
        {
            return ServiceSteps.FromWin32(ServiceSteps.FailureActions, error, "failed");
        }

        FailureActionCount = 0;
        FailureCommand = null;
        return ServiceSteps.FromWin32(ServiceSteps.FailureActions, 0);
    }

    public StepOutcome SetDelayedAutoStart(string name, bool delayed)
    {
        Calls.Add("delayed:" + delayed);
        if (Failed("delayed", out uint error))
        {
            return ServiceSteps.FromWin32(ServiceSteps.DelayedStart, error, "failed");
        }

        DelayedAutoStart = delayed;
        return ServiceSteps.FromWin32(ServiceSteps.DelayedStart, 0);
    }

    public StepOutcome SetServiceSidType(string name, uint type)
    {
        Calls.Add("sid:" + type);
        if (Failed("sid", out uint error))
        {
            return ServiceSteps.FromWin32(ServiceSteps.SidType, error, "failed");
        }

        ServiceSidType = type;
        return ServiceSteps.FromWin32(ServiceSteps.SidType, 0);
    }

    public StepOutcome ClearTriggers(string name)
    {
        Calls.Add("clear-triggers");
        if (Failed("triggers", out uint error))
        {
            return ServiceSteps.FromWin32(ServiceSteps.Triggers, error, "failed");
        }

        TriggerCount = 0;
        return ServiceSteps.FromWin32(ServiceSteps.Triggers, 0);
    }

    public StepOutcome WaitForState(string name, uint state, TimeSpan timeout, Func<TimeSpan, bool> wait)
    {
        Calls.Add("wait:" + ServiceSteps.StateName(state));
        return State == state
            ? ServiceSteps.FromWin32(ServiceSteps.Wait, 0, name + " is " + ServiceSteps.StateName(state) + ".")
            : ServiceSteps.FromWin32(ServiceSteps.Wait, AdvApi32.ERROR_SERVICE_REQUEST_TIMEOUT,
                name + " did not reach " + ServiceSteps.StateName(state) + ".", ok: false);
    }
}

// The dispatcher and the status handle in memory. RunDispatcher either fails at once with a Win32 error (a process the
// manager did not start) or runs the service's main function on its own thread, as the real dispatcher does, and
// returns when that function has returned. A test then plays the control manager: it sends controls with Send and reads
// what the service reported.
internal sealed class FakeServiceHost : IServiceHost
{
    private readonly object _lock = new();
    private readonly List<ServiceStatusReport> _reports = new();
    private readonly List<string> _events = new();
    private Func<uint, uint, nint, uint>? _handler;

    // Returned by RunDispatcher without calling the service's main function, when not zero.
    public uint DispatcherError { get; set; }

    public uint RegisterError { get; set; }

    // Every status report fails with this Win32 error, when not zero.
    public uint SetStatusError { get; set; }

    public string? DispatchedName { get; private set; }

    public nint Handle { get; } = 0x1234;

    public IReadOnlyList<ServiceStatusReport> Reports
    {
        get { lock (_lock) { return _reports.ToArray(); } }
    }

    // What the host was asked, in order: "dispatch", "register", "status:<state>".
    public IReadOnlyList<string> Events
    {
        get { lock (_lock) { return _events.ToArray(); } }
    }

    public bool HandlerRegistered
    {
        get { lock (_lock) { return _handler is not null; } }
    }

    public uint RunDispatcher(string serviceName, Action<string[]> serviceMain)
    {
        lock (_lock)
        {
            _events.Add("dispatch");
        }

        DispatchedName = serviceName;
        if (DispatcherError != 0)
        {
            return DispatcherError;
        }

        var main = new Thread(() => serviceMain([serviceName])) { Name = "fake ServiceMain" };
        main.Start();
        main.Join();
        return 0;
    }

    public nint RegisterHandler(string serviceName, Func<uint, uint, nint, uint> handler, out uint error)
    {
        lock (_lock)
        {
            _events.Add("register");
            if (RegisterError != 0)
            {
                error = RegisterError;
                return 0;
            }

            _handler = handler;
        }

        error = 0;
        return Handle;
    }

    public bool SetStatus(nint handle, in ServiceStatusReport report, out uint error)
    {
        lock (_lock)
        {
            _events.Add("status:" + report.State);
            if (SetStatusError != 0)
            {
                error = SetStatusError;
                return false;
            }

            _reports.Add(report);
        }

        error = 0;
        return true;
    }

    // A control from the control manager, on the calling thread as the real dispatcher does.
    public uint Send(uint control)
    {
        Func<uint, uint, nint, uint>? handler;
        lock (_lock)
        {
            handler = _handler;
        }

        Assert.IsNotNull(handler, "The service registered no control handler.");
        return handler(control, 0, 0);
    }

    // Waits until a report with this state has been made.
    public bool WaitForState(uint state, TimeSpan timeout)
    {
        DateTime end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (Reports.Any(r => r.State == state))
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return Reports.Any(r => r.State == state);
    }
}
