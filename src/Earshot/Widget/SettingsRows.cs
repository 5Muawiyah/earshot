namespace Earshot.Widget;

// What each settings row shows beside its label and says to a screen reader: the icon, the tooltip that holds what
// used to be the row's description, and the accessible name. The icons are Segoe Fluent Icons code points.
// https://learn.microsoft.com/en-us/windows/apps/design/style/segoe-fluent-icons-font
internal sealed record SettingsRowInfo(char Glyph, string? Tip, string? Name);

internal static class SettingsRows
{
    public static SettingsRowInfo For(SettingsRowId row) => row switch
    {
        SettingsRowId.GaugePosition => new(FluentGlyphs.DockBottom, WidgetCopy.TipGaugePosition, WidgetCopy.NameGaugePosition),
        SettingsRowId.GaugeDisplay => new(FluentGlyphs.TvMonitor, WidgetCopy.TipGaugeDisplay, WidgetCopy.NameGaugeDisplay),
        SettingsRowId.GaugeOrder => new(FluentGlyphs.Sort, WidgetCopy.TipGaugeOrder, WidgetCopy.NameGaugeOrder),
        SettingsRowId.OtherDevice => new(FluentGlyphs.CellPhone, WidgetCopy.TipOtherDevice, WidgetCopy.NameOtherDeviceBox),
        SettingsRowId.PauseBud => new(FluentGlyphs.EarbudForThisPc(), WidgetCopy.TipPauseBud, WidgetCopy.NamePauseBud),
        SettingsRowId.PauseLeave => new(FluentGlyphs.Pause, WidgetCopy.TipPauseLeave, WidgetCopy.NamePauseLeave),
        SettingsRowId.LowBattery => new(FluentGlyphs.Ringer, WidgetCopy.TipLowBattery, WidgetCopy.NameLowBattery),
        SettingsRowId.LeftClick => new(FluentGlyphs.Mouse, WidgetCopy.TipLeftClick, WidgetCopy.NameLeftClick),
        SettingsRowId.HandBack => new(FluentGlyphs.PowerButton, WidgetCopy.TipHandBack, WidgetCopy.NameHandBack),
        SettingsRowId.Connect => new(FluentGlyphs.KeyboardShortcut, WidgetCopy.TipConnectShortcut, WidgetCopy.NameConnectShortcut),
        SettingsRowId.Disconnect => new(FluentGlyphs.KeyboardShortcut, WidgetCopy.TipDisconnectShortcut, WidgetCopy.NameDisconnectShortcut),
        SettingsRowId.OpenCard => new(FluentGlyphs.KeyboardShortcut, ShortcutCopy.TipCardShortcut, ShortcutCopy.NameCardShortcut),
        SettingsRowId.CheckForUpdates => new(FluentGlyphs.Sync, WidgetCopy.TipCheckForUpdates, WidgetCopy.NameCheckForUpdates),
        SettingsRowId.Repair => new(FluentGlyphs.Repair, WidgetCopy.TipRepair, WidgetCopy.NameRepair),
        SettingsRowId.CheckAutomatically => new(FluentGlyphs.UpdateRestore, WidgetCopy.TipAutoCheck, WidgetCopy.NameAutoCheck),
        SettingsRowId.MicrophoneOff => new(FluentGlyphs.MicOff, WidgetCopy.TipMicOff, WidgetCopy.NameMicOff),
        SettingsRowId.SoundSettings => new(FluentGlyphs.Settings, WidgetCopy.TipSoundSettings, WidgetCopy.NameSoundSettings),
        SettingsRowId.CaseCard => new(FluentGlyphs.Preview, WidgetCopy.TipCaseCard, WidgetCopy.NameCaseCard),
        SettingsRowId.CaseCardClose => new('\0', WidgetCopy.TipCaseCardClose, WidgetCopy.NameCaseCardClose),
        SettingsRowId.CaseCardDisplays => new('\0', WidgetCopy.TipCaseCardDisplays, WidgetCopy.NameCaseCardDisplays),
        SettingsRowId.CaseCardDisplay => new('\0', WidgetCopy.TipCaseCardDisplay, null),
        _ => new('\0', null, null),
    };

    // The accessible name of one control of a row. The row's own name for most; the controls that share a row say
    // which of them they are.
    public static string NameOf(SettingsRowId row, SettingsPart part, int index = 0) => (row, part) switch
    {
        (SettingsRowId.None, SettingsPart.Back) => WidgetCopy.TipBack,
        (SettingsRowId.GaugePosition, SettingsPart.SegmentFirst) => WidgetCopy.PositionRightEnd,
        (SettingsRowId.GaugePosition, SettingsPart.SegmentSecond) => WidgetCopy.PositionNextToApps,
        (SettingsRowId.LowBattery, SettingsPart.Minus) => WidgetCopy.NameLowerLowBattery,
        (SettingsRowId.LowBattery, SettingsPart.Plus) => WidgetCopy.NameRaiseLowBattery,
        (SettingsRowId.Connect, SettingsPart.Clear) => WidgetCopy.NameClearConnectShortcut,
        (SettingsRowId.Disconnect, SettingsPart.Clear) => WidgetCopy.NameClearDisconnectShortcut,
        (SettingsRowId.OpenCard, SettingsPart.Clear) => ShortcutCopy.NameClearCardShortcut,
        (SettingsRowId.CaseCard, SettingsPart.Expand) => WidgetCopy.NameCaseCardMore,
        (SettingsRowId.GaugeOrder, SettingsPart.Tile) => GaugeOrders.AccessibleName(GaugeOrders.FromStored((GaugeOrder)index)),
        _ => For(row).Name ?? string.Empty,
    };

    // The tooltip of one control of a row, or null when it needs none. A control that is only an icon, or only a
    // picture, always has one.
    public static string? TipOf(SettingsRowId row, SettingsPart part, int index = 0) => (row, part) switch
    {
        (SettingsRowId.None, SettingsPart.Back) => WidgetCopy.TipBack,
        (SettingsRowId.Connect, SettingsPart.Clear) or (SettingsRowId.Disconnect, SettingsPart.Clear) or (SettingsRowId.OpenCard, SettingsPart.Clear) => WidgetCopy.TipClear,
        (SettingsRowId.GaugeOrder, SettingsPart.Tile) => GaugeOrders.AccessibleName(GaugeOrders.FromStored((GaugeOrder)index)),
        (SettingsRowId.CaseCard, SettingsPart.Expand) => WidgetCopy.TipCaseCardMore,
        _ => For(row).Tip,
    };
}
