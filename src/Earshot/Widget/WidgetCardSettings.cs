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

    // The case-open card as saved: on or off, its close choice (CaseOpenCardClose) and its stored displays
    // (CaseOpenCardDisplayChoice). Init-only for the same reason as the members above.
    public bool CaseOpenCardOn { get; init; } = true;

    public int CaseOpenCardCloseSeconds { get; init; } = CaseOpenCardClose.UntilCaseCloses;

    public IReadOnlyList<string> CaseOpenCardDisplays { get; init; } = [];

    // The connected displays, each with its box on the case-open card's row, and the Ids of the displays the card goes on
    // now (the boxes that are ticked). The boxes are offered only with more than one display.
    public IReadOnlyList<DisplayOption> CaseOpenCardDisplayOptions { get; init; } = [];

    public IReadOnlyList<string> CaseOpenCardShownOn { get; init; } = [];

    // Whether the case-open card's row is expanded to show its choices. Not a setting: the card holds it while the page is
    // open and hands it in here for the layout.
    public bool CaseOpenCardExpanded { get; init; }

    // Whether the "More" row and the gauge order row are expanded. Not settings: the card holds them while the page is open and
    // hands them in here for the layout.
    public bool MoreExpanded { get; init; }

    public bool GaugeOrderExpanded { get; init; }

    // Whether a notice that a full charge was reached is wanted (WidgetSettings.FullyChargedNotice, on by default).
    public bool FullyChargedNotice { get; init; } = true;

    public bool CaseOpenCardShownOnDisplay(int index) =>
        index >= 0 && index < CaseOpenCardDisplayOptions.Count &&
        CaseOpenCardShownOn.Contains(CaseOpenCardDisplayOptions[index].Id, StringComparer.OrdinalIgnoreCase);

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

    // Puts the same redacted text on the clipboard as the tray menu's Copy diagnostics item.
    void CopyDiagnostics(CardPlace place);

    // Sets the fully charged notice (WidgetSettings.FullyChargedNotice).
    void SetFullyChargedNotice(bool on, CardPlace place);

    // Opens Windows' Bluetooth settings (the card's Bluetooth off row) and the release notes page (the updates page's What's new).
    void OpenBluetoothSettings(CardPlace place);

    void OpenWhatsNew(CardPlace place);

    void SetCheckAutomatically(bool on, CardPlace place);

    // The case-open card: on or off, when it closes by itself (CaseOpenCardClose), which displays show it (the stored value,
    // CaseOpenCardDisplayChoice), and one display's box ticked or cleared.
    void SetCaseOpenCard(bool on, CardPlace place);

    void SetCaseOpenCardClose(int seconds, CardPlace place);

    void SetCaseOpenCardDisplays(IReadOnlyList<string> stored, CardPlace place);

    void SetCaseOpenCardDisplay(string id, bool on, CardPlace place);

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
    Repair,
    CheckAutomatically,
    MicrophoneOff,
    SoundSettings,
    CaseCard,          // the case-open card's switch and the chevron that expands its row
    CaseCardClose,     // in the expander: when it closes
    CaseCardDisplays,  // in the expander: where the gauge is, or all displays
    CaseCardDisplay,   // in the expander, with more than one display: one box per display, Index the display's place
    More,              // the one expander that holds the rarely changed rows
    FullyCharged,      // in More: the fully charged notice's switch
    History,           // in More: the row that opens the battery history page
    CopyDiagnostics,   // in More: the Copy button
    About,             // the row that opens the updates page
}

// Expand is the chevron of a row with an expander; Check a box, one per item of a list (Index says which).
internal enum SettingsPart { Back, Toggle, Text, Minus, Plus, Shortcut, Clear, Button, Choice, Tile, Expand, Check }

// One control of the settings page, for the keyboard order, the mouse and the focus visual. Index is which picture of a
// group of pictures (the gauge orders), 0 for everything else.
internal readonly record struct SettingsTarget(SettingsRowId Row, SettingsPart Part, int Index = 0)
{
    // True when both are the same stop in the keyboard order: the pictures of a group are one stop, and the arrow keys
    // move among them. Each box of a list is a stop of its own.
    public bool SameStop(SettingsTarget other) => Row == other.Row && Part == other.Part && (Part != SettingsPart.Check || Index == other.Index);
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

internal sealed record CaseCardCloseChange(int Seconds) : SettingChange;

internal sealed record CaseCardDisplaysChange(IReadOnlyList<string> Stored) : SettingChange;

internal sealed record CaseCardDisplayChange(string Id, bool On) : SettingChange;

internal sealed record OrderChange(GaugeOrder Value) : SettingChange;

internal sealed record TextChange(string Value) : SettingChange;

internal sealed record ThresholdChange(int Percent) : SettingChange;

internal sealed record ShortcutChange(CardShortcut Shortcut, Keys Key, bool Control, bool Alt, bool Shift) : SettingChange;

internal sealed record ShortcutClear(CardShortcut Shortcut) : SettingChange;

internal sealed record OpenSoundSettingsRequest : SettingChange;

internal sealed record OpenHistoryRequest : SettingChange;

internal sealed record OpenUpdatesRequest : SettingChange;

internal sealed record CopyDiagnosticsRequest : SettingChange;

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

    // Which item of a list this row is (the case-open card's display boxes), 0 for every other row.
    public int Index { get; init; }

    // A row inside another row's expander: no icon of its own.
    public bool Nested { get; init; }

    // A 1 px row stroke is drawn across the top of this row, which is under the expander row of its surface.
    public bool DividerAbove { get; init; }

    // The chevron at the row's right end and the glyph in it, for a row that opens something or expands; and, for the gauge order
    // row, where the gauge as it is now is drawn.
    public Rectangle Chevron { get; init; }

    public char ChevronGlyph { get; init; }

    public Rectangle Preview { get; init; }
}

// Surfaces: the filled, stroked rounded rectangles the page's rows sit on (one per row, or one for an expander and what is under
// it), in the page's own pixels.
internal sealed record SettingsLayout(
    SubPageFrame.FrameLayout Frame, IReadOnlyList<SettingsItem> Items, IReadOnlyList<SettingsTarget> Targets, IReadOnlyList<Rectangle> Surfaces);

// The settings page's rows in the order the design gives them, each sized from what it holds. Pure: no window and
// no drawing. Every figure is the design's, at 100% display scale and 100% text size; what holds text grows with the
// text size t (a single-line row is 20t + 12 for its control plus 8 above and below, 48 at 100%).
//
// Groups: Taskbar (Display, Gauge order), Behaviour (Case-open card, Hand back), Audio (Microphone off), Shortcuts
// (Connect, Disconnect, Card), About (Updates), then one More expander row holding Gauge position, Other device name,
// Pause when a bud comes out, Pause when AirPods leave, Low battery alerts, Fully charged notice, Left click connects,
// Battery history and Copy diagnostics.
internal static class SettingsPageLayout
{
    public const int BodyTopAt96 = 8;
    public const int BodySideAt96 = 12;
    public const int BodyBottomAt96 = 12;
    public const int RowGapAt96 = 4;
    public const int GroupTopAt96 = 12;
    public const int GroupFirstTopAt96 = 4;
    public const int GroupBottomAt96 = 4;
    public const int RowPadVerticalAt96 = 8;
    public const int RowPadLeftAt96 = 14;
    public const int RowPadRightAt96 = 12;
    public const int TwoLineExtraAt96 = 20;
    public const int GuidanceBottomAt96 = 10;
    public const int IconSizeAt96 = 16;
    public const int IconGapAt96 = 12;
    public const int ChevronAt96 = 12;
    public const int ComboMinWidthAt96 = 128;
    public const int TextBoxWidthAt96 = 128;
    public const int ShortcutWidthAt96 = 136;
    public const int StepperValueWidthAt96 = 40;
    public const int ToggleChevronGapAt96 = 4;
    public const int ButtonPaddingAt96 = 12;
    public const int ControlGapAt96 = 2;
    public const int LabelLineAt96 = 20;
    public const int SubLineAt96 = 16;
    public const int MinLabelWidthAt96 = 80;
    public const int OrderTileGapAt96 = 8;
    public const int OrderTileHeightAt96 = 52;
    public const int OrderPaddingAt96 = 12;
    public const int StackGapAt96 = 4;
    public const int CheckBoxAt96 = 20;
    public const int OrderHeaderAt96 = 56;

    // The 1 px row stroke that separates an expander's content.
    public const int SeparatorAt96 = 1;

    public static SettingsLayout Compute(CardSettingsValues values, int dpi, ICardTextMeasure measure, double textScale = 1.0)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(measure);

        int width = CardPlacement.Scale(SubPageFrame.WidthAt96, dpi);
        int bodySide = CardPlacement.Scale(BodySideAt96, dpi);
        int surfaceLeft = bodySide;
        int surfaceWidth = width - (2 * bodySide);
        int surfaceRight = surfaceLeft + surfaceWidth;
        int rowGap = CardPlacement.Scale(RowGapAt96, dpi);
        int rowPad = CardPlacement.Scale(RowPadVerticalAt96, dpi);
        int padLeft = CardPlacement.Scale(RowPadLeftAt96, dpi);
        int padRight = CardPlacement.Scale(RowPadRightAt96, dpi);
        int gap = CardPlacement.Scale(LabelControlGapAtDpi, dpi);
        int control = WidgetCardLayout.RowHeight(dpi, textScale);
        int labelLine = TextFit.Grow(LabelLineAt96, dpi, textScale);
        int subLine = TextFit.Grow(SubLineAt96, dpi, textScale);
        int fourteen = CardPlacement.Scale(14, dpi);
        int twelve = CardPlacement.Scale(12, dpi);
        int toggleW = CardPlacement.Scale(WidgetCardLayout.ToggleWidthAt96, dpi);
        int toggleH = CardPlacement.Scale(WidgetCardLayout.ToggleHeightAt96, dpi);
        int chevronBox = CardPlacement.Scale(ChevronAt96, dpi) + CardPlacement.Scale(8, dpi);
        int buttonPad = CardPlacement.Scale(ButtonPaddingAt96, dpi);

        var items = new List<SettingsItem>();
        var surfaces = new List<Rectangle>();
        var targets = new List<SettingsTarget> { new(SettingsRowId.None, SettingsPart.Back) };
        int y = CardPlacement.Scale(BodyTopAt96, dpi);

        // The icon sits 14 into the surface, the label after it and a 12 gap. A label (and its note) take the width from there to
        // the surface's right padding.
        int iconBox = CardPlacement.Scale(IconSizeAt96, dpi);
        int iconLeft = surfaceLeft + padLeft;
        int labelLeft = iconLeft + iconBox + CardPlacement.Scale(IconGapAt96, dpi);
        int right = surfaceRight - padRight;
        int labelArea = right - labelLeft;

        // A surface that holds a row, or an expander and what is under it. surfaceTop is where the open one began.
        bool holding = false;
        int surfaceTop = 0;
        bool nested = false;

        void BeginGroup()
        {
            holding = true;
            surfaceTop = y;
        }

        void EndGroup()
        {
            surfaces.Add(new Rectangle(surfaceLeft, surfaceTop, surfaceWidth, y - surfaceTop));
            y += rowGap;
            holding = false;
        }

        // The icon is centred on the label's first line.
        Rectangle IconAt(Rectangle labelRect) => new(iconLeft, labelRect.Y + ((labelLine - iconBox) / 2), iconBox, iconBox);

        SettingsItem Dressed(SettingsItem item, bool separated)
        {
            SettingsRowInfo info = SettingsRows.For(item.Row);
            return item with
            {
                Glyph = nested ? '\0' : info.Glyph,
                IconRect = IconAt(item.LabelRect),
                Tip = info.Tip,
                AccessibleName = info.Name,
                DividerAbove = separated,
                Nested = nested,
            };
        }

        // One row: an icon and the label (and a note) on the left, the control block, and its height.
        void Row(
            SettingsRowId id, string label, string? sub, bool subIsProblem, bool subFullWidth, int controlWidth,
            Func<int, int, (Rectangle A, Rectangle B, Rectangle Value)> place, params SettingsPart[] parts)
        {
            bool separated = holding && y != surfaceTop;
            if (!holding)
            {
                surfaceTop = y;
            }

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
            int top = y;
            int height;
            Rectangle labelRect;
            Rectangle subRect = Rectangle.Empty;
            Rectangle a;
            Rectangle b;
            Rectangle value;

            if (stacked)
            {
                int stackGap = CardPlacement.Scale(StackGapAt96, dpi);
                labelRect = new Rectangle(labelLeft, top + rowPad, labelWidth, textHeight);
                subRect = sub is null ? Rectangle.Empty : new Rectangle(labelLeft, labelRect.Bottom, labelArea, subHeight);
                int controlTop = (sub is null ? labelRect.Bottom : subRect.Bottom) + stackGap;
                (a, b, value) = place(controlTop, controlTop + (control / 2));
                height = controlTop + control + rowPad - top;
            }
            else
            {
                int lineHeight = Math.Max(textHeight + (subFullWidth ? 0 : subHeight), control);
                int labelBlock = textHeight + (subFullWidth ? 0 : subHeight);
                if (subFullWidth)
                {
                    // The design: the control row is 48 (8 above, the 32 control, 8 below), then the line, then 10 below the line.
                    int bottomPad = sub is null ? 0 : CardPlacement.Scale(GuidanceBottomAt96, dpi);
                    height = (2 * rowPad) + lineHeight + subHeight + bottomPad;
                }
                else
                {
                    height = (2 * rowPad) + lineHeight;
                    if (sub is not null)
                    {
                        height = Math.Max(height, labelBlock + CardPlacement.Scale(TwoLineExtraAt96, dpi));
                    }
                }

                // A row with a full-width line under it has its control row first (8 above it, the 32 control, 8 below, so 48 at 100%), and
                // the line starts where that row ends. Centring the control in all of the row but the line put it a pixel low, into the line.
                int labelTop = subFullWidth
                    ? top + rowPad + ((lineHeight - labelBlock) / 2)
                    : top + ((height - labelBlock) / 2);
                labelRect = new Rectangle(labelLeft, labelTop, labelWidth, textHeight);
                if (sub is not null)
                {
                    subRect = subFullWidth
                        ? new Rectangle(labelLeft, top + (2 * rowPad) + lineHeight, labelArea, subHeight)
                        : new Rectangle(labelLeft, labelRect.Bottom, labelWidth, subHeight);
                }

                int controlTop = subFullWidth
                    ? top + rowPad + ((lineHeight - control) / 2)
                    : top + ((height - control) / 2);
                (a, b, value) = place(controlTop, controlTop + (control / 2));
            }

            height = Math.Max(height, 1);
            var bounds = new Rectangle(surfaceLeft, top, surfaceWidth, height);
            items.Add(Dressed(new SettingsItem(SettingsItemKind.Row, id, label, sub, subIsProblem, bounds, labelRect, subRect, a, b, value), separated));
            foreach (SettingsPart part in parts)
            {
                targets.Add(new SettingsTarget(id, part));
            }

            y += height;
            if (!holding)
            {
                surfaces.Add(bounds);
                y += rowGap;
            }
        }

        // A row that is one target, pressed anywhere on it, with a chevron at its right end (History, Updates).
        void NavigationRow(SettingsRowId id, string label, string? sub)
        {
            Row(
                id, label, sub, false, false, chevronBox,
                (top, _) => (Rectangle.Empty, Rectangle.Empty, new Rectangle(right - chevronBox, top + ((control - chevronBox) / 2), chevronBox, chevronBox)),
                SettingsPart.Button);
            SettingsItem row = items[^1];
            items[^1] = row with { A = row.Bounds, Chevron = row.Value, ChevronGlyph = FluentGlyphs.ChevronRight };
        }

        void ToggleRow(SettingsRowId id, string label, string? sub) =>
            Row(
                id, label, sub, false, false, toggleW,
                (_, mid) => (new Rectangle(right - toggleW, mid - (toggleH / 2), toggleW, toggleH), Rectangle.Empty, Rectangle.Empty),
                SettingsPart.Toggle);

        // A button whose width is its words and padding (and a glyph when it has one).
        int ButtonWidth(string text, bool glyph) =>
            measure.Width(text, fourteen) + (2 * buttonPad) + (glyph ? CardPlacement.Scale(ChevronAt96, dpi) + CardPlacement.Scale(8, dpi) : 0);

        bool firstGroup = true;

        void Head(string text)
        {
            y += CardPlacement.Scale(firstGroup ? GroupFirstTopAt96 : GroupTopAt96, dpi);
            firstGroup = false;
            int headHeight = labelLine;
            var bounds = new Rectangle(surfaceLeft, y, surfaceWidth, headHeight);
            items.Add(new SettingsItem(SettingsItemKind.Head, SettingsRowId.None, text, null, false, bounds, bounds, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty));
            y += headHeight + CardPlacement.Scale(GroupBottomAt96, dpi);
        }

        // ---- Taskbar

        Head(WidgetCopy.SettingsGroupTaskbar);

        int choiceW = ComboMin(dpi);
        foreach (DisplayOption option in values.GaugeDisplayOptions)
        {
            choiceW = Math.Max(choiceW, ButtonWidth(option.Label, glyph: false));
        }

        choiceW = Math.Max(choiceW, Math.Max(ButtonWidth(Widget.GaugeDisplayOptions.NotConnectedLabel, false), ButtonWidth(Widget.GaugeDisplayOptions.AllLabel, false)));
        Row(
            SettingsRowId.GaugeDisplay, WidgetCopy.SettingsGaugeDisplay, values.GaugeDisplayNote, false, false, choiceW,
            (top, _) => (new Rectangle(right - choiceW, top, choiceW, control), Rectangle.Empty, Rectangle.Empty),
            SettingsPart.Choice);

        // Gauge order: an expander row showing the gauge as it is now; open, a grid of three across and two down holding the gauge in each
        // order. The pictures are one stop in the keyboard order; the arrow keys move among them.
        {
            Size gaugeSize = GaugeLayout.SizeFor(dpi);
            BeginGroup();
            int top = y;

            // The label has the room left of the gauge picture, which is left of the chevron; a label that is wider than that (large
            // text) wraps in it and makes the header taller, like a row's label beside its control, and never runs under the picture.
            int chevronX = right - chevronBox;
            int previewX = chevronX - gap - gaugeSize.Width;
            int orderLabelWidth = Math.Max(1, previewX - gap - labelLeft);
            int orderLabelHeight = labelLine * measure.Lines(WidgetCopy.SettingsOrder, orderLabelWidth, fourteen, labelLine);
            int header = Math.Max(
                Math.Max(CardPlacement.Scale(OrderHeaderAt96, dpi), gaugeSize.Height + (2 * rowPad)), orderLabelHeight + (2 * rowPad));
            var orderLabel = new Rectangle(labelLeft, top + ((header - orderLabelHeight) / 2), orderLabelWidth, orderLabelHeight);
            var chevron = new Rectangle(chevronX, top + ((header - chevronBox) / 2), chevronBox, chevronBox);
            var preview = new Rectangle(previewX, top + ((header - gaugeSize.Height) / 2), gaugeSize.Width, gaugeSize.Height);
            var headBounds = new Rectangle(surfaceLeft, top, surfaceWidth, header);
            items.Add(Dressed(new SettingsItem(
                SettingsItemKind.Row, SettingsRowId.GaugeOrder, WidgetCopy.SettingsOrder, null, false, headBounds, orderLabel,
                Rectangle.Empty, Rectangle.Empty, headBounds, Rectangle.Empty), separated: false)
                with { Chevron = chevron, ChevronGlyph = values.GaugeOrderExpanded ? FluentGlyphs.ChevronUp : FluentGlyphs.ChevronDown, Preview = preview });
            targets.Add(new SettingsTarget(SettingsRowId.GaugeOrder, SettingsPart.Expand));
            y += header;

            if (values.GaugeOrderExpanded)
            {
                int pad = CardPlacement.Scale(OrderPaddingAt96, dpi);
                int tileGap = CardPlacement.Scale(OrderTileGapAt96, dpi);
                int across = surfaceWidth - (2 * pad) - (2 * tileGap);
                int tileWidth = across / 3;
                int spare = across - (3 * tileWidth);
                // The design's 52 at 100%, scaled like every other figure. (The gauge is 40 high in it, so 6 above and below; at 125% the gauge's
                // 50 in a tile of 65 leaves 7 and 8, which is the rounding of a half pixel and not a reason to grow the tile.)
                int tileHeight = CardPlacement.Scale(OrderTileHeightAt96, dpi);
                int gridTop = y + pad;
                var tiles = new List<Rectangle>(6);
                for (int i = 0; i < 6; i++)
                {
                    // The spare pixels of the division go to the first columns, so the grid fills the surface to its padding.
                    int column = i % 3;
                    int left = surfaceLeft + pad + (column * (tileWidth + tileGap)) + Math.Min(column, spare);
                    tiles.Add(new Rectangle(left, gridTop + ((i / 3) * (tileHeight + tileGap)), tileWidth + (column < spare ? 1 : 0), tileHeight));
                }

                int gridHeight = tiles[^1].Bottom + pad - y;
                var gridBounds = new Rectangle(surfaceLeft, y, surfaceWidth, gridHeight);
                items.Add(new SettingsItem(
                    SettingsItemKind.Row, SettingsRowId.GaugeOrder, string.Empty, null, false, gridBounds, Rectangle.Empty, Rectangle.Empty,
                    Rectangle.Empty, Rectangle.Empty, Rectangle.Empty)
                {
                    Tiles = tiles,
                    DividerAbove = true,
                    Nested = true,
                    Tip = SettingsRows.For(SettingsRowId.GaugeOrder).Tip,
                    AccessibleName = SettingsRows.For(SettingsRowId.GaugeOrder).Name,
                });
                targets.Add(new SettingsTarget(SettingsRowId.GaugeOrder, SettingsPart.Tile, (int)GaugeOrders.FromStored(values.GaugeOrder)));
                y += gridHeight;
            }

            EndGroup();
        }

        // ---- Behaviour

        Head(WidgetCopy.SettingsGroupBehaviour);

        // The case-open card: a switch and a chevron; expanded, its close and display choices under it in the same surface, and a
        // box per display when there is more than one.
        BeginGroup();
        Row(
            SettingsRowId.CaseCard, WidgetCopy.SettingsCaseCard, null, false, false, toggleW + CardPlacement.Scale(ToggleChevronGapAt96, dpi) + chevronBox,
            (top, mid) => (
                new Rectangle(right - chevronBox - CardPlacement.Scale(ToggleChevronGapAt96, dpi) - toggleW, mid - (toggleH / 2), toggleW, toggleH),
                new Rectangle(right - chevronBox, top + ((control - chevronBox) / 2), chevronBox, chevronBox),
                Rectangle.Empty),
            SettingsPart.Toggle, SettingsPart.Expand);
        if (values.CaseOpenCardExpanded)
        {
            nested = true;
            int closeW = CaseOpenCardClose.Choices.Max(c => ButtonWidth(CaseOpenCardClose.Label(c), false));
            Row(
                SettingsRowId.CaseCardClose, WidgetCopy.SettingsCaseCardClose, null, false, false, closeW,
                (top, _) => (new Rectangle(right - closeW, top, closeW, control), Rectangle.Empty, Rectangle.Empty),
                SettingsPart.Choice);

            int placesW = new[] { WidgetCopy.CaseCardWhereTheGaugeIs, WidgetCopy.CaseCardAllDisplays, WidgetCopy.CaseCardChosenDisplays }
                .Max(t => ButtonWidth(t, false));
            Row(
                SettingsRowId.CaseCardDisplays, WidgetCopy.SettingsCaseCardDisplays, null, false, false, placesW,
                (top, _) => (new Rectangle(right - placesW, top, placesW, control), Rectangle.Empty, Rectangle.Empty),
                SettingsPart.Choice);

            if (values.CaseOpenCardDisplayOptions.Count > 1)
            {
                int box = CardPlacement.Scale(CheckBoxAt96, dpi);
                for (int i = 0; i < values.CaseOpenCardDisplayOptions.Count; i++)
                {
                    Row(
                        SettingsRowId.CaseCardDisplay, values.CaseOpenCardDisplayOptions[i].Label, null, false, false, box,
                        (_, mid) => (new Rectangle(right - box, mid - (box / 2), box, box), Rectangle.Empty, Rectangle.Empty));
                    items[^1] = items[^1] with { Index = i };
                    targets.Add(new SettingsTarget(SettingsRowId.CaseCardDisplay, SettingsPart.Check, i));
                }
            }

            nested = false;
        }

        EndGroup();

        // Hand back: a caption says when.
        ToggleRow(SettingsRowId.HandBack, WidgetCopy.SettingsHandBack, WidgetCopy.SettingsHandBackCaption);

        // ---- Audio

        Head(WidgetCopy.SettingsGroupAudio);

        // The Hands-Free "microphone off" mode. While it is on the row says what to do next in one line under it, and a
        // second row in the same surface opens sound settings, unless Windows already lists the microphone as disabled.
        string? micNote = !values.HandsFreeMicrophoneOff ? null : values.MicrophoneState switch
        {
            MicrophoneRowState.ConnectFirst => WidgetCopy.MicConnectFirst,
            MicrophoneRowState.OffInWindows => WidgetCopy.MicOffInWindows,
            _ => WidgetCopy.MicGuidance,
        };
        BeginGroup();
        Row(
            SettingsRowId.MicrophoneOff, WidgetCopy.SettingsMicOff, micNote, subIsProblem: false, subFullWidth: true, toggleW,
            (_, mid) => (new Rectangle(right - toggleW, mid - (toggleH / 2), toggleW, toggleH), Rectangle.Empty, Rectangle.Empty),
            SettingsPart.Toggle);
        if (values.HandsFreeMicrophoneOff && values.MicrophoneState != MicrophoneRowState.OffInWindows)
        {
            int openW = ButtonWidth(WidgetCopy.OpenButton, glyph: true);
            Row(
                SettingsRowId.SoundSettings, WidgetCopy.SettingsSoundSettings, null, subIsProblem: false, subFullWidth: false, openW,
                (top, _) => (new Rectangle(right - openW, top, openW, control), Rectangle.Empty, Rectangle.Empty),
                SettingsPart.Button);
        }

        EndGroup();

        // ---- Shortcuts

        Head(WidgetCopy.SettingsShortcuts);

        int shortcutW = CardPlacement.Scale(ShortcutWidthAt96, dpi);
        int stepGap = CardPlacement.Scale(ControlGapAt96, dpi);
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

        // ---- About

        Head(WidgetCopy.SettingsAbout);
        NavigationRow(SettingsRowId.About, WidgetCopy.SettingsUpdates, values.InstalledVersion is null ? null : "Version " + values.InstalledVersion);

        // ---- More: one expander row, and what it holds in the same surface

        y += CardPlacement.Scale(GroupTopAt96 - RowGapAt96, dpi);
        BeginGroup();
        Row(
            SettingsRowId.More, WidgetCopy.SettingsMore, null, false, false, chevronBox,
            (top, _) => (Rectangle.Empty, Rectangle.Empty, new Rectangle(right - chevronBox, top + ((control - chevronBox) / 2), chevronBox, chevronBox)),
            SettingsPart.Expand);
        {
            SettingsItem row = items[^1];
            items[^1] = row with { B = row.Bounds, Chevron = row.Value, ChevronGlyph = values.MoreExpanded ? FluentGlyphs.ChevronUp : FluentGlyphs.ChevronDown };
        }

        if (values.MoreExpanded)
        {
            // Two places, in a combo like the display's: it names the place and a press moves to the other.
            int positionW = Math.Max(ComboMin(dpi), Math.Max(ButtonWidth(WidgetCopy.PositionRightEnd, false), ButtonWidth(WidgetCopy.PositionNextToApps, false)));
            Row(
                SettingsRowId.GaugePosition, WidgetCopy.SettingsGaugePosition, null, false, false, positionW,
                (top, _) => (new Rectangle(right - positionW, top, positionW, control), Rectangle.Empty, Rectangle.Empty),
                SettingsPart.Choice);

            int textW = CardPlacement.Scale(TextBoxWidthAt96, dpi);
            Row(
                SettingsRowId.OtherDevice, WidgetCopy.SettingsOtherDevice, null, false, false, textW,
                (top, _) => (new Rectangle(right - textW, top, textW, control), Rectangle.Empty, Rectangle.Empty),
                SettingsPart.Text);

            ToggleRow(SettingsRowId.PauseBud, WidgetCopy.SettingsPauseBud, values.InEarProofMissing ? WidgetCopy.SettingsWaitsOnInEar : null);
            ToggleRow(SettingsRowId.PauseLeave, WidgetCopy.SettingsPauseLeave, null);

            int valueW = CardPlacement.Scale(StepperValueWidthAt96, dpi);
            int stepperW = control + stepGap + valueW + stepGap + control;
            Row(
                SettingsRowId.LowBattery, WidgetCopy.SettingsLowBattery, null, false, false, stepperW,
                (top, _) => (
                    new Rectangle(right - stepperW, top, control, control),
                    new Rectangle(right - control, top, control, control),
                    new Rectangle(right - stepperW + control + stepGap, top, valueW, control)),
                SettingsPart.Minus, SettingsPart.Plus);

            ToggleRow(SettingsRowId.FullyCharged, WidgetCopy.SettingsFullyCharged, null);
            ToggleRow(SettingsRowId.LeftClick, WidgetCopy.SettingsLeftClick, null);
            NavigationRow(SettingsRowId.History, WidgetCopy.SettingsHistory, null);

            int copyW = ButtonWidth(WidgetCopy.CopyButton, glyph: false);
            Row(
                SettingsRowId.CopyDiagnostics, WidgetCopy.SettingsCopyDiagnostics, null, false, false, copyW,
                (top, _) => (new Rectangle(right - copyW, top, copyW, control), Rectangle.Empty, Rectangle.Empty),
                SettingsPart.Button);
        }

        EndGroup();

        int bodyHeight = y - rowGap + CardPlacement.Scale(BodyBottomAt96, dpi);
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
                Chevron = Shift(item.Chevron, offset),
                Preview = Shift(item.Preview, offset),
                Tiles = item.Tiles.Select(t => Shift(t, offset)).ToList(),
            });
        }

        return new SettingsLayout(frame, shifted, targets, surfaces.Select(r => Shift(r, offset)).ToList());
    }

    private const int LabelControlGapAtDpi = 12;

    private static int ComboMin(int dpi) => CardPlacement.Scale(ComboMinWidthAt96, dpi);

    private static Rectangle Shift(Rectangle rect, int dy) => rect.IsEmpty ? rect : new Rectangle(rect.X, rect.Y + dy, rect.Width, rect.Height);
}
