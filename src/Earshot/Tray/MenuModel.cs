using Earshot.Contracts;

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

// How the whole tray menu looks, top to bottom. The separators are always shown.
internal sealed record MenuState(
    MenuItemState SafeMode,
    MenuItemState Toggle,
    MenuItemState BlockAtBoot,
    MenuItemState ProtectAudio,
    MenuItemState ProtectCaveat,
    MenuItemState OpenOnStartup,
    MenuItemState ChooseDevice,
    MenuItemState SetUp,
    MenuItemState Exit);

// The tray menu as a pure function of cached state. The menu applies it in its Opening handler, which
// does no I/O and makes no device calls.
internal static class MenuModel
{
    public const string SafeMode = "Safe mode: no device actions";
    public const string Connect = "Connect";
    public const string Disconnect = "Disconnect";
    public const string BlockAtBoot = "Block at boot";
    public const string ProtectAudioQuality = "Protect audio quality";
    public const string ProtectCaveat = "Turns off the AirPods microphone";
    public const string OpenOnStartup = "Open on startup";
    public const string ChooseDevice = "Choose device...";
    public const string SetUpEarshot = "Set up Earshot...";
    public const string Exit = "Exit";

    // safeMode (EARSHOT_SAFE_MODE) adds one caption at the top and changes nothing else: every item stays as it
    // is, and each action reports the refusal on its own card, so the menu that is tested is the menu that ships.
    public static MenuState Build(
        DeviceSnapshot snapshot,
        BootBlockStatus? block,
        AudioProtectionSnapshot? protection,
        EarshotSettings settings,
        bool busy,
        StartupState startup,
        bool safeMode = false)
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
            Toggle: new MenuItemState(connected ? Disconnect : Connect, Checked: false, Enabled: !busy && !changing, Visible: true),
            BlockAtBoot: new MenuItemState(BlockAtBoot, Checked: blockAtBoot, Enabled: !busy, Visible: true, Indeterminate: blockAtBootUnknown),
            ProtectAudio: new MenuItemState(
                ProtectAudioQuality,
                Checked: settings.ProtectAudioQuality,
                Enabled: !busy,
                Visible: true,
                Indeterminate: ProtectionDisagrees(settings.ProtectAudioQuality, protection)),
            ProtectCaveat: new MenuItemState(ProtectCaveat, Checked: false, Enabled: false, Visible: true),
            OpenOnStartup: new MenuItemState(OpenOnStartup, Checked: startup == StartupState.On, Enabled: !busy, Visible: true),
            ChooseDevice: new MenuItemState(ChooseDevice, Checked: false, Enabled: true, Visible: true),
            SetUp: new MenuItemState(SetUpEarshot, Checked: false, Enabled: !busy, Visible: TrayStatus.NeedsSetUp(block)),
            Exit: new MenuItemState(Exit, Checked: false, Enabled: true, Visible: true));
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
}
