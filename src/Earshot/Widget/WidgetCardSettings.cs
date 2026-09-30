using Earshot.App;
using Earshot.Popup;

namespace Earshot.Widget;

// Everything the settings page shows, read from the real settings by the host each time the page is drawn, so a
// row always shows what is saved and never what was last asked for. The two Missing flags are not settings: they
// say a feature is switched on or off but cannot act yet, because the field battery set-up would have to prove
// has not been proved.
internal sealed record CardSettingsValues(
    GaugePosition GaugePosition,
    string OtherDeviceLabel,
    bool PauseWhenBudComesOut,
    bool PauseWhenAirPodsLeave,
    bool CaseOpenCard,
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
    bool LidProofMissing)
{
    public const int LowBatteryMin = 10;
    public const int LowBatteryMax = 90;
    public const int LowBatteryStep = 10;
}

// What the settings page and the update page need from the tray, so the card and its presenter reach the real
// settings, the shortcut registration and the update flow through one seam a test replaces with a fake. Each
// setter goes through the path the tray menu's own item already uses. Nothing here starts a download: only
// StartUpdate does, and only the person's click on Update reaches it.
internal interface IWidgetCardHost
{
    CardSettingsValues ReadSettings();

    void SetGaugePosition(GaugePosition value, CardPlace place);

    void SetOtherDeviceLabel(string value, CardPlace place);

    void SetPauseWhenBudComesOut(bool on, CardPlace place);

    void SetPauseWhenAirPodsLeave(bool on, CardPlace place);

    void SetCaseOpenCard(bool on, CardPlace place);

    void SetLowBatteryPercent(int percent, CardPlace place);

    void SetLeftClickConnects(bool on, CardPlace place);

    void SetHandBack(bool on, CardPlace place);

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

// The two shortcuts the settings page edits.
internal enum CardShortcut { Connect, Disconnect }

internal enum SettingsRowId
{
    None,
    GaugePosition,
    OtherDevice,
    PauseBud,
    PauseLeave,
    CaseCard,
    LowBattery,
    LeftClick,
    HandBack,
    Connect,
    Disconnect,
    CheckForUpdates,
    CheckAutomatically,
}

internal enum SettingsPart { Back, Toggle, SegmentFirst, SegmentSecond, Text, Minus, Plus, Shortcut, Clear, Button }

// One control of the settings page, for the keyboard order, the mouse and the focus rectangle.
internal readonly record struct SettingsTarget(SettingsRowId Row, SettingsPart Part);

// A change the person made on the settings page, raised by the card and applied by the presenter.
internal abstract record SettingChange
{
    private protected SettingChange()
    {
    }
}

internal sealed record ToggleChange(SettingsRowId Row, bool On) : SettingChange;

internal sealed record PositionChange(GaugePosition Value) : SettingChange;

internal sealed record TextChange(string Value) : SettingChange;

internal sealed record ThresholdChange(int Percent) : SettingChange;

internal sealed record ShortcutChange(CardShortcut Shortcut, Keys Key, bool Control, bool Alt, bool Shift) : SettingChange;

internal sealed record ShortcutClear(CardShortcut Shortcut) : SettingChange;

internal sealed record CheckRequest : SettingChange;

// How wide a run of text is and how many lines it wraps to, so the layout can size rows without drawing. The
// card's own is measured with GDI+ in the card's font; a test hands in the same over an off-screen bitmap.
internal interface ICardTextMeasure
{
    int Width(string text, int pixelSize);

    int Lines(string text, int width, int pixelSize, int lineHeight);
}

internal sealed class GraphicsTextMeasure(Graphics graphics, string fontFamily) : ICardTextMeasure
{
    public int Width(string text, int pixelSize)
    {
        using var font = new Font(fontFamily, Math.Max(1, pixelSize), FontStyle.Regular, GraphicsUnit.Pixel);
        return (int)Math.Ceiling(graphics.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic).Width);
    }

    public int Lines(string text, int width, int pixelSize, int lineHeight) =>
        CardPaint.Lines(graphics, text, width, fontFamily, pixelSize, bold: false, lineHeight);
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
    Rectangle Value);

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

    public static SettingsLayout Compute(CardSettingsValues values, int dpi, ICardTextMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(measure);

        int width = CardPlacement.Scale(SubPageFrame.WidthAt96, dpi);
        int side = CardPlacement.Scale(SidePaddingAt96, dpi);
        int gap = CardPlacement.Scale(LabelControlGapAt96, dpi);
        int minRow = CardPlacement.Scale(RowMinHeightAt96, dpi);
        int rowPad = CardPlacement.Scale(RowPaddingAt96, dpi);
        int control = CardPlacement.Scale(ControlHeightAt96, dpi);
        int labelLine = CardPlacement.Scale(LabelLineAt96, dpi);
        int subLine = CardPlacement.Scale(SubLineAt96, dpi);
        int fourteen = CardPlacement.Scale(14, dpi);
        int twelve = CardPlacement.Scale(12, dpi);
        int toggleW = CardPlacement.Scale(WidgetCardLayout.ToggleWidthAt96, dpi);
        int toggleH = CardPlacement.Scale(WidgetCardLayout.ToggleHeightAt96, dpi);
        int contentWidth = width - (2 * side);

        var items = new List<SettingsItem>();
        var targets = new List<SettingsTarget> { new(SettingsRowId.None, SettingsPart.Back) };
        int y = 0;

        // One row: the label (and a sub-line) on the left, the control block, and its height.
        void Row(
            SettingsRowId id, string label, string? sub, bool subIsProblem, bool subFullWidth, int controlWidth,
            Func<int, int, (Rectangle A, Rectangle B, Rectangle Value)> place, params SettingsPart[] parts)
        {
            int labelWidth = Math.Max(1, contentWidth - controlWidth - gap);
            int labelLines = measure.Lines(label, labelWidth, fourteen, labelLine);
            int textHeight = labelLine * labelLines;
            int subWidth = subFullWidth ? contentWidth : labelWidth;
            int subLines = sub is null ? 0 : measure.Lines(sub, subWidth, twelve, subLine);
            int subHeight = subLine * subLines;

            int lineHeight = Math.Max(textHeight + (subFullWidth ? 0 : subHeight), control);
            int height = Math.Max(minRow, (2 * rowPad) + lineHeight + (subFullWidth ? subHeight : 0));
            var bounds = new Rectangle(0, y, width, height);
            int labelBlock = textHeight + (subFullWidth ? 0 : subHeight);
            int labelTop = y + rowPad + ((lineHeight - labelBlock) / 2);
            var labelRect = new Rectangle(side, labelTop, labelWidth, textHeight);
            Rectangle subRect = Rectangle.Empty;
            if (sub is not null)
            {
                subRect = subFullWidth
                    ? new Rectangle(side, y + rowPad + lineHeight, contentWidth, subHeight)
                    : new Rectangle(side, labelRect.Bottom, labelWidth, subHeight);
            }

            (Rectangle a, Rectangle b, Rectangle value) = place(y + rowPad + ((lineHeight - control) / 2), y + rowPad + (lineHeight / 2));
            items.Add(new SettingsItem(SettingsItemKind.Row, id, label, sub, subIsProblem, bounds, labelRect, subRect, a, b, value));
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
        ToggleRow(SettingsRowId.CaseCard, WidgetCopy.SettingsCaseCard, values.LidProofMissing ? WidgetCopy.SettingsWaitsOnLid : null);

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
            int headHeight = CardPlacement.Scale(HeadHeightAt96, dpi);
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

        Divider();
        Head(WidgetCopy.SettingsUpdates);

        string checkLabel = WidgetCopy.CheckForUpdates;
        int checkW = measure.Width(WidgetCopy.CheckButton, twelve) + (2 * CardPlacement.Scale(ButtonPaddingAt96, dpi));
        string? installed = values.InstalledVersion is null ? null : "Version " + values.InstalledVersion;
        Row(
            SettingsRowId.CheckForUpdates, checkLabel, installed, subIsProblem: false, subFullWidth: false, checkW,
            (top, _) => (new Rectangle(right - checkW, top, checkW, control), Rectangle.Empty, Rectangle.Empty),
            SettingsPart.Button);
        ToggleRow(SettingsRowId.CheckAutomatically, WidgetCopy.CheckAutomatically, null);

        int bodyHeight = y + CardPlacement.Scale(BodyBottomAt96, dpi);
        SubPageFrame.FrameLayout frame = SubPageFrame.Compute(dpi, bodyHeight, buttonCount: 0);
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
            });
        }

        return new SettingsLayout(frame, shifted, targets);
    }

    private static Rectangle Shift(Rectangle rect, int dy) => rect.IsEmpty ? rect : new Rectangle(rect.X, rect.Y + dy, rect.Width, rect.Height);
}
