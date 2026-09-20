using Earshot.TestWindow.Core;

namespace Earshot.TestWindow.Ui;

// Every string the owner sees lives here (design.md architecture table). T4 (test-gui.md section
// 13) pins one part of that: the literal word "Passed" appears in this file and nowhere else, so
// a row cannot start claiming success by any other path than RowStateKind.Passed.
internal static class Copy
{
    internal const string NotRun = "Not run";
    internal const string WaitingForShutDown = "Waiting for the shut down";
    internal const string WaitingForRestart = "Waiting for the restart";
    internal const string Passed = "Passed";
    internal const string Failed = "Failed";
    internal const string Inconclusive = "Inconclusive";
    internal const string Unknown = "Unknown";
    internal const string StoppedBeforeAnyStep = "Stopped before any step";
    internal const string Locked = "Locked";

    internal static string BaseText(RowStateKind kind) => kind switch
    {
        RowStateKind.NotRun => NotRun,
        RowStateKind.WaitingForShutDown => WaitingForShutDown,
        RowStateKind.WaitingForRestart => WaitingForRestart,
        RowStateKind.Passed => Passed,
        RowStateKind.Failed => Failed,
        RowStateKind.Inconclusive => Inconclusive,
        RowStateKind.StoppedBeforeAnyStep => StoppedBeforeAnyStep,
        _ => Unknown,
    };

    // The text a row shows: the base word, plus its qualifier when it has one. A qualified pass
    // ("Passed, on an earlier build") is never shortened back to the bare word, so the amber
    // states in test-gui.md section 6.2 are always told apart from a clean one.
    internal static string RowText(DerivedRowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string baseText = BaseText(state.Kind);
        return state.Qualifier is null ? baseText : baseText + ", " + state.Qualifier;
    }

    // test-gui.md section 12's leftAtRest copy, shown after every run. AtRestUnknown is written
    // to say plainly, in its own words, that the machine must be treated as left in the state
    // Earshot exists to prevent: an unread node state is never shown as though it were a yes.
    internal const string AtRestYes =
        "Left at rest: yes. The AirPods nodes are blocked, so this PC will not page them at the next start.";

    internal const string AtRestNo =
        "NOT AT REST. This PC was left able to page the AirPods at the next start. That is the state Earshot " +
        "exists to prevent. Run Restore, or start Earshot, before you shut down.";

    internal const string AtRestUnknown =
        "NOT KNOWN. The node state could not be read, so this PC must be treated as left in the state Earshot " +
        "exists to prevent, until Restore has run.";

    internal const string AtRestNotApplicable =
        "At rest does not apply here: Earshot is not set up, or Block at boot is off.";

    internal const string AtRestNoSuchFinding =
        "Not recorded. This run is older than the at-rest check. Treat it as not known.";

    // no-on-purpose carries its own reason, so it is built rather than fixed.
    internal static string AtRestOnPurpose(string reason) =>
        "Left enabled on purpose. " + reason + " Shut down as the test asks, or run Restore to put it back.";

    // Never conflates an unread state with a good one (T11): "unknown" and a missing finding
    // both route through AtRestUnknown/AtRestNoSuchFinding, never AtRestYes.
    internal static string LeftAtRestText(string? leftAtRest, string? reason) => leftAtRest switch
    {
        "yes" => AtRestYes,
        "no" => AtRestNo,
        "no-on-purpose" => AtRestOnPurpose(string.IsNullOrEmpty(reason) ? "No reason was recorded." : reason),
        "not-applicable" => AtRestNotApplicable,
        "unknown" => AtRestUnknown,
        null => AtRestNoSuchFinding,
        _ => AtRestUnknown,
    };
}
