namespace Earshot.Icons.Catalogue
{
    // Every icon Earshot draws, named for what it shows. IconCatalogue says which MingCute file each is cut from. The name is Icon,
    // in this namespace of its own: in Earshot.Icons it would hide System.Drawing.Icon from the tray's icon code, which names that
    // type without a qualifier. A file that draws and also uses System.Drawing says `using Icon = Earshot.Icons.Catalogue.Icon;`.
    internal enum Icon
    {
        Settings,
        Refresh,
        Sync,
        Back,
        ChevronLeft,
        ChevronRight,
        ChevronDown,
        ChevronUp,
        Transfer,
        Send,
        LayoutBottom,
        AirPodsCase,
        Battery,
        Bluetooth,
        BluetoothOff,
        BluetoothSearching,
        Cellphone,
        Computer,
        Device,
        Keyboard,
        Monitor,
        Mouse,
        Copy,
        Download,
        ExternalLink,
        AirPods,
        AirPodsLeft,
        AirPodsRight,
        Announcement,
        Headphone,
        MicOff,
        Notification,
        Pause,
        PlayCircle,
        Volume,
        Add,
        Alert,
        Check,
        CheckCircle,
        Close,
        Exit,
        History,
        Information,
        Lock,
        Minimize,
        More,
        Power,
        SafeShield,
        SafeShield2,
        Time,
        Tool,
        Warning,
        Rocket,
        Lightning,
    }
}

namespace Earshot.Icons
{
    using Earshot.Icons.Catalogue;

    // The set has a line and a filled drawing of some icons. The filled one marks an icon that is on, current or connected.
    internal enum IconStyle
    {
        Line,
        Filled,
    }

    // How the 24 unit grid meets the box an icon is drawn in. A: the set's art area, units 2 to 22, fills the box (the card, the
    // gauge, the tray). B: the whole grid fills the box, so the art keeps its two unit margin (the sub-page status icons and the
    // tray menu).
    internal enum IconRule
    {
        A,
        B,
    }

    // Which MingCute outline each icon is, in each style. An icon with no filled drawing is drawn as its line drawing when the
    // filled one is asked for. AirPodsLeft and AirPodsRight are the two buds of AirPods, each drawn centred on its own bounds.
    internal static class IconCatalogue
    {
        public static IReadOnlyList<Icon> All { get; } = Enum.GetValues<Icon>();

        public static bool HasFilled(Icon icon) => icon is
            Icon.Send or Icon.LayoutBottom or Icon.AirPodsCase or Icon.Cellphone or Icon.Computer or Icon.Mouse
            or Icon.AirPods or Icon.AirPodsLeft or Icon.AirPodsRight or Icon.Announcement or Icon.Headphone or Icon.MicOff
            or Icon.Notification or Icon.Pause or Icon.Lock or Icon.Power or Icon.Sync or Icon.Rocket or Icon.Lightning;

        // The two buds are placed by their own bounds, not by their place in the pair.
        public static bool IsBud(Icon icon) => icon is Icon.AirPodsLeft or Icon.AirPodsRight;

        public static IconOutline Outline(Icon icon, IconStyle style) =>
            style == IconStyle.Filled && HasFilled(icon) ? Filled(icon) : Line(icon);

        private static IconOutline Line(Icon icon) => icon switch
        {
            Icon.Settings => MingCuteIcons.RegularSystemSettings3,
            Icon.Refresh => MingCuteIcons.RegularSystemRefresh1,
            Icon.Sync => MingCuteIcons.RegularSystemRefresh2,
            Icon.Back => MingCuteIcons.RegularArrowArrowLeft,
            Icon.ChevronLeft => MingCuteIcons.RegularArrowLeft,
            Icon.ChevronRight => MingCuteIcons.RegularArrowRight,
            Icon.ChevronDown => MingCuteIcons.RegularArrowDown,
            Icon.ChevronUp => MingCuteIcons.RegularArrowUp,
            Icon.Transfer => MingCuteIcons.RegularArrowTransfer,
            Icon.Send => MingCuteIcons.RegularCommunicationSend,
            Icon.LayoutBottom => MingCuteIcons.RegularDesignLayoutBottom,
            Icon.AirPodsCase => MingCuteIcons.RegularDevicesAirpods2,
            Icon.Battery => MingCuteIcons.RegularDevicesBattery1,
            Icon.Bluetooth => MingCuteIcons.RegularDevicesBluetooth,
            Icon.BluetoothOff => MingCuteIcons.RegularDevicesBluetoothOff,
            Icon.BluetoothSearching => MingCuteIcons.RegularDevicesBluetoothSearching,
            Icon.Cellphone => MingCuteIcons.RegularDevicesCellphone,
            Icon.Computer => MingCuteIcons.RegularDevicesComputer,
            Icon.Device => MingCuteIcons.RegularDevicesDevice,
            Icon.Keyboard => MingCuteIcons.RegularDevicesKeyboard,
            Icon.Monitor => MingCuteIcons.RegularDevicesMonitor,
            Icon.Mouse => MingCuteIcons.RegularDevicesMouse,
            Icon.Copy => MingCuteIcons.RegularFilesCopy,
            Icon.Download => MingCuteIcons.RegularFilesDownload2,
            Icon.ExternalLink => MingCuteIcons.RegularFilesExternalLink,
            Icon.AirPods => MingCuteIcons.RegularMediaAirpods,
            Icon.AirPodsLeft => MingCuteIcons.RegularMediaAirpodsLeft,
            Icon.AirPodsRight => MingCuteIcons.RegularMediaAirpodsRight,
            Icon.Announcement => MingCuteIcons.RegularMediaAnnouncement,
            Icon.Headphone => MingCuteIcons.RegularMediaHeadphone,
            Icon.MicOff => MingCuteIcons.RegularMediaMicOff,
            Icon.Notification => MingCuteIcons.RegularMediaNotification,
            Icon.Pause => MingCuteIcons.RegularMediaPause,
            Icon.PlayCircle => MingCuteIcons.RegularMediaPlayCircle,
            Icon.Volume => MingCuteIcons.RegularMediaVolume,
            Icon.Add => MingCuteIcons.RegularSystemAdd,
            Icon.Alert => MingCuteIcons.RegularSystemAlert,
            Icon.Check => MingCuteIcons.RegularSystemCheck,
            Icon.CheckCircle => MingCuteIcons.RegularSystemCheckCircle,
            Icon.Close => MingCuteIcons.RegularSystemClose,
            Icon.Exit => MingCuteIcons.RegularSystemExit,
            Icon.History => MingCuteIcons.RegularSystemHistory,
            Icon.Information => MingCuteIcons.RegularSystemInformation,
            Icon.Lock => MingCuteIcons.RegularSystemLock,
            Icon.Minimize => MingCuteIcons.RegularSystemMinimize,
            Icon.More => MingCuteIcons.RegularSystemMore1,
            Icon.Power => MingCuteIcons.RegularSystemPower,
            Icon.SafeShield => MingCuteIcons.RegularSystemSafeShield,
            Icon.SafeShield2 => MingCuteIcons.RegularSystemSafeShield2,
            Icon.Time => MingCuteIcons.RegularSystemTime,
            Icon.Tool => MingCuteIcons.RegularSystemTool,
            Icon.Warning => MingCuteIcons.RegularSystemWarning,
            Icon.Rocket => MingCuteIcons.RegularTransportationRocket,
            Icon.Lightning => MingCuteIcons.RegularWeatherLightning,
            _ => throw new ArgumentOutOfRangeException(nameof(icon), icon, "No outline for this icon."),
        };

        private static IconOutline Filled(Icon icon) => icon switch
        {
            Icon.Send => MingCuteIcons.FilledCommunicationSend,
            Icon.LayoutBottom => MingCuteIcons.FilledDesignLayoutBottom,
            Icon.AirPodsCase => MingCuteIcons.FilledDevicesAirpods2,
            Icon.Cellphone => MingCuteIcons.FilledDevicesCellphone,
            Icon.Computer => MingCuteIcons.FilledDevicesComputer,
            Icon.Mouse => MingCuteIcons.FilledDevicesMouse,
            Icon.AirPods => MingCuteIcons.FilledMediaAirpods,
            Icon.AirPodsLeft => MingCuteIcons.FilledMediaAirpodsLeft,
            Icon.AirPodsRight => MingCuteIcons.FilledMediaAirpodsRight,
            Icon.Announcement => MingCuteIcons.FilledMediaAnnouncement,
            Icon.Headphone => MingCuteIcons.FilledMediaHeadphone,
            Icon.MicOff => MingCuteIcons.FilledMediaMicOff,
            Icon.Notification => MingCuteIcons.FilledMediaNotification,
            Icon.Pause => MingCuteIcons.FilledMediaPause,
            Icon.Lock => MingCuteIcons.FilledSystemLock,
            Icon.Power => MingCuteIcons.FilledSystemPower,
            Icon.Sync => MingCuteIcons.FilledSystemRefresh2,
            Icon.Rocket => MingCuteIcons.FilledTransportationRocket,
            Icon.Lightning => MingCuteIcons.FilledWeatherLightning,
            _ => throw new ArgumentOutOfRangeException(nameof(icon), icon, "No filled outline for this icon."),
        };
    }
}
