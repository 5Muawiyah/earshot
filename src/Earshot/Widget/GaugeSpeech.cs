namespace Earshot.Widget;

// What a screen reader is told of the gauge: a name that does not change and a value that does. Pure: built from the same
// snapshot, time and settings as the gauge's own content (GaugeContent), so the spoken value never says more than the gauge shows.
//
//   Reading: "Left 70%, Right 80%, charging"; a part that is not live says what it is and how old ("Left about 90%, estimated,
//   read 2 h ago", "Right 80%, last read 2 h ago"); "low battery" is added only for a live figure (GaugeContent.Low); Windows'
//   own figure reads "Windows reads 70%".
//   Away with the case's value: "Not on this PC. Case about 80%, estimated, read 2 h ago".
//   On another device: "On iPhone", with the case when it has a value. Otherwise the gauge's own one line.
internal static class GaugeSpeech
{
    public const string Name = "AirPods battery";

    public static string Value(WidgetSnapshot snapshot, DateTimeOffset now, GaugeDisplaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        GaugeContent content = GaugeContent.From(snapshot, now, settings);
        ShownBattery shown = BatteryFreshness.Shown(snapshot, now);
        switch (content.Mode)
        {
            case GaugeMode.OnOtherDevice:
                string label = settings.OtherDeviceLabel ?? "";
                string where = string.IsNullOrWhiteSpace(label) ? "On another device" : "On " + label.Trim();
                return shown.Case.HasValue ? where + ". " + WidgetCopy.SpokenReading(WidgetCopy.CaseLabel, shown.Case, now) : where;
            case GaugeMode.CaseAway:
                return WidgetCopy.GaugeNotOnThisPc + ". " + WidgetCopy.SpokenReading(WidgetCopy.CaseLabel, shown.Case, now);
            case GaugeMode.NotOnThisPc:
                return WidgetCopy.GaugeNotOnThisPc;
            case GaugeMode.Reading:
                return Reading(content, shown, now);
            default:
                return content.Tooltip.Replace("\r\n", ", ", StringComparison.Ordinal);
        }
    }

    private static string Reading(GaugeContent content, ShownBattery shown, DateTimeOffset now)
    {
        if (shown.Gauge is { Source: BatterySource.Windows } windows)
        {
            return WidgetCopy.WindowsReads(windows.Percent) + (windows.Charging ? ", charging" : "");
        }

        var parts = new List<string>();
        if (shown.Left.HasValue)
        {
            parts.Add(WidgetCopy.SpokenReading(WidgetCopy.LeftWord, shown.Left, now, withCharging: false));
        }

        if (shown.Right.HasValue)
        {
            parts.Add(WidgetCopy.SpokenReading(WidgetCopy.RightWord, shown.Right, now, withCharging: false));
        }

        if (parts.Count == 0)
        {
            // A figure with no bud under it cannot be said as buds: say the number the gauge draws.
            parts.Add(WidgetCopy.SpokenPercent(content.Percent ?? 0, content.Estimated));
        }

        string text = string.Join(", ", parts);
        if (content.Charging)
        {
            text += ", charging";
        }

        if (content.Low)
        {
            text += ", low battery";
        }

        return text;
    }
}
