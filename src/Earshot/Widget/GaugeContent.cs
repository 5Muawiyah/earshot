namespace Earshot.Widget;

// Which of the gauge's four looks it has.
internal enum GaugeMode
{
    // On this PC with a proved reading no older than an hour: the ring, the number, maybe a bolt.
    Reading,

    // On this PC with no reading to show: the earbud mark alone.
    MarkOnly,

    // Not on this PC: the earbud mark at 40% opacity.
    NotOnThisPc,

    // The owner's AirPods are near and in use on another device: the mark and a phone in the number slot.
    OnOtherDevice,
}

// The numbers the gauge's text depends on and the owner can change.
internal readonly record struct GaugeDisplaySettings(int LowBatteryThresholdPercent, string OtherDeviceLabel)
{
    public static GaugeDisplaySettings Default => new(WidgetSettings.DefaultLowBatteryThresholdPercent, "iPhone");
}

// What the gauge shows and says, worked out from the widget's snapshot and the time, and nothing else. Pure:
// no drawing, no clock read (now is handed in), so every state can be checked without a window.
//
// The first match wins:
//   1. not on this PC, and the owner's AirPods seen nearby and in use: OnOtherDevice
//   2. not on this PC otherwise: NotOnThisPc
//   3. on this PC with a proved reading no older than an hour: Reading
//   4. on this PC, no reading: MarkOnly
//
// Only what the snapshot carries as proved reaches the gauge: a bud with no percent is a bud with no proved
// percent (PartReading.Percent is null until the decode table proves the bud order), a charging flag that is
// not true is not shown, and a reading counts as recent only when every bud it is drawn from was read within
// the last hour (the number is the lower of the buds, so a stale bud could be the lower one). Nothing here
// invents, rounds or interpolates a figure.
internal sealed record GaugeContent(
    GaugeMode Mode,
    int? Percent,          // the lower proved bud, in Reading only
    bool Low,              // Percent is at or below the low battery level
    bool Charging,         // a bud with a proved charging flag says it is charging, in Reading only
    string Tooltip,
    bool BatteryNotSetUp)  // MarkOnly only: no set-up has been done, as against no recent reading
{
    // A battery reading older than this counts as no recent reading.
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(1);

    public static GaugeContent From(WidgetSnapshot snapshot, DateTimeOffset now, GaugeDisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string label = settings.OtherDeviceLabel ?? "";

        if (snapshot.Where == AirPodsWhere.Elsewhere)
        {
            return new GaugeContent(GaugeMode.OnOtherDevice, null, false, false, WidgetCopy.OnElsewhere(label), false);
        }

        if (snapshot.Where != AirPodsWhere.ThisPc)
        {
            return new GaugeContent(GaugeMode.NotOnThisPc, null, false, false, WidgetCopy.GaugeNotOnThisPc, false);
        }

        if (RecentBuds(snapshot, now, out int? left, out int? right, out DateTimeOffset oldest) && LowerOf(left, right) is { } percent)
        {
            bool low = percent <= settings.LowBatteryThresholdPercent;
            bool charging = snapshot.Left.Charging == true || snapshot.Right.Charging == true;
            string head = low ? WidgetCopy.GaugeLowBattery : charging ? WidgetCopy.GaugeCharging : WidgetCopy.GaugeAirPods;
            string tooltip = head + "\r\n" + WidgetCopy.GaugeBudsLine(left, right) + "\r\n" + WidgetCopy.GaugeReadLine(now - oldest);
            return new GaugeContent(GaugeMode.Reading, percent, low, charging, tooltip, false);
        }

        bool notSetUp = !snapshot.ClaimExists;
        return new GaugeContent(
            GaugeMode.MarkOnly, null, false, false,
            notSetUp ? WidgetCopy.GaugeBatteryNotSetUp : WidgetCopy.GaugeNoRecentReading, notSetUp);
    }

    // The buds' proved percents, when every one of them was read within the recent window. False when a bud
    // has a percent but no read time, or one older than the window, or when neither has a percent.
    private static bool RecentBuds(WidgetSnapshot snapshot, DateTimeOffset now, out int? left, out int? right, out DateTimeOffset oldest)
    {
        left = snapshot.Left.Percent;
        right = snapshot.Right.Percent;
        oldest = now;
        bool any = false;
        foreach (PartReading bud in new[] { snapshot.Left, snapshot.Right })
        {
            if (bud.Percent is null)
            {
                continue;
            }

            if (bud.ReadAt is not { } at)
            {
                return false;
            }

            if (now - at > RecentWindow)
            {
                return false;
            }

            if (!any || at < oldest)
            {
                oldest = at;
            }

            any = true;
        }

        return any;
    }

    private static int? LowerOf(int? left, int? right)
    {
        if (left is { } l && right is { } r)
        {
            return Math.Clamp(Math.Min(l, r), 0, 100);
        }

        int? one = left ?? right;
        return one is { } v ? Math.Clamp(v, 0, 100) : null;
    }
}
