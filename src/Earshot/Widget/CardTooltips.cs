namespace Earshot.Widget;

// The card's tooltips, all of them. A tooltip names a control that is only an icon, in one to three words; a control whose words
// show has none, and nothing here is a sentence. A screen reader keeps the control's own name and description (CardControl.Name
// and Tip), which are not these.
internal static class CardTooltips
{
    public const string Settings = "Settings";
    public const string Back = "Back";
    public const string Close = "Close";
    public const string Clear = "Clear";
    public const string Refresh = "Refresh";
    public const string Refreshing = "Refreshing";
    public const string PreviousDay = "Previous day";
    public const string NextDay = "Next day";

    public static string ForRefresh(bool reading) => reading ? Refreshing : Refresh;

    // The tooltip of one control of the settings page: the icon-only parts, and nothing for a part with words (an expander is a whole row: its label shows).
    public static string? ForSettingsPart(SettingsPart part, int index) => part switch
    {
        SettingsPart.Back => Back,
        SettingsPart.Clear => Clear,
        SettingsPart.Tile => GaugeOrders.AccessibleName(GaugeOrders.FromStored((GaugeOrder)index)),
        _ => null,
    };
}
