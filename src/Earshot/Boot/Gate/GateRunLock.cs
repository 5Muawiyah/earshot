using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using Earshot.Contracts;

namespace Earshot.Boot.Gate;

// One elevated Earshot run at a time on the machine: a gate run from \Earshot\Gate, \Earshot\Protect or
// \Earshot\BootBlock, and uninstall from before it reads device.json until the machine folder is removed, so a
// gate run queued behind uninstall finds nothing left to act on. The scheduler's Queue policy
// only orders runs of the same task, so without this a boot block, a protect verb and an allow could run in
// three processes at once. The device change lock (DeviceChangeLock) still guards each node or service change
// on its own; this lock also keeps the status files, config.json and device.json writes apart.
internal interface IGateRunLock
{
    // Enters the lock, waiting up to timeout. Null, with a failed step, when it could not be entered: nothing
    // may then be changed. Dispose the result on the thread that entered it.
    IDisposable? TryEnter(TimeSpan timeout, IList<StepOutcome> steps);
}

// The machine-wide named mutex Global\Earshot.Gate.RunLock. It is created with a protected access list that
// grants SYSTEM and Administrators full control and nobody else anything, so a standard user can neither open
// it nor hold it while the gate waits. A standard user could still create a mutex of that name before the
// gate does; such a mutex is not trusted (its owner is neither SYSTEM nor Administrators), so the gate changes
// nothing and says so rather than wait on it. That costs a failed request, never an elevation.
//
// Windows releases a mutex when the thread or process that owns it ends, so a gate run the scheduler stops at
// its time limit leaves it abandoned, not held: the next run enters it and records that.
// https://learn.microsoft.com/en-us/windows/win32/sync/using-mutex-objects
// https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces
// https://learn.microsoft.com/en-us/dotnet/api/system.threading.mutexacl.create
internal sealed class MachineGateMutex : IGateRunLock
{
    public const string DefaultName = @"Global\Earshot.Gate.RunLock";
    public const string StepName = "gate-run-lock";

    // WAIT_TIMEOUT, the Win32 result of a wait that ran out.
    // https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-waitforsingleobject
    internal const uint WaitTimeout = 258;

    private readonly string _name;
    private readonly Func<MutexSecurity> _security;
    private readonly Func<SecurityIdentifier?, bool> _trustedOwner;

    public MachineGateMutex()
        : this(DefaultName, MachineSecurity, IsMachineOwner)
    {
    }

    // For tests: another name, an access list that also grants the current user, and the owners trusted.
    internal MachineGateMutex(string name, Func<MutexSecurity> security, Func<SecurityIdentifier?, bool> trustedOwner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(trustedOwner);
        _name = name;
        _security = security;
        _trustedOwner = trustedOwner;
    }

    public IDisposable? TryEnter(TimeSpan timeout, IList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        Mutex mutex;
        bool created;
        try
        {
            mutex = MutexAcl.Create(initiallyOwned: false, _name, out created, _security());
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult(StepName, ex.HResult, _name + " exists but may not be opened here, so nothing was changed. " + ex.Message));
            return null;
        }
        catch (WaitHandleCannotBeOpenedException ex)
        {
            steps.Add(StepOutcomes.FromHResult(StepName, ex.HResult, _name + " could not be created or opened, so nothing was changed. " + ex.Message));
            return null;
        }
        catch (IOException ex)
        {
            steps.Add(StepOutcomes.FromHResult(StepName, ex.HResult, _name + " could not be created or opened, so nothing was changed. " + ex.Message));
            return null;
        }

        if (!created && Untrusted(mutex, steps))
        {
            mutex.Dispose();
            return null;
        }

        bool abandoned = false;
        bool entered;
        try
        {
            entered = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // The owner ended without releasing it; this thread now owns it.
            entered = true;
            abandoned = true;
        }

        if (!entered)
        {
            mutex.Dispose();
            steps.Add(StepOutcomes.FromWin32(StepName, WaitTimeout,
                "Another Earshot gate run still held " + _name + " after " + Seconds(timeout) + ", so nothing was changed.", ok: false));
            return null;
        }

        steps.Add(StepOutcomes.FromWin32(StepName, 0, abandoned
            ? _name + ": entered after the previous run ended without releasing it."
            : _name));
        return new Held(mutex);
    }

    // True for the step of a lock another run still held.
    public static bool IsBusy(StepOutcome step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Step == StepName && !step.Ok && step.Code == (int)WaitTimeout;
    }

    internal static MutexSecurity MachineSecurity()
    {
        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(Sddl.LocalSystemSid), MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(Sddl.AdministratorsSid), MutexRights.FullControl, AccessControlType.Allow));
        return security;
    }

    // SYSTEM's and an elevated administrator's objects are owned by SYSTEM or by Administrators.
    internal static bool IsMachineOwner(SecurityIdentifier? owner) =>
        owner is not null && (owner.Value == Sddl.LocalSystemSid || owner.Value == Sddl.AdministratorsSid);

    // Reads the owner of a mutex that already existed. A failed read is not trusted either.
    // https://learn.microsoft.com/en-us/dotnet/api/system.threading.threadingaclextensions.getaccesscontrol
    private bool Untrusted(Mutex mutex, IList<StepOutcome> steps)
    {
        SecurityIdentifier? owner;
        try
        {
            owner = mutex.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (UnauthorizedAccessException ex)
        {
            steps.Add(StepOutcomes.FromHResult(StepName, ex.HResult, _name + ": its owner could not be read, so it is not trusted and nothing was changed. " + ex.Message));
            return true;
        }
        catch (InvalidOperationException ex)
        {
            steps.Add(StepOutcomes.FromHResult(StepName, ex.HResult, _name + ": its owner could not be read, so it is not trusted and nothing was changed. " + ex.Message));
            return true;
        }

        if (_trustedOwner(owner))
        {
            return false;
        }

        steps.Add(StepOutcomes.NotAttempted(StepName,
            _name + " was created by " + (owner?.Value ?? "an unknown owner") + ", not by SYSTEM or Administrators, so it is not trusted and nothing was changed."));
        return true;
    }

    private static string Seconds(TimeSpan span) =>
        span.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " s";

    private sealed class Held(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            Mutex? held = Interlocked.Exchange(ref _mutex, null);
            if (held is null)
            {
                return;
            }

            held.ReleaseMutex();
            held.Dispose();
        }
    }
}

// No machine-wide lock: for a gate built over a test's fake node table, which runs unelevated and must not
// take the real mutex. Every enter succeeds and records that no lock was taken.
internal sealed class NoGateRunLock : IGateRunLock
{
    public static NoGateRunLock Instance { get; } = new();

    public IDisposable? TryEnter(TimeSpan timeout, IList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return Nothing.Instance;
    }

    private sealed class Nothing : IDisposable
    {
        public static Nothing Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
