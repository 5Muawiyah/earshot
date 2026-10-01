namespace Earshot.Widget;

// Which of the gauge's four looks it has.
internal enum GaugeMode
{
    // On this PC with a battery value that is not too old: the ring, the number, maybe a bolt.
    Reading,

    // On this PC with no value to show: the earbud mark alone.
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
//   3. on this PC with a value BatteryFreshness lets the gauge draw: Reading
//   4. on this PC, no value: MarkOnly
//
// The number is the one BatteryFreshness.Shown gives the gauge: the lower of the buds that have a value no older
// than an hour (an unknown bud is skipped), or Windows' own figure while that is current and no bud has a fresh
// broadcast value. Nothing here invents, rounds or interpolates a figure, and a charging flag that is not true
// is not shown.
internal sealed record GaugeContent(
    GaugeMode Mode,
    int? Percent,          // the number, in Reading only
    bool Low,              // Percent is at or below the low battery level
    bool Charging,         // a bud the number is drawn from says it is charging, in Reading only
    string Tooltip)
{
    public static GaugeContent From(WidgetSnapshot snapshot, DateTimeOffset now, GaugeDisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string label = settings.OtherDeviceLabel ?? "";

        if (snapshot.Where == AirPodsWhere.Elsewhere)
        {
            return new GaugeContent(GaugeMode.OnOtherDevice, null, false, false, WidgetCopy.OnElsewhere(label));
        }

        if (snapshot.Where != AirPodsWhere.ThisPc)
        {
            return new GaugeContent(GaugeMode.NotOnThisPc, null, false, false, WidgetCopy.GaugeNotOnThisPc);
        }

        if (BatteryFreshness.Shown(snapshot, now).Gauge is { } figure)
        {
            bool low = figure.Percent <= settings.LowBatteryThresholdPercent;
            string head = low ? WidgetCopy.GaugeLowBattery : figure.Charging ? WidgetCopy.GaugeCharging : WidgetCopy.GaugeAirPods;
            string detail = figure.Source == BatterySource.Windows
                ? WidgetCopy.WindowsReads(figure.Percent)
                : WidgetCopy.GaugeBudsLine(figure.Left, figure.Right);
            string tooltip = head + "\r\n" + detail + "\r\n" + WidgetCopy.GaugeReadLine(now - figure.ReadAt);
            return new GaugeContent(GaugeMode.Reading, figure.Percent, low, figure.Charging, tooltip);
        }

        return new GaugeContent(GaugeMode.MarkOnly, null, false, false, WidgetCopy.GaugeNoRecentReading);
    }
}
