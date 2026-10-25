using System.Diagnostics;
using Earshot.Contracts;

namespace Earshot.App;

// Why a Connect failed, in the three words the Fix offer cares about. This is the shape the connect fix's own classification is
// read through; the offer is made for PcSide and for nothing else (the owner's rule: this PC's side only).
//   PcSide      something on this PC (Windows, the driver, a refused change) kept the AirPods from connecting
//   DeviceSide  the AirPods themselves were away, in their case with the lid shut, or in use on the other device
//   Temporary   a failure the connect retries
//   Unknown     accepted and never connected, with nothing heard from the air: either side, so never offered
//   None        no failure
internal enum FixFailureClass { None, Temporary, PcSide, DeviceSide, Unknown }

// What the offer reads of a finished switch. The connect work decides both: a run of consecutive PC-side failures that reaches
// the threshold raises FixOfferDue once.
internal interface IFixOfferFacts
{
    bool FixOfferDue { get; }

    FixFailureClass FailureClass { get; }
}

internal sealed record FixOfferFacts(bool FixOfferDue, FixFailureClass FailureClass) : IFixOfferFacts
{
    public static FixOfferFacts None { get; } = new(false, FixFailureClass.None);

    // The facts of a finished switch, read off the report: the connect decides both (whose side the failure was on, and whether
    // this report made the second PC-side failure in a row).
    public static IFixOfferFacts From(ToggleReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new FixOfferFacts(report.FixOfferDue, report.FailureClass switch
        {
            Earshot.App.FailureClass.PcSide => FixFailureClass.PcSide,
            Earshot.App.FailureClass.DeviceSide => FixFailureClass.DeviceSide,
            Earshot.App.FailureClass.Temporary => FixFailureClass.Temporary,
            Earshot.App.FailureClass.Unknown => FixFailureClass.Unknown,
            _ => FixFailureClass.None,
        });
    }
}

internal static class FixOffer
{
    public const string ButtonLabel = "Fix connection";
    public const string NotStartedMessage = "Fix connection could not start.";
    public const string AlreadyRunningMessage = "Fix connection is already running.";

    // The offer is made only for a Connect that failed on this PC's side, was not cancelled, and has reached the threshold. A
    // failure that is the AirPods' (out of range, in their case, on the other device) or whose side could not be told is never
    // followed by it, whatever the count.
    public static bool ShouldOffer(IFixOfferFacts facts, bool succeeded, bool cancelled, bool wasConnect)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return wasConnect && !succeeded && !cancelled && facts.FixOfferDue && facts.FailureClass == FixFailureClass.PcSide;
    }
}

// Connect in Windows on a failed Connect's card: the connect says when (any failure it could class), and Fix connection, when it
// is due, takes the card's one button instead. Pressing it allows the AirPods and opens Windows' Bluetooth settings page.
internal static class ConnectInWindowsOffer
{
    public const string ButtonLabel = "Connect in Windows";

    public static bool ShouldOffer(ToggleReport report, bool cancelled, bool wasConnect, bool fixOffered)
    {
        ArgumentNullException.ThrowIfNull(report);
        return wasConnect && !cancelled && !fixOffered && !report.IsSuccess && report.OfferConnectInWindows;
    }
}

// Starts Fix connection. The fix runs as its own mode of this program (the "fix" verb), which asks for the person's two
// confirmations itself and raises its own administrator prompt; this only starts it.
internal interface IFixLauncher
{
    bool Start(ILog log);

    // True while a run this launcher started has not ended. Start refuses then, so a second press (card, menu, settings page)
    // never opens a second run.
    bool IsRunning => false;
}

// When the run's process ends, `ended` is called on a pool thread; the tray posts it to the UI thread, where the coordinator runs
// its start checks again (a run can end with a new pair Earshot does not protect). The program path and the arguments are
// parameters only so a test can start a harmless program in place of this one.
//
// One instance is shared by the card, the menu, the settings page and the coordinator's own card, so IsRunning is the whole
// tray's answer. A run started by another program (Setup.exe, the install script) is not seen here; the fix verb's own lock
// (FixRunLock) is what stops that one.
internal sealed class OwnProgramFixLauncher(Action? ended = null, Func<string?>? programPath = null, IReadOnlyList<string>? arguments = null) : IFixLauncher
{
    private static readonly string[] FixArguments = ["fix"];

    private readonly Lock _gate = new();
    private readonly List<Process> _running = [];
    private int _lastExitCode = int.MinValue;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running.Count > 0;
            }
        }
    }

    // The exit code of the last run that ended, for tests; null before any has.
    internal int? LastExitCode
    {
        get
        {
            lock (_gate)
            {
                return _lastExitCode == int.MinValue ? null : _lastExitCode;
            }
        }
    }

    public bool Start(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (IsRunning)
        {
            log.Info("Fix connection: a run started from here is still open, so another was not started.");
            return false;
        }

        string? exe = (programPath ?? (() => Environment.ProcessPath))();
        if (exe is null)
        {
            log.Warn("Fix connection: this program's own path is not known, so it was not started.");
            return false;
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (string argument in arguments ?? FixArguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process? process = null;
        try
        {
            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += (_, _) => OnEnded(process, log);
            lock (_gate)
            {
                _running.Add(process);
            }

            if (!process.Start())
            {
                Forget(process);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Error("Fix connection: it could not be started (" + ex.GetType().Name + ", HRESULT 0x" +
                ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ").", ex);
            if (process is not null)
            {
                Forget(process);
            }

            return false;
        }
    }

    private void OnEnded(Process process, ILog log)
    {
        try
        {
            int code = process.ExitCode;
            lock (_gate)
            {
                _lastExitCode = code;
            }
        }
        catch (InvalidOperationException ex)
        {
            log.Warn("Fix connection: the exit code of the run that ended could not be read (" + ex.GetType().Name + ").");
        }

        Forget(process);
        try
        {
            ended?.Invoke();
        }
        catch (Exception ex)
        {
            log.Error("Fix connection: the work after the run ended failed (" + ex.GetType().Name + ").", ex);
        }
    }

    private void Forget(Process process)
    {
        lock (_gate)
        {
            _running.Remove(process);
        }

        process.Dispose();
    }
}
