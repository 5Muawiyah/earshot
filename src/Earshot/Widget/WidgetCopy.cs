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
    public const string CardApp = "Earshot";

    // The three column labels on the widget card, matching the mockup exactly (Case, not "Case column" or
    // similar).
    public const string LeftWord = "Left";
    public const string RightWord = "Right";
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
    public const string SettingsGaugeDisplay = "Display";
    public const string DisplayChosenNotConnected = "That display is not connected. The gauge is on the main display.";
    public const string DisplayChosenNoTaskbar = "That display shows no taskbar. The gauge is on the main display.";
    public const string PositionRightEnd = "Right end";
    public const string PositionNextToApps = "Next to apps";
    public const string SettingsOtherDevice = "Other device name";
    public const string SettingsPauseBud = "Pause when a bud comes out";
    public const string SettingsPauseLeave = "Pause when AirPods leave";
    public const string SettingsLowBattery = "Low battery alerts";
    public const string SettingsLeftClick = "Left click connects";
    public const string SettingsHandBack = "Hand back";
    public const string SettingsShortcuts = "Shortcuts";
    public const string SettingsGroupTaskbar = "Taskbar";
    public const string SettingsGroupBehaviour = "Behaviour";
    public const string SettingsGroupAudio = "Audio";
    public const string SettingsAbout = "About";
    public const string SettingsMore = "More";
    public const string SettingsFullyCharged = "Fully charged notice";
    public const string SettingsHistory = "Battery history";
    public const string HistoryTitle = "Battery history";
    public const string SettingsCopyDiagnostics = "Copy diagnostics";
    public const string CopyButton = "Copy";
    public const string SettingsHandBackCaption = "Shut down, sleep, Exit";
    public const string SettingsWhatsNew = "What's new";
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
    public const string SettingsOrder = "Gauge order";
    public const string SettingsAutoCheck = "Check automatically";
    public const string SettingsMicOff = "Microphone off";
    public const string SettingsSoundSettings = "Sound settings";
    public const string OpenButton = "Open";
    public const string TipSettings = "Settings";
    public const string TipBack = "Back";
    public const string TipGaugePosition = "Where the gauge sits on the taskbar";
    public const string TipGaugeDisplay = "Which display's taskbar shows the gauge. All displays puts it on every taskbar at once.";
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
    public const string TipMicOff = "Keep the Hands-Free link up, which may let Windows read the battery, and turn only the AirPods microphone off in Windows' sound settings. Off by default.";
    public const string TipFullyCharged = "Tell me when the AirPods are fully charged";
    public const string NameFullyCharged = "Fully charged notice";
    public const string TipHistory = "See how the battery has gone";
    public const string NameHistory = "Battery history";
    public const string TipCopyDiagnostics = "Copy redacted diagnostics to the clipboard";
    public const string NameCopyDiagnostics = "Copy diagnostics";
    public const string TipMore = "Less used settings";
    public const string NameMore = "More settings";
    public const string TipAbout = "Version, updates and repair";
    public const string NameAbout = "Updates";
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
    public const string BluetoothSettingsNotOpened = "Couldn't open Bluetooth settings.";
    public const string WhatsNewNotOpened = "Couldn't open the release notes.";
    public const string SoundSettingsNotOpened = "Couldn't open sound settings.";

    // The battery history page: the two step buttons' names, the day names and the chart's accessible words.
    public const string HistoryEarlier = "Previous day";
    public const string HistoryLater = "Next day";
    public const string HistoryToday = "Today";
    public const string HistoryYesterday = "Yesterday";
    public const string HistoryChartName = "Battery history";
    public const string HistoryNothingHeard = "Nothing heard";

    // "Nothing heard in 2 stretches": how many gaps the chart marks.
    public static string HistoryGaps(int count) =>
        HistoryNothingHeard + " in " + count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " stretch." : " stretches.");

    // What a screen reader says for a battery value. It cannot read "≈" as "about", so the spoken form says it in words:
    // "80%", or "about 80%" for an estimate.
    public static string SpokenPercent(int percent, bool estimated) =>
        (estimated ? "about " : "") + percent.ToString(CultureInfo.InvariantCulture) + "%";

    // One part as it is said: "Left 70%, charging" for a live value, "Left about 90%, estimated, read 2 h ago" for an estimate (the
    // age of the reading it grew from), "Left 70%, last read 2 h ago" for an old reading, "Left, no reading" for none. The charging
    // flag is left out when withCharging is false (the gauge says it once for the pair).
    public static string SpokenReading(string label, ShownPart part, DateTimeOffset now, bool withCharging = true)
    {
        if (part.Percent is not int percent)
        {
            return label + ", " + NoReading.ToLowerInvariant();
        }

        string text = label + " " + SpokenPercent(percent, part.Estimated);
        if (withCharging && part.Charging == true)
        {
            text += ", charging";
        }

        if (part.Estimated)
        {
            text += ", estimated";
        }

        if (!part.Fresh && part.ReadAt is DateTimeOffset at)
        {
            text += (part.Estimated ? ", read " : ", last read ") + StaleAgeAmount(now - at) + " ago";
        }

        return text;
    }

    // The case-open card's row on the settings page, the choices in its expander, and its close button.
    public const string SettingsCaseCard = "Case-open card";
    public const string SettingsCaseCardClose = "Close";
    public const string SettingsCaseCardDisplays = "Displays";
    public const string CaseCardUntilCaseCloses = "Until the case closes";
    public const string CaseCardWhereTheGaugeIs = "Where the gauge is";
    public const string CaseCardAllDisplays = "All displays";
    public const string CaseCardChosenDisplays = "Chosen displays";
    public const string TipCaseCard = "Show the battery when you open the case near this PC";
    public const string TipCaseCardClose = "When the card closes by itself. It also closes when the case closes.";
    public const string TipCaseCardDisplays = "Which displays show the card";
    public const string TipCaseCardDisplay = "Show the card on this display";
    public const string NameCaseCard = "Case-open card";
    public const string NameCaseCardClose = "When the case-open card closes";
    public const string NameCaseCardDisplays = "Displays for the case-open card";
    public const string NameCaseCardMore = "More case-open card settings";
    public const string TipCaseCardMore = "Close and display choices";
    public const string TipCloseCard = "Close";

    // What a screen reader says once when the case-open card shows: "AirPods case open. Left 70%, Right 70%, Case 50%." A
    // part that is not live says what kind of value it is and how old, as its column's tooltip does, and one with no value
    // says so.
    public static string CaseOpenAnnouncement(ShownBattery shown, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(shown);
        return "AirPods case open. " +
            SpokenPart("Left", shown.Left, now) + ", " + SpokenPart("Right", shown.Right, now) + ", " + SpokenPart(CaseLabel, shown.Case, now) + ".";
    }

    private static string SpokenPart(string label, ShownPart part, DateTimeOffset now)
    {
        if (part.Percent is not int percent)
        {
            return label + " " + NoReading.ToLowerInvariant();
        }

        string value = label + " " + PercentText(percent, part.Estimated);
        return PartTip(part, now) is { } kind ? value + " (" + kind.ToLowerInvariant() + ")" : value;
    }

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

    // The fully charged notice's line: "Left AirPod at 100%, fully charged" for a live reading, and for an estimate
    // "Left AirPod ≈100%, estimated from a reading 2 h ago", with the sign before the value as everywhere an estimate is
    // shown, and the age of the reading the estimate grew from (the notice's own time, StaleAgeAmount), as every estimate carries.
    public static string FullyChargedLeftText(bool estimated, TimeSpan age) => FullyChargedText(LeftAirPodLabel, estimated, age);

    public static string FullyChargedRightText(bool estimated, TimeSpan age) => FullyChargedText(RightAirPodLabel, estimated, age);

    public static string FullyChargedCaseText(bool estimated, TimeSpan age) => FullyChargedText(CaseLabel, estimated, age);

    private static string FullyChargedText(string partLabel, bool estimated, TimeSpan age) =>
        estimated
            ? partLabel + " " + EstimateSign + "100%, estimated from a reading " + StaleAgeAmount(age) + " ago"
            : partLabel + " at 100%, fully charged";

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

    // The card's full read line: "Battery read 4 min ago", "Battery not read yet" before any reading, and the empty text while
    // the reading is fresh (see ReadAge). Never "live".
    public static string BatteryReadLine(DateTimeOffset? readAt, DateTimeOffset now) =>
        readAt is null ? BatteryNotReadYet : IsFreshAge(readAt.Value, now) ? "" : "Battery read " + StaleAge(readAt.Value, now);

    // The card's read line beside its clock icon: "2 min ago", "Not read yet" before any reading, and the empty text while the
    // reading is fresh. The full words are the line's tooltip.
    //
    // Fresh means within BatteryFreshness.FreshWindow, the age at which the card greys a value. A fresh value shows no age: the
    // line is hidden but its place is kept (a design choice, the card's "fresh: read-time line hidden but reserved"). Every
    // message moves the newest read time, which is the radio's own timestamp and so a varying latency behind the clock, so a
    // line that counted seconds changed text at message cadence and the card twitched. A reading that is not fresh says its age
    // at the level of minutes (StaleAgeAmount), which changes at most once a minute.
    public static string ReadAge(DateTimeOffset? readAt, DateTimeOffset now) =>
        readAt is null ? NotReadYet : IsFreshAge(readAt.Value, now) ? "" : StaleAge(readAt.Value, now);

    public const string NotReadYet = "Not read yet";

    // The same boundary BatteryFreshness.IsFresh uses (a reading from the future is fresh, never a negative age).
    private static bool IsFreshAge(DateTimeOffset at, DateTimeOffset now) => now - at <= BatteryFreshness.FreshWindow;

    // "2 min ago": for a reading that is not fresh, in minutes, hours or days, rounded to the nearest whole.
    private static string StaleAge(DateTimeOffset at, DateTimeOffset now) => StaleAgeAmount(now - at) + " ago";

    // "under 1 min", "2 min", "3 h", "2 d": how long ago, for a value that is not fresh. Never seconds, so the text a stale value
    // draws does not change from one second to the next. Not fresh means more than FreshWindow (30 s), so the first step
    // below a minute is the only one the seconds could have shown.
    public static string StaleAgeAmount(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "under 1 min" : AgeAmount(age);

    // "40 s", "2 min", "3 h", "2 d": how long ago, without the "ago". Days from a day on, since a last reading may be kept
    // for as long as nothing newer is heard.
    public static string AgeAmount(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalDays >= 1
            ? Round(age.TotalDays) + " d"
            : age.TotalHours >= 1
                ? Round(age.TotalHours) + " h"
                : age.TotalMinutes >= 1
                    ? Round(age.TotalMinutes) + " min"
                    : Round(Math.Max(0, age.TotalSeconds)) + " s";
    }

    // The gauge's tooltip. Three lines when there is a reading, one line otherwise.
    public const string GaugeAirPods = "AirPods";
    public const string GaugeLowBattery = "Low battery";

    // The tooltip's first line, as the design words it: "AirPods 60%" and "AirPods 60%, charging", "Low battery 15%" while low. An
    // estimate has "≈" before its value.
    public static string GaugeHead(bool low, int percent, bool estimated, bool charging) =>
        (low ? GaugeLowBattery : GaugeAirPods) + " " + PercentText(percent, estimated) + (charging ? ", charging" : "");

    // "AirPods, on iPhone" while the AirPods are in use on the other device, or "AirPods, on another device" with no name given.
    public static string GaugeOnElsewhere(string otherDeviceLabel) =>
        GaugeAirPods + ", on " + (string.IsNullOrWhiteSpace(otherDeviceLabel) ? "another device" : otherDeviceLabel.Trim());
    public const string GaugeNotOnThisPc = "Not on this PC";
    public const string GaugeNoRecentReading = "No recent reading";

    // "L 70%   R 60%": the buds that have a reading, left first, three spaces apart, an estimate with "≈" before it. A bud
    // with none is left out, never shown as a dash or a guess.
    public static string GaugeBudsLine(int? left, int? right, bool leftEstimated = false, bool rightEstimated = false)
    {
        string l = left is { } lp ? LeftLabel + " " + PercentText(lp, leftEstimated) : "";
        string r = right is { } rp ? RightLabel + " " + PercentText(rp, rightEstimated) : "";
        return l.Length > 0 && r.Length > 0 ? l + "   " + r : l + r;
    }

    // The sign an estimate carries before its value, wherever it is shown.
    public const string EstimateSign = "≈";

    // "80%", or "≈80%" for an estimate.
    public static string PercentText(int percent, bool estimated) =>
        (estimated ? EstimateSign : "") + percent.ToString(CultureInfo.InvariantCulture) + "%";

    // "Case 80%" or "Case ≈90%", for the gauge's tooltip while the AirPods are away.
    public static string GaugeCaseLine(int percent, bool estimated) => CaseLabel + " " + PercentText(percent, estimated);

    // The tooltip's age line for what kind of value the number is: "Read just now" and the like for a live one, "Last
    // read 2 h ago" for a last reading, "Estimated, read 2 h ago" for an estimate (the age of the reading it grew from).
    public static string GaugeAgeLine(ReadingKind kind, TimeSpan age) => kind switch
    {
        ReadingKind.Live => GaugeReadLine(age),
        ReadingKind.Estimated => EstimatedReadLine(age),
        _ => LastReadLine(age),
    };

    public static string LastReadLine(TimeSpan age) => "Last read " + StaleAgeAmount(age) + " ago";

    public static string EstimatedReadLine(TimeSpan age) => "Estimated, read " + StaleAgeAmount(age) + " ago";

    // A card column's percent line: "80%" while live, "80% · 2 h" for a last reading and "≈90% · 2 h" for an estimate, the
    // age being that of the reading shown or grown from. NoReading when the part has none.
    public static string PartLine(ShownPart part, DateTimeOffset now)
    {
        if (part.Percent is not int percent)
        {
            return NoReading;
        }

        string value = PercentText(percent, part.Estimated);
        return part.Fresh || part.ReadAt is not DateTimeOffset at ? value : value + " · " + StaleAgeAmount(now - at);
    }

    // A card column's tooltip, a sentence: "Left 70%, charging, read 4 min ago". An estimate says so and gives the age of the
    // reading it grew from; a live value gives no age. Null when the part has no value.
    public static string? ColumnTip(string label, ShownPart part, DateTimeOffset now)
    {
        if (part.Percent is not int percent)
        {
            return null;
        }

        string tip = label + " " + PercentText(percent, part.Estimated);
        if (part.Charging == true)
        {
            tip += ", charging";
        }

        if (part.Estimated)
        {
            tip += ", estimated";
        }

        if (!part.Fresh && part.ReadAt is DateTimeOffset at)
        {
            tip += ", read " + StaleAgeAmount(now - at) + " ago";
        }

        return tip;
    }

    // A card column's tooltip: what kind of value it is and how old, or null for a live value or none.
    public static string? PartTip(ShownPart part, DateTimeOffset now) =>
        !part.HasValue || part.Fresh || part.ReadAt is not DateTimeOffset at ? null
        : part.Estimated ? EstimatedReadLine(now - at)
        : LastReadLine(now - at);

    // "Read just now" only while the reading is current (within BatteryFreshness.FreshWindow, the age at which the card
    // greys it), then "Read 45 s ago" inside the minute and "Read 2 min ago" after it (whole seconds and minutes,
    // rounded down, so the line is never younger than the reading).
    public static string GaugeReadLine(TimeSpan age)
    {
        if (age <= BatteryFreshness.FreshWindow)
        {
            return "Read just now";
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return "Read " + ((long)Math.Floor(age.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " s ago";
        }

        return "Read " + ((long)Math.Floor(age.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " min ago";
    }

    // The percent for one part, or NoReading when it has none.
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

    // What the card and the gauge say while the AirPods are connected and no pair is linked to show a battery for: opening
    // the case next to the PC is what links one.
    public const string OpenTheCaseToShowBattery = "Open the case to show battery";
    public const string BluetoothIsOff = "Bluetooth is off";

    // The status row's words while Bluetooth is off.
    public const string BluetoothOff = "Bluetooth off";
    public const string TipBluetoothSettings = "Open Bluetooth settings";
    public const string NotListening = "Not listening";
    public const string ReadingBattery = "Reading the battery";
    public static readonly string NothingHeardOpenTheCase = "Nothing heard. " + OpenTheCase + " near this PC";
}
