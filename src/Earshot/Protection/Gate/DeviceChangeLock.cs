using System.Globalization;
using Earshot.Contracts;

namespace Earshot.AudioProtection.Gate;

// One device change at a time across the Earshot processes that change the device. \Earshot\Gate,
// \Earshot\Protect and \Earshot\BootBlock are separate tasks, and the scheduler's Queue policy only orders
// runs of the same task, so a protect verb and a block could otherwise run side by side as SYSTEM, and the
// elevated uninstall restore could run beside either. Nothing documents BluetoothSetServiceState while the
// device nodes are being disabled, so a service change must never overlap a node change.
//
// The lock is an open of %ProgramData%\Earshot\device-change.lock that asks for write access and shares only
// read, so a second writer's open fails with a sharing violation until the first handle closes. The gate has
// already checked that folder's ACL (only SYSTEM and administrators may create or replace entries), and the
// file is never read or written, so nothing is trusted from it. Windows closes the handle when the process
// ends, however it ends, and the scheduler stops a task at its time limit, so a stuck holder cannot keep the
// lock past its task's limit.
//
// It is re-entrant on the thread that holds it, so a caller that already holds it (the whole gate run, say)
// can call code that takes it again. Dispose on the thread that acquired it.
// https://learn.microsoft.com/en-us/windows/win32/fileio/creating-and-opening-files
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itasksettings-get_executiontimelimit
internal sealed class DeviceChangeLock : IDisposable
{
    public const string FileName = "device-change.lock";
    public const string StepName = "device-change-lock";

    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION, and the HRESULT form FileStream reports them in.
    private const uint ErrorSharingViolation = 32;
    private const uint ErrorLockViolation = 33;
    private const int HResultSharingViolation = unchecked((int)0x80070020);
    private const int HResultLockViolation = unchecked((int)0x80070021);

    [ThreadStatic]
    private static Dictionary<string, DeviceChangeLock>? t_held;

    private readonly string _path;
    private FileStream? _stream;
    private int _depth = 1;

    private DeviceChangeLock(string path, FileStream stream)
    {
        _path = path;
        _stream = stream;
    }

    // Sleeps for the poll interval and keeps waiting.
    public static bool SleepAndContinue(TimeSpan delay)
    {
        Thread.Sleep(delay);
        return true;
    }

    // The lock, or null when it could not be taken within the timeout (another change still holds it), when
    // wait returned false, or when the open failed for another reason. Every outcome is a step.
    public static DeviceChangeLock? TryAcquire(string folder, TimeSpan timeout, Func<TimeSpan, bool> wait, IList<StepOutcome> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentNullException.ThrowIfNull(steps);
        string path = Path.GetFullPath(Path.Combine(folder, FileName));
        t_held ??= new Dictionary<string, DeviceChangeLock>(StringComparer.OrdinalIgnoreCase);
        if (t_held.TryGetValue(path, out DeviceChangeLock? held))
        {
            held._depth++;
            return held;
        }

        long retries = timeout <= TimeSpan.Zero ? 0 : (long)Math.Ceiling(timeout / PollInterval);
        for (long waited = 0; ; waited++)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, bufferSize: 0, FileOptions.None);
                var acquired = new DeviceChangeLock(path, stream);
                t_held[path] = acquired;
                steps.Add(StepOutcomes.FromHResult(StepName, 0,
                    waited == 0 ? path : path + ", after waiting " + Seconds(waited) + " for another device change."));
                return acquired;
            }
            catch (IOException ex) when (IsHeldElsewhere(ex))
            {
                if (waited >= retries)
                {
                    steps.Add(StepOutcomes.FromWin32(StepName, Win32Of(ex),
                        path + ": another Earshot device change was still running after " + Seconds(waited) + ", so nothing was changed."));
                    return null;
                }

                if (!wait(PollInterval))
                {
                    steps.Add(StepOutcomes.FromWin32(StepName, Win32Of(ex),
                        path + ": stopped waiting for another Earshot device change, so nothing was changed."));
                    return null;
                }
            }
            catch (IOException ex)
            {
                steps.Add(StepOutcomes.FromHResult(StepName, ex.HResult, path + ": " + ex.Message));
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                steps.Add(StepOutcomes.FromHResult(StepName, ex.HResult, path + ": " + ex.Message));
                return null;
            }
        }
    }

    public void Dispose()
    {
        if (_stream is null || --_depth > 0)
        {
            return;
        }

        t_held?.Remove(_path);
        _stream.Dispose();
        _stream = null;
    }

    // True for the step of a lock that another device change still held, as opposed to an open that failed.
    public static bool IsBusy(StepOutcome step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Step == StepName && !step.Ok && step.Code is (int)ErrorSharingViolation or (int)ErrorLockViolation;
    }

    private static bool IsHeldElsewhere(IOException ex) =>
        ex.HResult is HResultSharingViolation or HResultLockViolation;

    // The CreateFile error behind the HRESULT, so the step is named from the Win32 table.
    private static uint Win32Of(IOException ex) =>
        ex.HResult == HResultLockViolation ? ErrorLockViolation : ErrorSharingViolation;

    private static string Seconds(long polls) =>
        (polls * PollInterval).TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " s";
}
