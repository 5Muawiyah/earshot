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
    public const string CardTitle = "AirPods";

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

    // The settings page: row labels, the two gauge positions, section heads and the captions that say why a
    // switched-on row cannot act yet.
    public const string SettingsTitle = "Settings";
    public const string SettingsGaugePosition = "Gauge position";
    public const string PositionRightEnd = "Right end";
    public const string PositionNextToApps = "Next to apps";
    public const string SettingsOtherDevice = "Other device";
    public const string SettingsPauseBud = AutoPauseSwitch;
    public const string SettingsPauseLeave = "Pause when AirPods leave this PC";
    public const string SettingsCaseCard = "Case-open card";
    public const string SettingsLowBattery = "Low battery alert";
    public const string SettingsLeftClick = "Left click connects";
    public const string SettingsHandBack = Earshot.Tray.MenuModel.HandBackOnShutdownAndSleep;
    public const string SettingsShortcuts = "Shortcuts";
    public const string SettingsUpdates = "Updates";
    public const string SettingsWaitsOnInEar = "Earshot cannot yet tell when a bud is in your ear.";
    public const string SettingsWaitsOnLid = "Earshot cannot yet tell when the case lid is open.";
    public const string ShortcutNotSet = "Not set";
    public const string ShortcutPressKeys = "Press keys";
    public const string ShortcutClear = "Clear";
    public const string CheckForUpdates = "Check for updates";
    public const string CheckButton = "Check";
    public const string RepairEarshot = "Repair Earshot";
    public const string RepairButton = "Repair";
    public const string RepairSub = "Checks the installed files and sets Earshot up again.";
    public const string CheckAutomatically = "Check automatically";
    public const string UpdateButton = "Update";
    public const string ShortcutNotSaved = "Couldn't save that shortcut.";

    // Battery set-up: the card's button, the menu item, the three steps and their outcomes. Plain, short,
    // never a figure the set-up did not prove.
    public const string SetUpBattery = "Set up battery";
    public const string SetUpBatteryBluetoothOff = "Set up battery (Bluetooth is off)";
    public const string TryAgain = "Try again";
    public const string Cancel = "Cancel";
    public const string Save = "Save";
    public const string Done = "Done";
    public const string Back = "Back";
    public const string Charging = "Charging";
    public const string SetupOpenCase = "Open your AirPods case next to this PC";
    public const string SetupWaiting = "Waiting";
    public const string SetupWhatDoesYourIphoneShow = "What does your iPhone show?";
    public const string SetupPickCaption = "Pick the nearest 10. If it ends in 5, pick the lower.";
    public const string SetupBatterySetUp = "Battery set up";
    public const string SetupCaseSetUp = "Case battery set up";
    public const string SetupRepeatForBuds = "To set up the buds, try again when they show different levels.";
    public const string SetupBudsReadTheSame = "Both buds read the same. Try again when they differ.";
    public const string SetupSaved = "Set-up saved";
    public const string SetupNotSaved = "Couldn't save the set-up";
    public const string SetupNotSavedSub = "Nothing was changed. Try again.";
    public const string SetupNeedsAnother = "Set up once more to confirm it. The battery shows once it is confirmed.";
    public const string SetupCouldNotRead = "Couldn't read your AirPods' battery";
    public const string SetupCapturesKept = "What was seen is kept on this PC.";
    public const string SetupCannotReadYet = "Your AirPods' battery could not be read yet.";
    public const string SetupNotFound = "Couldn't find your AirPods";
    public const string SetupNotFoundHint = "Open the case next to the PC. If that does not work, take a bud out and try again.";
    public const string SetupAmbiguous = "More than one set of AirPods is near";
    public const string SetupAmbiguousHint = "Move away from other AirPods and try again.";
    public const string SetupBluetoothOff = "Bluetooth is off";
    public const string SetupBluetoothOffHint = "Turn Bluetooth on, then try again.";

    private const string LeftAirPodLabel = "Left AirPod";
    private const string RightAirPodLabel = "Right AirPod";

    // The update line on the card: the version a check found, from the release itself.
    public static string UpdateAvailable(string version) => "Version " + version + " is available";

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

    // The gauge's tooltip. Three lines when there is a reading, one line otherwise.
    public const string GaugeAirPods = "AirPods";
    public const string GaugeCharging = "Charging";
    public const string GaugeLowBattery = "Low battery";
    public const string GaugeNotOnThisPc = "Not on this PC";
    public const string GaugeBatteryNotSetUp = "Battery not set up";
    public const string GaugeNoRecentReading = "No recent reading";

    // "L 70%   R 60%": the buds that have a proved reading, left first, three spaces apart. A bud with none is
    // left out, never shown as a dash or a guess.
    public static string GaugeBudsLine(int? left, int? right)
    {
        string l = left is { } lp ? LeftLabel + " " + lp.ToString(CultureInfo.InvariantCulture) + "%" : "";
        string r = right is { } rp ? RightLabel + " " + rp.ToString(CultureInfo.InvariantCulture) + "%" : "";
        return l.Length > 0 && r.Length > 0 ? l + "   " + r : l + r;
    }

    // "Read just now" inside the first minute, then "Read 2 min ago" (whole minutes, rounded down, so the
    // line is never younger than the reading).
    public static string GaugeReadLine(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "Read just now";
        }

        return "Read " + ((long)Math.Floor(age.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " min ago";
    }

    // The percent for one part, or NoReading when it has not been proved.
    public static string Percent(int? percent) =>
        percent is { } value ? value.ToString(CultureInfo.InvariantCulture) + "%" : NoReading;

    private static string Round(double value) => Math.Max(0, Math.Round(value)).ToString(CultureInfo.InvariantCulture);
}
