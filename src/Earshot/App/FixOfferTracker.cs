using Earshot.Diagnostics;

namespace Earshot.App;

// Decides when the card should offer Fix connection. Two connects in a row that failed for a reason on this PC's side raise
// the offer, once, and the count starts again. A success starts it again too. A failure on the AirPods' side (away, in the
// case, in use on the phone) and a cancelled connect neither count nor reset: they say nothing about this PC's pairing.
internal sealed class FixOfferTracker
{
    // Consecutive PC-side failures that raise the offer.
    public const int FailuresForOffer = 2;

    public int Consecutive { get; private set; }

    // Notes one finished connect. Returns true exactly when this one makes the offer due.
    public bool NoteConnect(bool success, FailureClass failure, bool cancelled)
    {
        if (cancelled)
        {
            return false;
        }

        if (success)
        {
            Consecutive = 0;
            return false;
        }

        if (failure != FailureClass.PcSide)
        {
            return false;
        }

        Consecutive++;
        if (Consecutive < FailuresForOffer)
        {
            return false;
        }

        Consecutive = 0;
        return true;
    }

    // Starts from what an earlier run wrote down, without raising the offer for failures that were already offered or
    // already answered. Entries are oldest first.
    public static FixOfferTracker Replay(IEnumerable<ConnectHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var tracker = new FixOfferTracker();
        foreach (ConnectHistoryEntry entry in entries.Where(e => e.Action == ConnectHistoryActions.Connect))
        {
            tracker.NoteConnect(entry.Outcome == ConnectHistoryOutcomes.Connected, ParseClass(entry.FailureClass), entry.Outcome == ConnectHistoryOutcomes.Cancelled);
        }

        return tracker;
    }

    private static FailureClass ParseClass(string name) =>
        Enum.TryParse(name, ignoreCase: false, out FailureClass parsed) ? parsed : FailureClass.None;
}
