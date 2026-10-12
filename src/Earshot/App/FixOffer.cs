using System.Diagnostics;
using Earshot.Contracts;

namespace Earshot.App;

// Why a Connect failed, in the three words the Fix offer cares about. This is the shape the connect fix's own classification is
// read through; the offer is made for PcSide and Unknown, and for nothing else.
//   PcSide      something on this PC (Windows, the driver, a refused change) kept the AirPods from connecting
//   DeviceSide  the AirPods themselves were away, in their case with the lid shut, or in use on the other device
//   Temporary   a failure the connect retries
//   Unknown     accepted and never connected, with nothing heard from the air: it may be this PC's side
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

    // The offer is made only for a Connect that failed on this PC's side, or for a reason nothing heard could tell, was not
    // cancelled, and has reached the threshold (the connect counts the unknown later than the PC-side). A failure that is the
    // AirPods' (out of range, in their case, on the other device) is never followed by it, whatever the count.
    public static bool ShouldOffer(IFixOfferFacts facts, bool succeeded, bool cancelled, bool wasConnect)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return wasConnect && !succeeded && !cancelled && facts.FixOfferDue &&
            facts.FailureClass is FixFailureClass.PcSide or FixFailureClass.Unknown;
    }
}

// Starts Fix connection. The fix runs as its own mode of this program (the "fix" verb), which asks for the person's two
// confirmations itself and raises its own administrator prompt; this only starts it.
internal interface IFixLauncher
{
    bool Start(ILog log);
}

// When the run's process ends, `ended` is called on a pool thread; the tray posts it to the UI thread, where the coordinator runs
// its start checks again (a run can end with a new pair Earshot does not protect). The program path is a parameter only so a
// test can start a harmless program in place of this one.
internal sealed class OwnProgramFixLauncher(Action? ended = null, Func<string?>? programPath = null) : IFixLauncher
{
    private readonly Lock _gate = new();
    private readonly List<Process> _running = [];

    public bool Start(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        string? exe = (programPath ?? (() => Environment.ProcessPath))();
        if (exe is null)
        {
            log.Warn("Fix connection: this program's own path is not known, so it was not started.");
            return false;
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add("fix");
        Process? process = null;
        try
        {
            process = new Process { StartInfo = start, EnableRaisingEvents = ended is not null };
            if (ended is not null)
            {
                process.Exited += (_, _) => OnEnded(process, log);
                lock (_gate)
                {
                    _running.Add(process);
                }
            }

            if (!process.Start())
            {
                Forget(process);
                return false;
            }

            if (ended is null)
            {
                process.Dispose();
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
