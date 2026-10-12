namespace Earshot.Widget;

// The words of the send-to picker, its card row, its menu item and the three settings that belong to it. British English, plain
// and short, no em-dashes. Text that names a control is on the control; a tooltip only goes on an icon with no words.
internal static class SendToCopy
{
    public const string WindowName = "Send AirPods to";

    public const string ThisPc = "This PC";
    public const string OtherSpeakers = "Other speakers";
    public const string NotOnThisPc = "Not on this PC";

    // "Send to iPhone": the owner's own name for the other device. Empty (the owner cleared it) reads "another device".
    public static string SendToDevice(string otherDeviceLabel) =>
        "Send to " + (string.IsNullOrWhiteSpace(otherDeviceLabel) ? "another device" : otherDeviceLabel.Trim());

    // The card's row and the gauge menu's item.
    public const string CardRow = "Send to";
    public const string MenuItem = "Send to...";

    // What a screen reader says for a row that is where the AirPods are now.
    public static string RowName(string label, bool current) => current ? label + ", current" : label;

    // The settings: two switches in More, and the shortcut row beside the others.
    public const string SettingCardRow = "Send to row";
    public const string SettingGaugeMenu = "Send to menu";
    public const string TipCardRow = "Show a Send to row on the card";
    public const string TipGaugeMenu = "Show Send to when you right-click the gauge";
    public const string NameCardRow = "Send to on the card";
    public const string NameGaugeMenu = "Send to in the gauge menu";

    public const string ShortcutLabel = "Send to";
    public const string TipShortcut = "Shortcut to open Send to";
    public const string NameShortcut = "Send to shortcut";
    public const string NameClearShortcut = "Clear send to shortcut";
}
