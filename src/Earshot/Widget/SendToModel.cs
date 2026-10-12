namespace Earshot.Widget;

// What each row of the send-to picker does.
internal enum SendToChoice
{
    // Bring the AirPods here: Connect, the same request the Connect button and the Connect shortcut make.
    ThisPc,

    // Send the AirPods to the owner's other device: the existing hand-back (a disconnect from this PC), the request the
    // Disconnect button and the Disconnect shortcut make.
    OtherDevice,

    // Open Windows' sound settings, to pick other speakers. It changes nothing in Earshot and never hands the AirPods back.
    OtherSpeakers,
}

// One row: what it does, the icon beside it, its words, whether it is where the AirPods are now (a tick, and a press
// does nothing) and whether it can be pressed at all.
internal sealed record SendToRow(SendToChoice Choice, char Glyph, string Label, bool Current, bool Enabled)
{
    // A press does something only on a row that is available and is not where the AirPods already are.
    public bool Acts => Enabled && !Current;
}

// The picker as data: where the AirPods are now in words, and its rows. Pure, so every state is checked without a window.
//
// Where the AirPods are comes from the same state the card and the gauge use (WidgetSnapshot.Where):
//   on this PC             the first row is current; the second sends them to the other device; the third opens sound settings
//   on the other device    the second row is current and carries the owner's name for that device ("iPhone"), and the first
//                          brings them here
//   neither                "Not on this PC": the first row brings them here, and the second has nothing to send, so it cannot
//                          be pressed
// The other device's name is shown as the place only in the second state, where the existing state says the AirPods are on it. In
// the other two it appears only as the name of what a press would do ("Send to iPhone").
// Both of the first two rows are held while a switch is already in flight (busy), as the Connect button is.
internal sealed record SendToModel(string Where, char WhereGlyph, IReadOnlyList<SendToRow> Rows)
{
    public static SendToModel From(AirPodsWhere where, string otherDeviceLabel, bool busy)
    {
        string name = otherDeviceLabel ?? string.Empty;
        bool here = where == AirPodsWhere.ThisPc;
        bool there = where == AirPodsWhere.Elsewhere;

        string whereText = WidgetCopy.Where(where, name);
        if (where is AirPodsWhere.NotInUse or AirPodsWhere.Unknown)
        {
            whereText = SendToCopy.NotOnThisPc;
        }

        var rows = new List<SendToRow>
        {
            new(SendToChoice.ThisPc, FluentGlyphs.OnThisPc, SendToCopy.ThisPc, Current: here, Enabled: !busy),
            new(SendToChoice.OtherDevice, FluentGlyphs.CellPhone, SendToCopy.SendToDevice(name), Current: there, Enabled: !busy && (here || there)),
            new(SendToChoice.OtherSpeakers, FluentGlyphs.Volume, SendToCopy.OtherSpeakers, Current: false, Enabled: true),
        };
        char glyph = here ? FluentGlyphs.OnThisPc : there ? FluentGlyphs.CellPhone : FluentGlyphs.Earbud;
        return new SendToModel(whereText, glyph, rows);
    }
}

// What a press on a row calls. The three things are handed in, so a test sees exactly which of them a press reached.
internal sealed class SendToActions(Action connect, Action handBack, Action openSoundSettings)
{
    // Runs the row's action and returns true, or does nothing and returns false (the row is held, or it is where the AirPods
    // already are). The other speakers row reaches only the sound settings: it can never reach the hand-back or the connect.
    public bool Choose(SendToModel model, SendToChoice choice)
    {
        ArgumentNullException.ThrowIfNull(model);
        SendToRow? row = model.Rows.FirstOrDefault(r => r.Choice == choice);
        if (row is not { Acts: true })
        {
            return false;
        }

        switch (choice)
        {
            case SendToChoice.ThisPc:
                connect();
                return true;
            case SendToChoice.OtherDevice:
                handBack();
                return true;
            case SendToChoice.OtherSpeakers:
                openSoundSettings();
                return true;
            default:
                return false;
        }
    }
}

// Which of the ways in to the picker are on. Each is its own switch: the card's row (WidgetSettings.SendToOnCard, and never on
// the case-open card, which has no room for it), the Send to item in the menu a right-click on the gauge opens
// (WidgetSettings.SendToOnGauge, and never in the tray icon's menu), and the shortcut (SendToShortcut: on while it holds a chord
// and shortcuts are on, so clearing it turns it off). Turning one off leaves the others as they are.
internal static class SendToAccess
{
    public static bool CardRow(WidgetSettings widget, bool noticeCard)
    {
        ArgumentNullException.ThrowIfNull(widget);
        return widget.SendToOnCard && !noticeCard;
    }

    public static bool GaugeMenuItem(WidgetSettings widget, bool openedFromGauge)
    {
        ArgumentNullException.ThrowIfNull(widget);
        return widget.SendToOnGauge && openedFromGauge;
    }

    // Where the AirPods are, for the picker: the PC's own connection is the authority on "here", so a connected link is this PC
    // whatever the broadcast last said, and otherwise the widget's reading stands (only the broadcast can say they are on the
    // other device).
    public static AirPodsWhere Where(AirPodsWhere widgetWhere, bool connectedHere) => connectedHere ? AirPodsWhere.ThisPc : widgetWhere;
}
