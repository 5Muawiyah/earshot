namespace Earshot.Widget;

// Which of the gauge's five looks it has.
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

    // Not on this PC, with a value of the case to show: the case mark in place of the earbud mark, and the ring, the number
    // and maybe a bolt for the case's last reading or estimate, all in tertiary ink.
    CaseAway,
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
//   1. not on this PC, and the owner's AirPods seen nearby and in use: OnOtherDevice, with the case in the tooltip
//   2. not on this PC, with a value of the case: CaseAway
//   3. not on this PC otherwise: NotOnThisPc
//   4. on this PC with a value BatteryFreshness lets the gauge draw: Reading
//   5. on this PC, no value: MarkOnly
//
// The number is the one BatteryFreshness.Shown gives: on this PC the lower bud (live, or else its last reading or the
// estimate grown from it, at any age), or Windows' own figure while that is current and no bud has a live broadcast value;
// away, the case's. While any bud is live the number is the lower of the live buds only, and "Low battery" is said only
// for a live figure. A value that is not live is drawn in tertiary ink (Tertiary), an estimate with "≈" before it
// (Estimated), and the tooltip says "Last read" or "Estimated" with the age of the reading. Nothing here invents, rounds or
// interpolates a figure (ChargeEstimator is the one place an estimate is made), and a charging flag that is not true is
// not shown.
internal sealed record GaugeContent(
    GaugeMode Mode,
    int? Percent,          // the number, in Reading and CaseAway only
    bool Low,              // Percent is at or below the low battery level
    bool Charging,         // the part the number is drawn from says it is (or was, when read) charging
    string Tooltip)
{
    // The case mark is drawn in place of the earbud mark.
    public bool CaseMark { get; init; }

    // The mark, the ring, the number and the bolt are drawn in tertiary ink: the value is a last reading or an estimate.
    public bool Tertiary { get; init; }

    // The number is an estimate: "≈" is drawn before it.
    public bool Estimated { get; init; }

    public static GaugeContent From(WidgetSnapshot snapshot, DateTimeOffset now, GaugeDisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string label = settings.OtherDeviceLabel ?? "";
        ShownBattery shown = BatteryFreshness.Shown(snapshot, now);

        if (snapshot.Where == AirPodsWhere.Elsewhere)
        {
            return new GaugeContent(GaugeMode.OnOtherDevice, null, false, false, WidgetCopy.OnElsewhere(label) + CaseLines(shown.Case, now));
        }

        if (snapshot.Where != AirPodsWhere.ThisPc)
        {
            if (shown.Case is { Percent: int casePercent } box)
            {
                return new GaugeContent(
                    GaugeMode.CaseAway, casePercent, false, box.Charging == true, WidgetCopy.GaugeNotOnThisPc + CaseLines(box, now))
                {
                    CaseMark = true,
                    Tertiary = box.Kind != ReadingKind.Live,
                    Estimated = box.Estimated,
                };
            }

            return new GaugeContent(GaugeMode.NotOnThisPc, null, false, false, WidgetCopy.GaugeNotOnThisPc);
        }

        if (shown.Gauge is { } figure)
        {
            // Low is a claim about now: only a live figure gives it. A last reading or an estimate that is at or under the
            // level is drawn as it is, in tertiary ink with its age, and never as "Low battery".
            bool low = figure.Kind == ReadingKind.Live && figure.Percent <= settings.LowBatteryThresholdPercent;
            string head = low ? WidgetCopy.GaugeLowBattery : figure.Charging ? WidgetCopy.GaugeCharging : WidgetCopy.GaugeAirPods;
            string detail = figure.Source == BatterySource.Windows
                ? WidgetCopy.WindowsReads(figure.Percent)
                : WidgetCopy.GaugeBudsLine(figure.Left, figure.Right, figure.LeftEstimated, figure.RightEstimated);
            string tooltip = head + "\r\n" + detail + "\r\n" + WidgetCopy.GaugeAgeLine(figure.Kind, now - figure.ReadAt);
            return new GaugeContent(GaugeMode.Reading, figure.Percent, low, figure.Charging, tooltip)
            {
                Tertiary = figure.Kind != ReadingKind.Live,
                Estimated = figure.Kind == ReadingKind.Estimated,
            };
        }

        // Connected, with no figure: either no pair is linked yet (opening the case near the PC links one) or nothing
        // has been read.
        string noFigure = snapshot.Selection == BroadcastSelectionState.Listening
            ? WidgetCopy.OpenTheCaseToShowBattery
            : WidgetCopy.GaugeNoRecentReading;
        return new GaugeContent(GaugeMode.MarkOnly, null, false, false, noFigure);
    }

    // "\r\nCase 80%\r\nLast read 2 h ago" for the case's value, or nothing when it has none.
    private static string CaseLines(ShownPart box, DateTimeOffset now) =>
        box is { Percent: int percent, ReadAt: DateTimeOffset at }
            ? "\r\n" + WidgetCopy.GaugeCaseLine(percent, box.Estimated) + "\r\n" + WidgetCopy.GaugeAgeLine(box.Kind, now - at)
            : "";
}
