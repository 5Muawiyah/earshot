namespace Earshot.Widget;

// Grows a part's last reading while it was charging, at its rate (ChargeRates). Pure: now is handed in.
//
//   - only a part that said it was charging at its last reading rises: buds charge only in the case, and the case only on
//     its own power, so a bud that was not charging, or a case that was not, keeps its value;
//   - it rises from the reading at the rate until 100 and stops there;
//   - a clock behind the read time gives no rise, and nothing here ever gives less than the reading;
//   - with no rate there is no estimate.
// The value is rounded down to a whole point, and an estimate that has not yet risen a whole point is no estimate: the
// reading itself is shown, as a last reading. A newer live reading replaces whatever this gave.
internal static class ChargeEstimator
{
    public static int? Estimate(int percent, bool charging, DateTimeOffset readAt, double? percentPerHour, DateTimeOffset now)
    {
        if (!charging || percentPerHour is not double rate || rate <= 0 || double.IsNaN(rate) || now <= readAt || percent >= 100)
        {
            return null;
        }

        double grown = Math.Min(100.0, percent + (rate * (now - readAt).TotalHours));
        int value = (int)Math.Floor(grown);
        return value > percent ? value : null;
    }
}
