using System.Globalization;
using Earshot.Boot.Gate;

namespace Earshot.Service;

// Every line the hand-back service writes to its log, in one place, so the tests pin the text and the service never
// builds a line of its own. The log is SYSTEM's own, which the owner cannot read without elevation, so nothing the
// live tests decide rests on it: the status file is the record. No device name, address or container id appears in any
// line.
internal static class HandBackServiceText
{
    public const string Prefix = "Hand-back service: ";

    public static string Running(string image) => Prefix + "running (image " + image + ").";

    public static string Refused(string reason, GateExitCode code) =>
        Prefix + "refused, " + reason + " (" + GateExitCodes.ResultName(code) + ").";

    public static string NotStartedByTheControlManager(uint error) =>
        Prefix + "refused, not started by the service control manager (Win32 " + error.ToString(CultureInfo.InvariantCulture) + ").";

    public static string DispatcherFailed(uint error) =>
        Prefix + "the service control manager did not take this process (Win32 " + error.ToString(CultureInfo.InvariantCulture) + ").";

    public static string HandlerNotRegistered(uint error) =>
        Prefix + "the control handler was not registered (Win32 " + error.ToString(CultureInfo.InvariantCulture) + ").";

    public static string StatusNotReported(uint state, uint error) =>
        Prefix + "status " + ServiceSteps.StateName(state) + " was not reported (Win32 " + error.ToString(CultureInfo.InvariantCulture) + ").";

    public static string MachineFolder(string state) => Prefix + "machine folder " + state + ".";

    public static string PreshutdownReceived(DateTimeOffset at) => Prefix + "preshutdown received at " + Utc(at) + ".";

    public const string PreshutdownReceivedAgain = Prefix + "preshutdown received again; the first run continues.";

    public static string NothingToDo(string reason) => Prefix + "nothing to do: " + reason + ".";

    public static string BlockSent(DateTimeOffset at) => Prefix + "block sent at " + Utc(at) + ".";

    public static string Veto(int nodes, TimeSpan after) =>
        Prefix + "veto on " + nodes.ToString(CultureInfo.InvariantCulture) + " node(s); sent again after " +
        Milliseconds(after) + " ms.";

    // block: success, partial, failed, or "not sent: <reason>".
    public static string Finished(TimeSpan elapsed, string block, string? state, string status) =>
        Prefix + "finished in " + Milliseconds(elapsed) + " ms; block " + block + "; state " + (state ?? "unknown") +
        "; status file " + status + ".";

    public static string CutShort(TimeSpan elapsed, string waitingFor) =>
        Prefix + "cut short at " + Milliseconds(elapsed) + " ms while waiting for " + waitingFor + "; nothing was changed.";

    public static string PreshutdownStopped(Exception error) =>
        Prefix + "the shut-down block stopped with " + error.GetType().Name + ".";

    public static string ControlNotHandled(uint control) =>
        Prefix + "control " + control.ToString(CultureInfo.InvariantCulture) + " not handled.";

    public const string StopReceived = Prefix + "stop received.";

    public const string Stopped = Prefix + "stopped.";

    // What the log says about the outcome of a shut-down block: the "block ..." clause of the finished line.
    public static string BlockClause(PreshutdownResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.BlockSent)
        {
            return "not sent: " + result.Reason;
        }

        return result.Outcome switch
        {
            GateExitCode.Success => "success",
            GateExitCode.Partial => "partial",
            _ => "failed",
        };
    }

    public static string StatusClause(PreshutdownResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.StatusWritten
            ? "written"
            : "not written: " + (result.StatusStep is { } step ? step.CodeName : GateExitCodes.ResultName(result.Outcome));
    }

    internal static string Utc(DateTimeOffset value) => GateActions.FormatUtc(value);

    private static string Milliseconds(TimeSpan span) =>
        Math.Round(span.TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture);
}
