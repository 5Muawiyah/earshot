using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Streaming;
using Earshot.Voice;
using Earshot.Widget;

namespace Earshot.Tray;

// Whether Earshot opens when the user signs in, as read from HKCU (see StartupRegistration).
internal enum StartupState
{
    Off,                // no Run value
    On,                 // Run value present and not turned off in Windows
    DisabledInWindows,  // Run value present but turned off in Settings or Task Manager
    Unknown             // the registry could not be read
}

// How one menu item looks.
internal readonly record struct MenuItemState(string Text, bool Checked, bool Enabled, bool Visible, bool Indeterminate = false);

// One entry of the low battery threshold submenu, as data, the same way StreamingMenuItem lets
// TrayMenu draw the Play from a phone submenu without knowing what built it. Text is built here
// (WidgetCopy.Percent), not in TrayMenu, exactly as StreamingMenuModel already builds its own items' text.
internal readonly record struct LowBatteryThresholdMenuItem(string Text, int Percent, bool Checked, bool Enabled);

// How the whole tray menu looks, top to bottom. The separators are always shown.
internal sealed record MenuState(
    MenuItemState SafeMode,
    MenuItemState Toggle,
    MenuItemState PlayFromPhone,
    IReadOnlyList<StreamingMenuItem> PlayFromPhoneItems,
    MenuItemState BlockAtBoot,
    MenuItemState HandBack,
    MenuItemState ProtectAudio,
    MenuItemState ProtectCaveat,
    MenuItemState OpenOnStartup,
    MenuItemState SpeakStatus,
    MenuItemState ShowOnTaskbar,
    MenuItemState LeftClickConnectsItem,
    MenuItemState CaseOpenCardItem,
    MenuItemState LowBatteryAlert,
    MenuItemState LowBatteryThreshold,
    IReadOnlyList<LowBatteryThresholdMenuItem> LowBatteryThresholdItems,
    MenuItemState ClaimAirPodsItem,
    MenuItemState NameOtherDeviceItem,
    MenuItemState ChooseDevice,
    MenuItemState SetUp,
    MenuItemState CheckForUpdates,
    MenuItemState CheckAutomatically,
    MenuItemState Exit);

// The tray menu as a pure function of cached state. The menu applies it in its Opening handler, which
// does no I/O and makes no device calls.
internal static class MenuModel
{
    public const string SafeMode = "Safe mode: no device actions";
    public const string Connect = "Connect";
    public const string Disconnect = "Disconnect";
    // Kept exactly as StreamingLabels has it, referenced rather than duplicated, as with AnnouncerCopy below.
    public const string PlayFromPhone = StreamingLabels.Parent;
    public const string BlockAtBoot = "Block at boot";
    public const string HandBackOnShutdownAndSleep = "Hand back at shut down and sleep";
    public const string ProtectAudioQuality = "Protect audio quality";
    public const string ProtectCaveat = "Turns off the AirPods microphone";
    public const string OpenOnStartup = "Open on startup";
    // Kept exactly as AnnouncerCopy has it, referenced rather than duplicated: AnnouncerCopy is where
    // every string a person hears or reads about VoiceOver lives.
    public const string SpeakStatusText = AnnouncerCopy.MenuItem;
    public const string SpeakStatusNoVoice = AnnouncerCopy.MenuItemNoVoice;
    public const string ChooseDevice = "Choose device...";
    public const string SetUpEarshot = "Set up Earshot...";
    // Kept exactly as UpdateCopy has it, referenced rather than duplicated, so the menu and the update page never
    // word the same thing two ways.
    public const string CheckForUpdates = Earshot.Update.UpdateCopy.CheckRowLabel;
    public const string CheckAutomatically = Earshot.Update.UpdateCopy.CheckAutomaticallyLabel;
    public const string Exit = "Exit";

    // safeMode (EARSHOT_SAFE_MODE) adds one caption at the top and changes nothing else: every item stays as it
    // is, and each action reports the refusal on its own card, so the menu that is tested is the menu that ships.
    //
    // voiceKnownMissing defaults to false, so a caller that omits it (every caller except TrayContext,
    // which passes its own _voiceKnownNoVoice) sees the ordinary "Speak status" label. The parameter used
    // to be voiceAvailable defaulting to false, which meant every caller that left it out, including the
    // pinned TrayMenuTests order, saw the "(no voice)" label by default: a build with a perfectly good
    // voice looked exactly like one with none the moment a caller forgot the argument. Naming and
    // defaulting it the other way round makes the safe default the normal label, and only a caller that
    // actually knows the voice is missing has to say so.
    //
    // streaming is what StreamingCoordinator last built, or null while Play from a phone is switched off, which is
    // the default: the item is then not there at all, and the menu is the one it was before the feature existed.
    // It is cached state like everything else here, so opening the menu still reads no device.
    public static MenuState Build(
        DeviceSnapshot snapshot,
        BootBlockStatus? block,
        AudioProtectionSnapshot? protection,
        EarshotSettings settings,
        bool busy,
        StartupState startup,
        bool safeMode = false,
        bool voiceKnownMissing = false,
        StreamingMenuModel? streaming = null,
        bool claimAvailable = false,
        bool updateInProgress = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        // The same target rule as the left click, so the label always names what the click would do.
        ConnectionState connection = TrayStatus.ActiveTarget(snapshot, settings)?.Connection ?? ConnectionState.Unknown;
        bool changing = connection is ConnectionState.Connecting or ConnectionState.Disconnecting;
        bool connected = connection == ConnectionState.Connected;

        // The check shows what is in force. Before setup nothing blocks at boot, so it is unchecked (clicking it runs
        // setup, which turns it on); before the first status read, or when the setting could not be read, it is not
        // known, so it shows neither state (clicking it then turns it on, which writes the setting again).
        bool blockAtBoot = block is { State: not BlockState.NotSetUp, BlockAtBoot: true };
        bool blockAtBootUnknown = block is null || (block.State != BlockState.NotSetUp && !block.BlockAtBootKnown);

        return new MenuState(
            SafeMode: new MenuItemState(SafeMode, Checked: false, Enabled: false, Visible: safeMode),
            Toggle: new MenuItemState(WithShortcut(connected ? Disconnect : Connect, settings.Hotkeys, HotkeyAction.ToggleConnection), Checked: false, Enabled: !busy && !changing, Visible: true),
            PlayFromPhone: new MenuItemState(streaming?.ParentText ?? PlayFromPhone, Checked: false, Enabled: streaming is { ParentEnabled: true }, Visible: streaming is not null),
            PlayFromPhoneItems: streaming?.Items ?? [],
            BlockAtBoot: new MenuItemState(WithShortcut(BlockAtBoot, settings.Hotkeys, HotkeyAction.ToggleBlockAtBoot), Checked: blockAtBoot, Enabled: !busy, Visible: true, Indeterminate: blockAtBootUnknown),
            HandBack: new MenuItemState(HandBackOnShutdownAndSleep, Checked: settings.HandBackOnShutdownAndSleep, Enabled: !busy, Visible: true),
            ProtectAudio: new MenuItemState(
                WithShortcut(ProtectAudioQuality, settings.Hotkeys, HotkeyAction.ToggleAudioProtection),
                Checked: settings.ProtectAudioQuality,
                Enabled: !busy,
                Visible: true,
                Indeterminate: ProtectionDisagrees(settings.ProtectAudioQuality, protection)),
            ProtectCaveat: new MenuItemState(ProtectCaveat, Checked: false, Enabled: false, Visible: true),
            OpenOnStartup: new MenuItemState(OpenOnStartup, Checked: startup == StartupState.On, Enabled: !busy, Visible: true),
            SpeakStatus: new MenuItemState(
                voiceKnownMissing ? SpeakStatusNoVoice : SpeakStatusText,
                Checked: settings.VoiceOver.Enabled,
                Enabled: !busy && !voiceKnownMissing,
                Visible: true),
            ShowOnTaskbar: new MenuItemState(WidgetCopy.ShowOnTaskbar, Checked: settings.Widget.ShowOnTaskbar, Enabled: !busy, Visible: true),
            LeftClickConnectsItem: new MenuItemState(WidgetCopy.LeftClickConnects, Checked: settings.Widget.LeftClickConnects, Enabled: !busy, Visible: true),
            CaseOpenCardItem: new MenuItemState(WidgetCopy.CardWhenCaseOpens, Checked: settings.Widget.CaseOpenCard, Enabled: !busy, Visible: true),
            LowBatteryAlert: new MenuItemState(WidgetCopy.LowBatteryAlert, Checked: settings.Widget.LowBatteryAlert, Enabled: !busy, Visible: true),
            // A plain submenu opener, never checkable itself: the alert's own on/off state lives on
            // LowBatteryAlert above, not here, so one menu row is never both a toggle and a dropdown parent.
            LowBatteryThreshold: new MenuItemState(WidgetCopy.LowBatteryThreshold, Checked: false, Enabled: !busy && settings.Widget.LowBatteryAlert, Visible: true),
            LowBatteryThresholdItems: LowBatteryThresholdItems(settings.Widget, busy),
            // Never enabled on a guess: disabled, with the plain reason in its own text, until phase 0 has
            // proved a signal threshold to claim against. busy still gates it the same as every other action
            // here, once it is otherwise available, so a claim cannot start while something else is already
            // in flight.
            ClaimAirPodsItem: new MenuItemState(
                claimAvailable ? WidgetCopy.MakeTheseMyAirPods : WidgetCopy.MakeTheseMyAirPods + " (" + WidgetCopy.MakeTheseMyAirPodsDisabledReason + ")",
                Checked: false,
                Enabled: claimAvailable && !busy,
                Visible: true),
            NameOtherDeviceItem: new MenuItemState(WidgetCopy.NameOtherDevice, Checked: false, Enabled: !busy, Visible: true),
            ChooseDevice: new MenuItemState(ChooseDevice, Checked: false, Enabled: true, Visible: true),
            SetUp: new MenuItemState(SetUpEarshot, Checked: false, Enabled: !busy, Visible: TrayStatus.NeedsSetUp(block)),
            // A check reads GitHub and touches no device, so a connect in flight does not stop it; only another
            // update step does.
            CheckForUpdates: new MenuItemState(CheckForUpdates, Checked: false, Enabled: !updateInProgress, Visible: true),
            CheckAutomatically: new MenuItemState(CheckAutomatically, Checked: settings.CheckForUpdatesAutomatically, Enabled: !busy, Visible: true),
            Exit: new MenuItemState(Exit, Checked: false, Enabled: true, Visible: true));
    }

    // The nine percentages the threshold can be set to, checked against the saved value and disabled
    // together while the alert itself is off: a submenu with nothing to choose from would confuse a
    // reader more than it helps them.
    private static List<LowBatteryThresholdMenuItem> LowBatteryThresholdItems(WidgetSettings widget, bool busy)
    {
        var items = new List<LowBatteryThresholdMenuItem>(9);
        for (int percent = 10; percent <= 90; percent += 10)
        {
            items.Add(new LowBatteryThresholdMenuItem(
                WidgetCopy.Percent(percent),
                percent,
                Checked: percent == widget.LowBatteryThresholdPercent,
                Enabled: !busy && widget.LowBatteryAlert));
        }

        return items;
    }

    // The check shows the saved intent. It turns indeterminate when the read-back from the device is
    // partial, or known and different from the intent (for example while the change waits for the next
    // connect). An unknown read-back never overrides the intent.
    internal static bool ProtectionDisagrees(bool intent, AudioProtectionSnapshot? protection) =>
        protection?.State switch
        {
            AudioProtectionState.Partial => true,
            AudioProtectionState.Protected => !intent,
            AudioProtectionState.NotProtected => intent,
            _ => false,
        };

    // Puts the current shortcut text next to each command in the tray menu. Only shown when hotkeys are
    // switched on and the text for this action actually parses: an owner mid-way
    // through typing an invalid shortcut, or with hotkeys off, sees the label exactly as before.
    private static string WithShortcut(string label, HotkeySettings hotkeys, HotkeyAction action)
    {
        if (!hotkeys.Enabled)
        {
            return label;
        }

        string text = hotkeys.TextFor(action);
        if (string.IsNullOrWhiteSpace(text) || !HotkeyText.TryParse(text, out HotkeyCombination combination, out _))
        {
            return label;
        }

        return label + " (" + HotkeyText.Format(combination) + ")";
    }
}
