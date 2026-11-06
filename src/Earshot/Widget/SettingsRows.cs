using IconId = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Widget;

// What each settings row shows beside its label and says to a screen reader: the icon (null for a row with none), the tooltip
// that holds what used to be the row's description, and the accessible name. The icons are the catalogue's (Earshot.Icons).
internal sealed record SettingsRowInfo(IconId? Glyph, string? Tip, string? Name);

internal static class SettingsRows
{
    public static SettingsRowInfo For(SettingsRowId row) => row switch
    {
        SettingsRowId.GaugePosition => new(IconId.LayoutBottom, WidgetCopy.TipGaugePosition, WidgetCopy.NameGaugePosition),
        SettingsRowId.GaugeDisplay => new(IconId.Monitor, WidgetCopy.TipGaugeDisplay, WidgetCopy.NameGaugeDisplay),
        SettingsRowId.GaugeOrder => new(IconId.Transfer, WidgetCopy.TipGaugeOrder, WidgetCopy.NameGaugeOrder),
        SettingsRowId.OtherDevice => new(IconId.Cellphone, WidgetCopy.TipOtherDevice, WidgetCopy.NameOtherDeviceBox),
        SettingsRowId.PauseBud => new(IconId.AirPods, WidgetCopy.TipPauseBud, WidgetCopy.NamePauseBud),
        SettingsRowId.PauseLeave => new(IconId.Pause, WidgetCopy.TipPauseLeave, WidgetCopy.NamePauseLeave),
        SettingsRowId.LowBattery => new(IconId.Notification, WidgetCopy.TipLowBattery, WidgetCopy.NameLowBattery),
        SettingsRowId.LeftClick => new(IconId.Mouse, WidgetCopy.TipLeftClick, WidgetCopy.NameLeftClick),
        SettingsRowId.HandBack => new(IconId.Power, WidgetCopy.TipHandBack, WidgetCopy.NameHandBack),
        SettingsRowId.Connect => new(IconId.Keyboard, WidgetCopy.TipConnectShortcut, WidgetCopy.NameConnectShortcut),
        SettingsRowId.Disconnect => new(IconId.Keyboard, WidgetCopy.TipDisconnectShortcut, WidgetCopy.NameDisconnectShortcut),
        SettingsRowId.OpenCard => new(IconId.Keyboard, ShortcutCopy.TipCardShortcut, ShortcutCopy.NameCardShortcut),
        SettingsRowId.SendTo => new(IconId.Keyboard, SendToCopy.TipShortcut, SendToCopy.NameShortcut),
        SettingsRowId.SendToCard => new(IconId.Send, SendToCopy.TipCardRow, SendToCopy.NameCardRow),
        SettingsRowId.SendToGauge => new(IconId.Send, SendToCopy.TipGaugeMenu, SendToCopy.NameGaugeMenu),
        SettingsRowId.Repair => new(IconId.Tool, WidgetCopy.TipRepair, WidgetCopy.NameRepair),
        SettingsRowId.CheckAutomatically => new(IconId.Sync, WidgetCopy.TipAutoCheck, WidgetCopy.NameAutoCheck),
        SettingsRowId.MicrophoneOff => new(IconId.MicOff, WidgetCopy.TipMicOff, WidgetCopy.NameMicOff),
        SettingsRowId.SoundSettings => new(IconId.Volume, WidgetCopy.TipSoundSettings, WidgetCopy.NameSoundSettings),
        SettingsRowId.CaseCard => new(IconId.AirPodsCase, WidgetCopy.TipCaseCard, WidgetCopy.NameCaseCard),
        SettingsRowId.FullyCharged => new(IconId.Lightning, WidgetCopy.TipFullyCharged, WidgetCopy.NameFullyCharged),
        SettingsRowId.MovedTo => new(IconId.Cellphone, WidgetCopy.TipMovedTo, WidgetCopy.NameMovedTo),
        SettingsRowId.History => new(IconId.History, WidgetCopy.TipHistory, WidgetCopy.NameHistory),
        SettingsRowId.CopyDiagnostics => new(IconId.Copy, WidgetCopy.TipCopyDiagnostics, WidgetCopy.NameCopyDiagnostics),
        SettingsRowId.More => new(IconId.More, WidgetCopy.TipMore, WidgetCopy.NameMore),
        SettingsRowId.About => new(IconId.Sync, WidgetCopy.TipAbout, WidgetCopy.NameAbout),
        SettingsRowId.CaseCardClose => new(null, WidgetCopy.TipCaseCardClose, WidgetCopy.NameCaseCardClose),
        SettingsRowId.CaseCardDisplays => new(null, WidgetCopy.TipCaseCardDisplays, WidgetCopy.NameCaseCardDisplays),
        SettingsRowId.CaseCardDisplay => new(null, WidgetCopy.TipCaseCardDisplay, null),
        _ => new(null, null, null),
    };

    // The accessible name of one control of a row. The row's own name for most; the controls that share a row say
    // which of them they are.
    public static string NameOf(SettingsRowId row, SettingsPart part, int index = 0) => (row, part) switch
    {
        (SettingsRowId.None, SettingsPart.Back) => WidgetCopy.TipBack,
        (SettingsRowId.LowBattery, SettingsPart.Minus) => WidgetCopy.NameLowerLowBattery,
        (SettingsRowId.LowBattery, SettingsPart.Plus) => WidgetCopy.NameRaiseLowBattery,
        (SettingsRowId.Connect, SettingsPart.Clear) => WidgetCopy.NameClearConnectShortcut,
        (SettingsRowId.Disconnect, SettingsPart.Clear) => WidgetCopy.NameClearDisconnectShortcut,
        (SettingsRowId.OpenCard, SettingsPart.Clear) => ShortcutCopy.NameClearCardShortcut,
        (SettingsRowId.SendTo, SettingsPart.Clear) => SendToCopy.NameClearShortcut,
        (SettingsRowId.CaseCard, SettingsPart.Expand) => WidgetCopy.NameCaseCardMore,
        (SettingsRowId.More, SettingsPart.Expand) => WidgetCopy.NameMore,
        (SettingsRowId.GaugeOrder, SettingsPart.Expand) => WidgetCopy.NameGaugeOrder,
        (SettingsRowId.GaugeOrder, SettingsPart.Tile) => GaugeOrders.AccessibleName(GaugeOrders.FromStored((GaugeOrder)index)),
        _ => For(row).Name ?? string.Empty,
    };

    // The tooltip of one control of a row, or null when it needs none. A control that is only an icon, or only a
    // picture, always has one.
    public static string? TipOf(SettingsRowId row, SettingsPart part, int index = 0) => (row, part) switch
    {
        (SettingsRowId.None, SettingsPart.Back) => WidgetCopy.TipBack,
        (SettingsRowId.Connect, SettingsPart.Clear) or (SettingsRowId.Disconnect, SettingsPart.Clear) or (SettingsRowId.OpenCard, SettingsPart.Clear) or (SettingsRowId.SendTo, SettingsPart.Clear) => WidgetCopy.TipClear,
        (SettingsRowId.GaugeOrder, SettingsPart.Tile) => GaugeOrders.AccessibleName(GaugeOrders.FromStored((GaugeOrder)index)),
        (SettingsRowId.CaseCard, SettingsPart.Expand) => WidgetCopy.TipCaseCardMore,
        _ => For(row).Tip,
    };
}
