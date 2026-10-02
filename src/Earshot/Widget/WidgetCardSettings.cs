using Earshot.App;
using Earshot.Popup;

namespace Earshot.Widget;

// Everything the settings page shows, read from the real settings by the host each time the page is drawn, so a
// row always shows what is saved and never what was last asked for. InEarProofMissing is not a setting: it says
// a feature is switched on or off but cannot act yet, because the in-ear signal is not known.
internal sealed record CardSettingsValues(
    GaugePosition GaugePosition,
    string OtherDeviceLabel,
    bool PauseWhenBudComesOut,
    bool PauseWhenAirPodsLeave,
    int LowBatteryPercent,
    bool LeftClickConnects,
    bool HandBack,
    string ConnectChord,
    string DisconnectChord,
    string? ConnectFailure,      // why the Connect shortcut is not registered, or null
    string? DisconnectFailure,
    string? InstalledVersion,    // "1.2.0", or null when the running version cannot be read
    bool CheckAutomatically,
    bool InEarProofMissing,
    bool InstallExists = false)    // an install exists in any state, so Repair is offered beside Check for updates
{
    // Which display the gauge is on (GaugeDisplayChoice.MainDisplay, "", for the main one), the list the row offers
    // and a line under the row when the choice cannot be honoured now. Init-only members, so the positional list above
    // stays as it is.
    public string GaugeDisplayId { get; init; } = GaugeDisplayChoice.MainDisplay;

    public IReadOnlyList<DisplayOption> GaugeDisplayOptions { get; init; } = new DisplayOption[] { new(GaugeDisplayChoice.MainDisplay, Widget.GaugeDisplayOptions.MainLabel) };

    public string? GaugeDisplayNote { get; init; }

    // How the gauge's ring, number and bolt line up, and what the gauge shows now, so each picture of an order is drawn
    // with the real thing. Init-only for the same reason as the members above.
    public GaugeOrder GaugeOrder { get; init; } = GaugeOrder.RingNumberBolt;

    public GaugeContent? GaugePreview { get; init; }

    // The "microphone off" Hands-Free mode as saved, and where the AirPods' Hands-Free microphone stands in Windows, which
    // decides the line under the row and whether the row offers to open sound settings. Init-only for the same reason.
    public bool HandsFreeMicrophoneOff { get; init; }

    // The shortcut that opens or closes the card, and why it is not registered, or null. Init-only for the same reason.
    public string OpenCardChord { get; init; } = string.Empty;

    public string? OpenCardFailure { get; init; }

    // The chord a shortcut row holds, "" when none.
    public string ChordFor(SettingsRowId row) => row switch
    {
        SettingsRowId.Connect => ConnectChord,
        SettingsRowId.Disconnect => DisconnectChord,
        SettingsRowId.OpenCard => OpenCardChord,
        _ => string.Empty,
    };

    public MicrophoneRowState MicrophoneState { get; init; } = MicrophoneRowState.OpenSettings;

    // Why Check for updates and Repair do nothing now (a setup, repair or update is running), or null. The rows say it in place
    // of their usual line, and the tray refuses the buttons with the same words.
    public string? ElevatedRunNote { get; init; }

    public const int LowBatteryMin = 10;
    public const int LowBatteryMax = 90;
    public const int LowBatteryStep = 10;
}

// Where the AirPods' Hands-Free microphone stands in Windows, as the "microphone off" mode's row says it.
internal enum MicrophoneRowState
{
    // No Hands-Free microphone exists yet: Windows adds it at the next connect, once protection is off.
    ConnectFirst,

    // The microphone is there and Windows has not been told to refuse it: the person can open sound settings.
    OpenSettings,

    // Windows lists the microphone as disabled: nothing is left to do.
    OffInWindows,
}

// What the settings page and the update page need from the tray, so the card and its presenter reach the real
// settings, the shortcut registration and the update flow through one seam a test replaces with a fake. Each
// setter goes through the path the tray menu's own item already uses. Nothing here starts a download: only
// StartUpdate does, and only the person's click on Update reaches it.
internal interface IWidgetCardHost
{
    CardSettingsValues ReadSettings();

    void SetGaugePosition(GaugePosition value, CardPlace place);

    void SetGaugeDisplay(string id, CardPlace place);

    void SetGaugeOrder(GaugeOrder value, CardPlace place);

    void SetOtherDeviceLabel(string value, CardPlace place);

    void SetPauseWhenBudComesOut(bool on, CardPlace place);

    void SetPauseWhenAirPodsLeave(bool on, CardPlace place);

    void SetLowBatteryPercent(int percent, CardPlace place);

    void SetLeftClickConnects(bool on, CardPlace place);

    void SetHandBack(bool on, CardPlace place);

    // The "microphone off" mode: on turns Protect audio quality off through the menu item's own path, off turns it back on.
    void SetHandsFreeMicrophoneOff(bool on, CardPlace place);

    // Opens Windows' sound settings at the AirPods' microphone. Opens a page and changes nothing.
    void OpenSoundSettings(CardPlace place);

    void SetCheckAutomatically(bool on, CardPlace place);

    // The keys pressed for a shortcut. The chord is stored when this build would register it; otherwise nothing is
    // stored and the reason is returned.
    string? SetShortcut(CardShortcut shortcut, Keys key, bool control, bool alt, bool shift, CardPlace place);

    void ClearShortcut(CardShortcut shortcut, CardPlace place);

    // The update page as the update flow's state words it, and with a spinner frame when it is checking.
    SetupViewModel UpdatePage(int spinnerFrame);

    // The newer version a check found and nothing has dealt with yet, as text, or null.
    string? AvailableUpdateVersion();

    // Runs a check and nothing more.
    void CheckForUpdates();

    // The person clicked Update: download, verify and hand over.
    void StartUpdate();

    void CancelUpdate();

    void TryUpdateAgain();

    // The update page's other buttons, for when there is no install to update: set Earshot up, repair it, or switch to
    // the installed copy from one that is not.
    void SetUpEarshot();

    void RepairEarshot();

    void SwitchToInstalled();

    // Raised, on any thread, whenever the update flow moves.
    event EventHandler? UpdateChanged;
}

// The shortcuts the settings page edits.
internal enum CardShortcut { Connect, Disconnect, OpenCard }

internal enum SettingsRowId
{
    None,
    GaugePosition,
    GaugeDisplay,
    GaugeOrder,
    OtherDevice,
    PauseBud,
    PauseLeave,
    LowBattery,
    LeftClick,
    HandBack,
    Connect,
    Disconnect,
    OpenCard,
    CheckForUpdates,
    Repair,
    CheckAutomatically,
    MicrophoneOff,
    SoundSettings,
}

internal enum SettingsPart { Back, Toggle, SegmentFirst, SegmentSecond, Text, Minus, Plus, Shortcut, Clear, Button, Choice, Tile }

// One control of the settings page, for the keyboard order, the mouse and the focus visual. Index is which picture of a
// group of pictures (the gauge orders), 0 for everything else.
internal readonly record struct SettingsTarget(SettingsRowId Row, SettingsPart Part, int Index = 0)
{
    // True when both are the same stop in the keyboard order: the pictures of a group are one stop, and the arrow keys
    // move among them.
    public bool SameStop(SettingsTarget other) => Row == other.Row && Part == other.Part;
}

// A change the person made on the settings page, raised by the card and applied by the presenter.
internal abstract record SettingChange
{
    private protected SettingChange()
    {
    }
}

internal sealed record ToggleChange(SettingsRowId Row, bool On) : SettingChange;

internal sealed record PositionChange(GaugePosition Value) : SettingChange;

internal sealed record DisplayChange(string Id) : SettingChange;

internal sealed record OrderChange(GaugeOrder Value) : SettingChange;

internal sealed record TextChange(string Value) : SettingChange;

internal sealed record ThresholdChange(int Percent) : SettingChange;

internal sealed record ShortcutChange(CardShortcut Shortcut, Keys Key, bool Control, bool Alt, bool Shift) : SettingChange;

internal sealed record ShortcutClear(CardShortcut Shortcut) : SettingChange;

internal sealed record CheckRequest : SettingChange;

internal sealed record RepairRequest : SettingChange;

internal sealed record OpenSoundSettingsRequest : SettingChange;

// How wide a run of text is and how many lines it wraps to, so the layout can size rows without drawing. The
// card's own is measured with GDI+ in the card's font; a test hands in the same over an off-screen bitmap.
internal interface ICardTextMeasure
{
    int Width(string text, int pixelSize);

    int Lines(string text, int width, int pixelSize, int lineHeight);
}

internal sealed class GraphicsTextMeasure(Graphics graphics, CardType type) : ICardTextMeasure
{
    public int Width(string text, int pixelSize)
    {
        using Font font = type.Font(pixelSize, bold: false);
        return (int)Math.Ceiling(graphics.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic).Width);
    }

    public int Lines(string text, int width, int pixelSize, int lineHeight) =>
        CardPaint.Lines(graphics, text, width, type, pixelSize, bold: false, lineHeight);
}

internal enum SettingsItemKind { Row, Divider, Head }

// One item of the page, in client pixels. Rectangles a row does not have are Rectangle.Empty. A is the row's
// main control (the toggle, the first segment, the text box, the minus button, the shortcut box, the Check
// button) and B its second (the second segment, the plus button, the clear button).
internal sealed record SettingsItem(
    SettingsItemKind Kind,
    SettingsRowId Row,
    string Label,
    string? Sub,
    bool SubIsProblem,
    Rectangle Bounds,
    Rectangle LabelRect,
    Rectangle SubRect,
    Rectangle A,
    Rectangle B,
    Rectangle Value)
{
    // The icon beside the label, where it goes, the tooltip and the accessible name. The pictures of the gauge orders,
    // for the one row that has them. Init-only, so a row built without them is a row with no icon.
    public char Glyph { get; init; }

    public Rectangle IconRect { get; init; }

    public string? Tip { get; init; }

    public string? AccessibleName { get; init; }

    public IReadOnlyList<Rectangle> Tiles { get; init; } = [];
}

internal sealed record SettingsLayout(SubPageFrame.FrameLayout Frame, IReadOnlyList<SettingsItem> Items, IReadOnlyList<SettingsTarget> Targets);

// The settings page's rows in the order the design gives them, each sized from what it holds. Pure: no window and
// no drawing.
internal static class SettingsPageLayout
{
    public const int RowMinHeightAt96 = 36;
    public const int RowPaddingAt96 = 4;
    public const int SidePaddingAt96 = 16;
    public const int LabelControlGapAt96 = 12;
    public const int ControlHeightAt96 = 28;
    public const int TextBoxWidthAt96 = 120;
    public const int ShortcutWidthAt96 = 136;
    public const int StepperValueWidthAt96 = 40;
    public const int SegmentPaddingAt96 = 10;
    public const int SegmentGapAt96 = 4;
    public const int ButtonPaddingAt96 = 12;
    public const int DividerGapAt96 = 8;
    public const int HeadHeightAt96 = 32;
    public const int BodyBottomAt96 = 12;
    public const int ControlGapAt96 = 2;
    public const int LabelLineAt96 = 20;
    public const int SubLineAt96 = 16;
    public const int MinLabelWidthAt96 = 80;
    public const int IconSizeAt96 = 16;
    public const int IconGapAt96 = 12;
    public const int OrderTileGapAt96 = 8;
    public const int OrderTileHeightAt96 = 52;
    public const int StackGapAt96 = 4;

    public static SettingsLayout Compute(CardSettingsValues values, int dpi, ICardTextMeasure measure, double textScale = 1.0)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(measure);

        int width = CardPlacement.Scale(SubPageFrame.WidthAt96, dpi);
        int side = CardPlacement.Scale(SidePaddingAt96, dpi);
        int gap = CardPlacement.Scale(LabelControlGapAt96, dpi);
        int minRow = CardPlacement.Scale(RowMinHeightAt96, dpi);
        int rowPad = CardPlacement.Scale(RowPaddingAt96, dpi);
        int control = TextFit.Fit(ControlHeightAt96, TypeRole.Body, 8, dpi, textScale);
        int labelLine = TextFit.Grow(LabelLineAt96, dpi, textScale);
        int subLine = TextFit.Grow(SubLineAt96, dpi, textScale);
        int fourteen = CardPlacement.Scale(14, dpi);
        int twelve = CardPlacement.Scale(12, dpi);
        int toggleW = CardPlacement.Scale(WidgetCardLayout.ToggleWidthAt96, dpi);
        int toggleH = CardPlacement.Scale(WidgetCardLayout.ToggleHeightAt96, dpi);
        int contentWidth = width - (2 * side);

        var items = new List<SettingsItem>();
        var targets = new List<SettingsTarget> { new(SettingsRowId.None, SettingsPart.Back) };
        int y = 0;

        // The icon sits at the side padding and the label after it. A label (and its note) take the width from there to
        // the right padding.
        int iconBox = CardPlacement.Scale(IconSizeAt96, dpi);
        int labelLeft = side + iconBox + CardPlacement.Scale(IconGapAt96, dpi);
        int labelArea = width - side - labelLeft;

        // The icon is centred on the label's first line.
        Rectangle IconAt(Rectangle labelRect) => new(side, labelRect.Y + ((labelLine - iconBox) / 2), iconBox, iconBox);

        SettingsItem Dressed(SettingsItem item) =>
            item with
            {
                Glyph = SettingsRows.For(item.Row).Glyph,
                IconRect = IconAt(item.LabelRect),
                Tip = SettingsRows.For(item.Row).Tip,
                AccessibleName = SettingsRows.For(item.Row).Name,
            };

        // One row: an icon and the label (and a note) on the left, the control block, and its height.
        void Row(
            SettingsRowId id, string label, string? sub, bool subIsProblem, bool subFullWidth, int controlWidth,
            Func<int, int, (Rectangle A, Rectangle B, Rectangle Value)> place, params SettingsPart[] parts)
        {
            int labelWidth = Math.Max(1, labelArea - controlWidth - gap);

            // A control that leaves the label too little room (long text at a large text size) goes under the label,
            // at the right, instead of squeezing it.
            bool stacked = labelWidth < CardPlacement.Scale(MinLabelWidthAt96, dpi);
            if (stacked)
            {
                labelWidth = labelArea;
            }

            int labelLines = measure.Lines(label, labelWidth, fourteen, labelLine);
            int textHeight = labelLine * labelLines;
            int subWidth = subFullWidth || stacked ? labelArea : labelWidth;
            int subLines = sub is null ? 0 : measure.Lines(sub, subWidth, twelve, subLine);
            int subHeight = subLine * subLines;

            if (stacked)
            {
                int stackGap = CardPlacement.Scale(StackGapAt96, dpi);
                var stackedLabel = new Rectangle(labelLeft, y + rowPad, labelWidth, textHeight);
                Rectangle stackedSub = sub is null ? Rectangle.Empty : new Rectangle(labelLeft, stackedLabel.Bottom, labelArea, subHeight);
                int controlTop = (sub is null ? stackedLabel.Bottom : stackedSub.Bottom) + stackGap;
                (Rectangle sa, Rectangle sb, Rectangle sv) = place(controlTop, controlTop + (control / 2));
                int stackedHeight = Math.Max(minRow, controlTop + control + rowPad - y);
                items.Add(Dressed(new SettingsItem(
                    SettingsItemKind.Row, id, label, sub, subIsProblem, new Rectangle(0, y, width, stackedHeight), stackedLabel, stackedSub, sa, sb, sv)));
                foreach (SettingsPart part in parts)
                {
                    targets.Add(new SettingsTarget(id, part));
                }

                y += stackedHeight;
                return;
            }

            int lineHeight = Math.Max(textHeight + (subFullWidth ? 0 : subHeight), control);
            int height = Math.Max(minRow, (2 * rowPad) + lineHeight + (subFullWidth ? subHeight : 0));
            var bounds = new Rectangle(0, y, width, height);
            int labelBlock = textHeight + (subFullWidth ? 0 : subHeight);
            int labelTop = y + rowPad + ((lineHeight - labelBlock) / 2);
            var labelRect = new Rectangle(labelLeft, labelTop, labelWidth, textHeight);
            Rectangle subRect = Rectangle.Empty;
            if (sub is not null)
            {
                subRect = subFullWidth
                    ? new Rectangle(labelLeft, y + rowPad + lineHeight, labelArea, subHeight)
                    : new Rectangle(labelLeft, labelRect.Bottom, labelWidth, subHeight);
            }

            (Rectangle a, Rectangle b, Rectangle value) = place(y + rowPad + ((lineHeight - control) / 2), y + rowPad + (lineHeight / 2));
            items.Add(Dressed(new SettingsItem(SettingsItemKind.Row, id, label, sub, subIsProblem, bounds, labelRect, subRect, a, b, value)));
            foreach (SettingsPart part in parts)
            {
                targets.Add(new SettingsTarget(id, part));
            }

            y += height;
        }

        int right = width - side;

        // The two segments' widths come from their own text, so a longer word never clips.
        int firstW = measure.Width(WidgetCopy.PositionRightEnd, twelve) + (2 * CardPlacement.Scale(SegmentPaddingAt96, dpi));
        int secondW = measure.Width(WidgetCopy.PositionNextToApps, twelve) + (2 * CardPlacement.Scale(SegmentPaddingAt96, dpi));
        int segGap = CardPlacement.Scale(SegmentGapAt96, dpi);
        Row(
            SettingsRowId.GaugePosition, WidgetCopy.SettingsGaugePosition, null, false, false, firstW + segGap + secondW,
            (top, _) => (new Rectangle(right - secondW - segGap - firstW, top, firstW, control), new Rectangle(right - secondW, top, secondW, control), Rectangle.Empty),
            SettingsPart.SegmentFirst, SettingsPart.SegmentSecond);

        int choiceW = 0;
        foreach (DisplayOption option in values.GaugeDisplayOptions)
        {
            choiceW = Math.Max(choiceW, measure.Width(option.Label, twelve));
        }

        choiceW = Math.Max(choiceW, Math.Max(measure.Width(Widget.GaugeDisplayOptions.NotConnectedLabel, twelve), measure.Width(Widget.GaugeDisplayOptions.AllLabel, twelve))) + (2 * CardPlacement.Scale(ButtonPaddingAt96, dpi));
        Row(
            SettingsRowId.GaugeDisplay, WidgetCopy.SettingsGaugeDisplay, values.GaugeDisplayNote, false, false, choiceW,
            (top, _) => (new Rectangle(right - choiceW, top, choiceW, control), Rectangle.Empty, Rectangle.Empty),
            SettingsPart.Choice);

        // Gauge order: the label, then six pictures of the gauge in a grid of three across, each holding the gauge itself in
        // that order. One stop in the keyboard order; the arrow keys move among the pictures.
        {
            var orderLabel = new Rectangle(labelLeft, y + rowPad, labelArea, labelLine);
            int tileGap = CardPlacement.Scale(OrderTileGapAt96, dpi);
            int tileWidth = (contentWidth - (2 * tileGap)) / 3;
            int tileHeight = CardPlacement.Scale(OrderTileHeightAt96, dpi);
            int tilesTop = orderLabel.Bottom + CardPlacement.Scale(OrderTileGapAt96, dpi);
            var tiles = new List<Rectangle>(6);
            for (int i = 0; i < 6; i++)
            {
                tiles.Add(new Rectangle(side + ((i % 3) * (tileWidth + tileGap)), tilesTop + ((i / 3) * (tileHeight + tileGap)), tileWidth, tileHeight));
            }

            int orderHeight = tiles[^1].Bottom + rowPad - y;
            items.Add(Dressed(new SettingsItem(
                SettingsItemKind.Row, SettingsRowId.GaugeOrder, WidgetCopy.SettingsOrder, null, false, new Rectangle(0, y, width, orderHeight), orderLabel,
                Rectangle.Empty, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty)) with { Tiles = tiles });
            targets.Add(new SettingsTarget(SettingsRowId.GaugeOrder, SettingsPart.Tile, (int)GaugeOrders.FromStored(values.GaugeOrder)));
            y += orderHeight;
        }

        int textW = CardPlacement.Scale(TextBoxWidthAt96, dpi);
        Row(
            SettingsRowId.OtherDevice, WidgetCopy.SettingsOtherDevice, null, false, false, textW,
            (top, _) => (new Rectangle(right - textW, top, textW, control), Rectangle.Empty, Rectangle.Empty),
            SettingsPart.Text);

        void ToggleRow(SettingsRowId id, string label, string? sub) =>
            Row(
                id, label, sub, false, false, toggleW,
                (_, mid) => (new Rectangle(right - toggleW, mid - (toggleH / 2), toggleW, toggleH), Rectangle.Empty, Rectangle.Empty),
                SettingsPart.Toggle);

        ToggleRow(SettingsRowId.PauseBud, WidgetCopy.SettingsPauseBud, values.InEarProofMissing ? WidgetCopy.SettingsWaitsOnInEar : null);
        ToggleRow(SettingsRowId.PauseLeave, WidgetCopy.SettingsPauseLeave, null);

        int valueW = CardPlacement.Scale(StepperValueWidthAt96, dpi);
        int stepGap = CardPlacement.Scale(ControlGapAt96, dpi);
        int stepperW = control + stepGap + valueW + stepGap + control;
        Row(
            SettingsRowId.LowBattery, WidgetCopy.SettingsLowBattery, null, false, false, stepperW,
            (top, _) => (
                new Rectangle(right - stepperW, top, control, control),
                new Rectangle(right - control, top, control, control),
                new Rectangle(right - stepperW + control + stepGap, top, valueW, control)),
            SettingsPart.Minus, SettingsPart.Plus);

        ToggleRow(SettingsRowId.LeftClick, WidgetCopy.SettingsLeftClick, null);
        ToggleRow(SettingsRowId.HandBack, WidgetCopy.SettingsHandBack, null);

        // The Hands-Free "microphone off" mode. While it is on the row says what to do next in one line under it, and a
        // second row opens sound settings, unless Windows already lists the microphone as disabled.
        string? micNote = !values.HandsFreeMicrophoneOff ? null : values.MicrophoneState switch
        {
            MicrophoneRowState.ConnectFirst => WidgetCopy.MicConnectFirst,
            MicrophoneRowState.OffInWindows => WidgetCopy.MicOffInWindows,
            _ => WidgetCopy.MicGuidance,
        };
        Row(
            SettingsRowId.MicrophoneOff, WidgetCopy.SettingsMicOff, micNote, subIsProblem: false, subFullWidth: true, toggleW,
            (_, mid) => (new Rectangle(right - toggleW, mid - (toggleH / 2), toggleW, toggleH), Rectangle.Empty, Rectangle.Empty),
            SettingsPart.Toggle);
        if (values.HandsFreeMicrophoneOff && values.MicrophoneState != MicrophoneRowState.OffInWindows)
        {
            int openW = measure.Width(WidgetCopy.OpenButton, twelve) + (2 * CardPlacement.Scale(ButtonPaddingAt96, dpi));
            Row(
                SettingsRowId.SoundSettings, WidgetCopy.SettingsSoundSettings, null, subIsProblem: false, subFullWidth: false, openW,
                (top, _) => (new Rectangle(right - openW, top, openW, control), Rectangle.Empty, Rectangle.Empty),
                SettingsPart.Button);
        }

        void Divider()
        {
            int dividerGap = CardPlacement.Scale(DividerGapAt96, dpi);
            items.Add(new SettingsItem(
                SettingsItemKind.Divider, SettingsRowId.None, string.Empty, null, false,
                new Rectangle(side, y + dividerGap, contentWidth, 1), Rectangle.Empty, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty));
            y += dividerGap + 1;
        }

        void Head(string text)
        {
            int headHeight = TextFit.Grow(HeadHeightAt96, dpi, textScale);
            var bounds = new Rectangle(side, y, contentWidth, headHeight);
            items.Add(new SettingsItem(SettingsItemKind.Head, SettingsRowId.None, text, null, false, bounds, bounds, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty));
            y += headHeight;
        }

        Divider();
        Head(WidgetCopy.SettingsShortcuts);

        int shortcutW = CardPlacement.Scale(ShortcutWidthAt96, dpi);
        int shortcutBlock = shortcutW + stepGap + control;
        void ShortcutRow(SettingsRowId id, string label, string chord, string? failure)
        {
            Row(
                id, label, failure, subIsProblem: true, subFullWidth: true, shortcutBlock,
                (top, _) => (new Rectangle(right - shortcutBlock, top, shortcutW, control), new Rectangle(right - control, top, control, control), Rectangle.Empty),
                string.IsNullOrEmpty(chord) ? new[] { SettingsPart.Shortcut } : new[] { SettingsPart.Shortcut, SettingsPart.Clear });
        }

        ShortcutRow(SettingsRowId.Connect, WidgetCopy.Connect, values.ConnectChord, values.ConnectFailure);
        ShortcutRow(SettingsRowId.Disconnect, WidgetCopy.Disconnect, values.DisconnectChord, values.DisconnectFailure);
        ShortcutRow(SettingsRowId.OpenCard, ShortcutCopy.Card, values.OpenCardChord, values.OpenCardFailure);

        Divider();
        Head(WidgetCopy.SettingsUpdates);

        // The row says which version is installed, and keeps its note only when an update or repair is under way.
        string checkLabel = values.InstalledVersion is null ? WidgetCopy.CheckForUpdates : "Version " + values.InstalledVersion;
        int checkW = measure.Width(WidgetCopy.CheckButton, twelve) + (2 * CardPlacement.Scale(ButtonPaddingAt96, dpi));
        Row(
            SettingsRowId.CheckForUpdates, checkLabel, values.ElevatedRunNote, subIsProblem: false, subFullWidth: false, checkW,
            (top, _) => (new Rectangle(right - checkW, top, checkW, control), Rectangle.Empty, Rectangle.Empty),
            SettingsPart.Button);

        // Repair is offered whenever an install exists, in any state, in the row under the check.
        if (values.InstallExists)
        {
            int repairW = measure.Width(WidgetCopy.RepairButton, twelve) + (2 * CardPlacement.Scale(ButtonPaddingAt96, dpi));
            Row(
                SettingsRowId.Repair, WidgetCopy.RepairEarshot, values.ElevatedRunNote, subIsProblem: false, subFullWidth: false, repairW,
                (top, _) => (new Rectangle(right - repairW, top, repairW, control), Rectangle.Empty, Rectangle.Empty),
                SettingsPart.Button);
        }

        ToggleRow(SettingsRowId.CheckAutomatically, WidgetCopy.SettingsAutoCheck, null);

        int bodyHeight = y + CardPlacement.Scale(BodyBottomAt96, dpi);
        SubPageFrame.FrameLayout frame = SubPageFrame.Compute(dpi, bodyHeight, buttonCount: 0, textScale);
        int offset = frame.Body.Y;
        var shifted = new List<SettingsItem>(items.Count);
        foreach (SettingsItem item in items)
        {
            shifted.Add(item with
            {
                Bounds = Shift(item.Bounds, offset),
                LabelRect = Shift(item.LabelRect, offset),
                SubRect = Shift(item.SubRect, offset),
                A = Shift(item.A, offset),
                B = Shift(item.B, offset),
                Value = Shift(item.Value, offset),
                IconRect = Shift(item.IconRect, offset),
                Tiles = item.Tiles.Select(t => Shift(t, offset)).ToList(),
            });
        }

        return new SettingsLayout(frame, shifted, targets);
    }

    private static Rectangle Shift(Rectangle rect, int dy) => rect.IsEmpty ? rect : new Rectangle(rect.X, rect.Y + dy, rect.Width, rect.Height);
}
