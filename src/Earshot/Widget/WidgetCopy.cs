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
    public const string AutoPauseSwitch = "Pause on removal";
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
    public const string SettingsGaugePosition = "Position";
    public const string SettingsGaugeDisplay = "Display";
    public const string DisplayChosenNotConnected = "That display is not connected. The gauge is on the main display.";
    public const string DisplayChosenNoTaskbar = "That display shows no taskbar. The gauge is on the main display.";
    public const string PositionRightEnd = "Right end";
    public const string PositionNextToApps = "Next to apps";
    public const string SettingsOtherDevice = "Other device";
    public const string SettingsPauseBud = AutoPauseSwitch;
    public const string SettingsPauseLeave = "Pause on leave";
    public const string SettingsLowBattery = "Low battery";
    public const string SettingsLeftClick = "Click connects";
    public const string SettingsHandBack = "Hand back";
    public const string SettingsShortcuts = "Shortcuts";
    public const string SettingsUpdates = "Updates";
    public const string SettingsWaitsOnInEar = "Earshot cannot yet tell when a bud is in your ear.";
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

    // Settings rows are an icon and one to three words. What used to be the row's description is its tooltip, and
    // every control has an accessible name that says the whole thing, because an icon says nothing to a screen reader.
    public const string SettingsOrder = "Order";
    public const string SettingsAutoCheck = "Auto check";
    public const string SettingsMicOff = "Microphone off";
    public const string SettingsSoundSettings = "Sound settings";
    public const string OpenButton = "Open";
    public const string TipSettings = "Settings";
    public const string TipBack = "Back";
    public const string TipGaugePosition = "Where the gauge sits on the taskbar";
    public const string TipGaugeDisplay = "Which display's taskbar shows the gauge";
    public const string TipGaugeOrder = "Pick how the gauge's ring, number and bolt line up";
    public const string TipOtherDevice = "The name shown when your AirPods are on another device";
    public const string TipPauseBud = "Pause when a bud comes out, and play again when it goes back";
    public const string TipPauseLeave = "Pause this PC when the AirPods move to another device. It never plays again by itself.";
    public const string TipLowBattery = "Alert me at this level";
    public const string TipLeftClick = "A left click on the gauge connects or disconnects";
    public const string TipHandBack = "Let go of the AirPods at shut down, sleep and Exit";
    public const string TipConnectShortcut = "Shortcut to connect";
    public const string TipDisconnectShortcut = "Shortcut to disconnect";
    public const string TipClear = "Clear";
    public const string TipCheckForUpdates = "Check for a newer version now";
    public const string TipRepair = RepairSub;
    public const string TipAutoCheck = "Look for a newer version once a day";
    public const string TipMicOff = "Keep the Hands-Free link up so Windows can read the battery, and turn only the AirPods microphone off in Windows' sound settings. Off by default.";
    public const string TipSoundSettings = "Open Windows sound settings at the AirPods microphone";
    public const string NameGaugePosition = "Gauge position";
    public const string NameGaugeDisplay = "Gauge display";
    public const string NameGaugeOrder = "Gauge order";
    public const string NameOtherDeviceBox = "Other device name";
    public const string NamePauseBud = "Pause when a bud comes out";
    public const string NamePauseLeave = "Pause when AirPods leave this PC";
    public const string NameLowBattery = "Low battery alert";
    public const string NameLowerLowBattery = "Lower the low battery level";
    public const string NameRaiseLowBattery = "Raise the low battery level";
    public const string NameLeftClick = "Left click connects";
    public const string NameHandBack = "Hand back on shut down, sleep and Exit";
    public const string NameConnectShortcut = "Connect shortcut";
    public const string NameDisconnectShortcut = "Disconnect shortcut";
    public const string NameClearConnectShortcut = "Clear connect shortcut";
    public const string NameClearDisconnectShortcut = "Clear disconnect shortcut";
    public const string NameCheckForUpdates = CheckForUpdates;
    public const string NameRepair = RepairEarshot;
    public const string NameAutoCheck = "Check for updates automatically";
    public const string NameMicOff = "Microphone off mode";
    public const string NameSoundSettings = "Open sound settings";

    // The one line under the Microphone off row while the mode is on: what to do next, or that it is done.
    public const string MicGuidance = "In Sound settings, set the AirPods microphone to Don't allow.";
    public const string MicConnectFirst = "Connect the AirPods, then open sound settings.";
    public const string MicOffInWindows = "Microphone is off in Windows";
    public const string SoundSettingsNotOpened = "Couldn't open sound settings.";

    private const string LeftAirPodLabel = "Left AirPod";
    private const string RightAirPodLabel = "Right AirPod";

    // The update line on the card: the version a check found, from the release itself.
    public static string UpdateAvailable(string version) => "Version " + version + " is available";

    // The update line's words beside its download icon; the full sentence above is its tooltip.
    public static string UpdateAvailableShort(string version) => version + " available";

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
    public static string BatteryReadLine(DateTimeOffset? readAt, DateTimeOffset now) =>
        readAt is null ? BatteryNotReadYet : "Battery read " + Age(readAt.Value, now);

    // The card's read line beside its clock icon: "2 min ago", and "Not read yet" before any reading. The full words are
    // the line's tooltip.
    public static string ReadAge(DateTimeOffset? readAt, DateTimeOffset now) =>
        readAt is null ? NotReadYet : Age(readAt.Value, now);

    public const string NotReadYet = "Not read yet";

    // "2 min ago": the amount in seconds, minutes or hours, rounded to the nearest whole, never negative.
    private static string Age(DateTimeOffset at, DateTimeOffset now)
    {
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
        return amount + " ago";
    }

    // The gauge's tooltip. Three lines when there is a reading, one line otherwise.
    public const string GaugeAirPods = "AirPods";
    public const string GaugeCharging = "Charging";
    public const string GaugeLowBattery = "Low battery";
    public const string GaugeNotOnThisPc = "Not on this PC";
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

    // Windows' own figure for the AirPods as a headset: one number, not a bud's or the case's.
    public static string WindowsReads(int percent) =>
        "Windows reads " + percent.ToString(CultureInfo.InvariantCulture) + "%";

    // The low battery alert's line for that same figure.
    public static string LowBatteryHeadsetText(int percent) => LowBatteryText("AirPods", percent);

    // Refresh: the menu item (and the name of the card's icon), and what a refresh that heard nothing says.
    public const string RefreshBattery = "Refresh battery";
    public const string OpenTheCase = "Open the case";
    public const string BluetoothIsOff = "Bluetooth is off";
    public const string NotListening = "Not listening";
    public const string ReadingBattery = "Reading the battery";
    public static readonly string NothingHeardOpenTheCase = "Nothing heard. " + OpenTheCase;
}
