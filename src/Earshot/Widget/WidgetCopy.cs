using System.Globalization;

namespace Earshot.Widget;

// Every string the widget shows or reads aloud. British English, plain, short, no em-dashes, kept to the
// same plain style as the rest of the tray copy.
internal static class WidgetCopy
{
    public const string NoReading = "No reading";
    public const string DefaultOtherDeviceLabel = "On another device";
    public const string NotSeenYet = "Not seen yet";
    public const string OnThisPc = "On this PC";
    public const string NotInUse = "Not in use";
    public const string BatteryNotReadYet = "Battery not read yet";
    public const string Connect = "Connect";
    public const string Disconnect = "Disconnect";
    public const string AutoPauseSwitch = "Pause when a bud comes out";
    public const string CaseOpen = "Case open";

    // The three column labels on the widget card, matching the mockup exactly (Case, not "Case column" or
    // similar).
    public const string LeftLabel = "L";
    public const string RightLabel = "R";
    public const string CaseLabel = "Case";

    public const string ShowOnTaskbar = "Show on the taskbar";
    public const string LeftClickConnects = "Left click connects straight away";
    public const string CardWhenCaseOpens = "Card when the case opens";
    public const string NameOtherDevice = "Name your other device...";
    public const string OtherDeviceCaption = "This is your own label for \"in use, not on this PC\". The AirPods do not report a device name.";
    public const string LowBatteryAlert = "Low battery alert";
    public const string LowBatteryThreshold = "Threshold";
    public const string OtherDeviceNameTitle = "Name your other device";

    // The claim trigger: open the case next to the PC, then choose this. Disabled with a one-line reason
    // while nothing has proved a signal threshold to claim against, never enabled on a guess. The reason
    // reads as the same fact ClaimFlow's own refusal message states, worded to sit in parentheses after
    // the item's own text.
    public const string MakeTheseMyAirPods = "Make these my AirPods";
    public const string MakeTheseMyAirPodsDisabledReason = "no signal threshold set up yet";

    private const string LeftAirPodLabel = "Left AirPod";
    private const string RightAirPodLabel = "Right AirPod";

    // "Left AirPod at 20%" and so on: the low battery alert's one line, from the literal percent the reading
    // carried, never rounded or interpolated (there is no other figure to show).
    public static string LowBatteryLeftText(int percent) => LowBatteryText(LeftAirPodLabel, percent);

    public static string LowBatteryRightText(int percent) => LowBatteryText(RightAirPodLabel, percent);

    public static string LowBatteryCaseText(int percent) => LowBatteryText(CaseLabel, percent);

    private static string LowBatteryText(string partLabel, int percent) =>
        partLabel + " at " + percent.ToString(CultureInfo.InvariantCulture) + "%";

    // "On your <name>" when a label is set, "On another device" when it is empty.
    public static string OnElsewhere(string otherDeviceLabel) =>
        string.IsNullOrWhiteSpace(otherDeviceLabel) ? DefaultOtherDeviceLabel : "On your " + otherDeviceLabel.Trim();

    // The where line for the card and the tooltip.
    public static string Where(AirPodsWhere where, string otherDeviceLabel) => where switch
    {
        AirPodsWhere.ThisPc => OnThisPc,
        AirPodsWhere.Elsewhere => OnElsewhere(otherDeviceLabel),
        AirPodsWhere.NotInUse => NotInUse,
        _ => NotSeenYet,
    };

    // "Battery read <n> s|min|h ago", never "live".
    public static string BatteryReadLine(DateTimeOffset? readAt, DateTimeOffset now)
    {
        if (readAt is not { } at)
        {
            return BatteryNotReadYet;
        }

        TimeSpan age = now - at;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        string amount = age.TotalHours >= 1
            ? Round(age.TotalHours) + " h"
            : age.TotalMinutes >= 1
                ? Round(age.TotalMinutes) + " min"
                : Round(Math.Max(0, age.TotalSeconds)) + " s";
        return "Battery read " + amount + " ago";
    }

    // The percent for one part, or NoReading when it has not been proved.
    public static string Percent(int? percent) =>
        percent is { } value ? value.ToString(CultureInfo.InvariantCulture) + "%" : NoReading;

    private static string Round(double value) => Math.Max(0, Math.Round(value)).ToString(CultureInfo.InvariantCulture);
}
