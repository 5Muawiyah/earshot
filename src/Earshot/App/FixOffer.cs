using System.Diagnostics;
using Earshot.Contracts;

namespace Earshot.App;

// Why a Connect failed, in the three words the Fix offer cares about. This is the shape the connect fix's own classification is
// read through; the offer is made for PcSide and for nothing else.
//   PcSide      something on this PC (Windows, the driver, a refused change) kept the AirPods from connecting
//   DeviceSide  the AirPods themselves were away, in their case with the lid shut, or in use on the other device
//   Temporary   a failure the connect retries, or one that is not yet known to be either
//   None        no failure
internal enum FixFailureClass { None, Temporary, PcSide, DeviceSide }

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
            _ => FixFailureClass.None,
        });
    }
}

internal static class FixOffer
{
    public const string ButtonLabel = "Fix connection";
    public const string NotStartedMessage = "Fix connection could not start.";

    // The offer is made only for a Connect that failed on this PC's side, was not cancelled, and has reached the threshold. A
    // failure that is the AirPods' (out of range, in their case, on the other device) is never followed by it, whatever the count.
    public static bool ShouldOffer(IFixOfferFacts facts, bool succeeded, bool cancelled, bool wasConnect)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return wasConnect && !succeeded && !cancelled && facts.FixOfferDue && facts.FailureClass == FixFailureClass.PcSide;
    }
}

// Starts Fix connection. The fix runs as its own mode of this program (the "fix" verb), which asks for the person's two
// confirmations itself and raises its own administrator prompt; this only starts it.
internal interface IFixLauncher
{
    bool Start(ILog log);
}

internal sealed class OwnProgramFixLauncher : IFixLauncher
{
    public bool Start(ILog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            log.Warn("Fix connection: this program's own path is not known, so it was not started.");
            return false;
        }

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add("fix");
        try
        {
            using Process? process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Error("Fix connection: it could not be started (" + ex.GetType().Name + ").", ex);
            return false;
        }
    }
}
