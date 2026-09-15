using Earshot.Composition;
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

// How the whole tray menu looks, top to bottom. The two separators that are not listed are always shown.
internal sealed record MenuState(
    MenuItemState SafeModeCaption,
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
    public const string Connect = "Connect";
    public const string Disconnect = "Disconnect";
    public const string BlockAtBoot = "Block at boot";
    public const string ProtectAudioQuality = "Protect audio quality";
    public const string ProtectCaveat = "Turns off the AirPods microphone";
    public const string OpenOnStartup = "Open on startup";
    public const string ChooseDevice = "Choose device...";
    public const string SetUpEarshot = "Set up Earshot...";
    public const string Exit = "Exit";

    // Shown at the top only in safe mode, so a test run cannot be mistaken for a real one.
    public static readonly string SafeModeCaption = SafeDecorators.Message.TrimEnd('.');

    public static MenuState Build(
        DeviceSnapshot snapshot,
        BootBlockStatus? block,
        AudioProtectionSnapshot? protection,
        EarshotSettings settings,
        bool busy,
        bool safeMode,
        StartupState startup)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        ConnectionState connection = snapshot.Target?.Connection ?? ConnectionState.Unknown;
        bool changing = connection is ConnectionState.Connecting or ConnectionState.Disconnecting;
        bool connected = connection == ConnectionState.Connected;

        // Before setup the gate config does not exist, so the shipped default is shown.
        bool blockAtBoot = block?.BlockAtBoot ?? new GateConfig().BlockAtBoot;

        return new MenuState(
            SafeModeCaption: new MenuItemState(SafeModeCaption, Checked: false, Enabled: false, Visible: safeMode),
            Toggle: new MenuItemState(connected ? Disconnect : Connect, Checked: false, Enabled: !busy && !changing, Visible: true),
            BlockAtBoot: new MenuItemState(BlockAtBoot, Checked: blockAtBoot, Enabled: !busy, Visible: true),
            ProtectAudio: new MenuItemState(
                ProtectAudioQuality,
                Checked: settings.ProtectAudioQuality,
                Enabled: !busy,
                Visible: true,
                Indeterminate: ProtectionDisagrees(settings.ProtectAudioQuality, protection)),
            ProtectCaveat: new MenuItemState(ProtectCaveat, Checked: false, Enabled: false, Visible: true),
            OpenOnStartup: new MenuItemState(OpenOnStartup, Checked: startup == StartupState.On, Enabled: !busy, Visible: true),
            ChooseDevice: new MenuItemState(ChooseDevice, Checked: false, Enabled: true, Visible: true),
            SetUp: new MenuItemState(SetUpEarshot, Checked: false, Enabled: !busy, Visible: block?.State == BlockState.NotSetUp),
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
