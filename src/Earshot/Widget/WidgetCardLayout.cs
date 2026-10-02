using Earshot.Popup;

namespace Earshot.Widget;

// Pure row layout for the widget card. No window, no drawing. Every figure is the design's, at 100% display scale
// and 100% text size, in epx; a length that holds text grows with the text size t (Windows' text size, 1 to 2.25) and the
// rest does not. Padding, gaps, radii and the card's width of 360 do not grow with t; the card grows in height only.
//
// Main view, top to bottom: 12 padding; the title row (the title, the battery refresh icon and the gear that opens the
// settings, when the view has them), as high as an icon button (16t + 16); a 12 section gap; three columns (Left, Right,
// Case), 8 apart, each a label (16t), a mark (36t by 32t), the value (28t), a bar (3 high, 64 wide, a 1 px track centred
// and a 3 px fill) and the read-time line (16t, always reserved, empty while the value is fresh), 6 apart; a 12 gap; the
// status row (20t + 12 high); a 12 gap; the update line when a check found a newer version; the Connect/Disconnect button
// (20t + 12) and, only when the snapshot says auto-pause is available, the switch row beneath it; 16 bottom padding. The
// sides are 16 padding. A part with no value says so in its own column.
//
// Sub-pages (settings, updates): width 360, on the sub-page frame (SubPageFrame): the header and footer
// are the frame's, the body is the page's own.
internal static class WidgetCardLayout
{
    public const int WidthAt96 = 360;
    public const int PaddingAt96 = 16;
    public const int ColumnGapAt96 = 8;
    public const int SectionGapAt96 = 12;
    public const int ColumnInnerGapAt96 = 6;
    public const int MarkWidthAt96 = 36;
    public const int MarkHeightAt96 = 32;
    public const int ValueHeightAt96 = 28;
    public const int ColumnLabelHeightAt96 = 16;
    public const int ReadTimeHeightAt96 = 16;
    public const int BarWidthAt96 = 64;
    public const int BarFillHeightAt96 = 3;
    public const int BoltSlotAt96 = 12;

    // The card's pieces that follow the text size, in epx at 100%: an icon button is 16t + 16 square and a row or button 20t + 12 high.
    public const int IconButtonBaseAt96 = 16;
    public const int IconButtonExtraAt96 = 16;
    public const int RowBaseAt96 = 20;
    public const int RowExtraAt96 = 12;

    // The title row: 12 above it, as high as the icon button, the gear a button whose right edge is 8 into the padding.
    public const int TopPaddingAt96 = 12;
    public const int GearOverhangAt96 = 8;

    // The update line: 40 high, full width, a divider above, the caption left and a 24 high button right.
    public const int UpdateLineHeightAt96 = 40;
    public const int UpdateLinePaddingAt96 = 8;
    public const int UpdateButtonHeightAt96 = 24;
    public const int UpdateButtonPaddingAt96 = 10;
    public const int UpdateButtonMinWidthAt96 = 60;

    // The sub-pages' body.
    public const int BodyTopAt96 = 4;
    public const int BodyBottomAt96 = 20;
    public const int BodySideAt96 = 16;
    public const int BodyItemGapAt96 = 16;
    public const int PromptLineAt96 = 20;
    public const int CaptionLineAt96 = 16;
    public const int PromptCaptionGapAt96 = 2;
    public const int StatusIconAt96 = 20;
    public const int StatusIconGapAt96 = 12;
    public const int ToggleWidthAt96 = 40;
    public const int ToggleHeightAt96 = 20;
    public const int ProgressBarHeightAt96 = 4;
    public const int ProgressTextWidthAt96 = 36;
    public const int ProgressGapAt96 = 12;

    public static int WidthFor(int dpi) => CardPlacement.Scale(WidthAt96, dpi);

    // A length that follows the text size: at96 scaled to the display and grown by t.
    private static int Follow(int at96, int dpi, double textScale) => TextFit.Grow(at96, dpi, textScale);

    // The icon button's box, 16t + 16 (32 at 100%, 40 at 150%): the title row's height and the buttons' size.
    public static int IconButtonSize(int dpi, double textScale) =>
        Math.Max(CardPlacement.Scale(IconButtonBaseAt96 + IconButtonExtraAt96, dpi), TextScaled(IconButtonBaseAt96, IconButtonExtraAt96, dpi, textScale));

    // A row or a button, 20t + 12 high (32 at 100%, 42 at 150%).
    public static int RowHeight(int dpi, double textScale) =>
        Math.Max(CardPlacement.Scale(RowBaseAt96 + RowExtraAt96, dpi), TextScaled(RowBaseAt96, RowExtraAt96, dpi, textScale));

    // (base * t + extra) at the display scale, rounded half away from zero.
    private static int TextScaled(int baseAt96, int extraAt96, int dpi, double textScale)
    {
        double t = Math.Clamp(double.IsFinite(textScale) ? textScale : 1.0, TypeRamp.MinTextScale, TypeRamp.MaxTextScale);
        double s = (dpi > 0 ? dpi : CardPlacement.BaseDpi) / (double)CardPlacement.BaseDpi;
        return (int)Math.Round(((baseAt96 * t) + extraAt96) * s, MidpointRounding.AwayFromZero);
    }

    // One column's parts, in client pixels. Label ("L", "R" or "Case") sits above the mark; the value is centred in the
    // column; the bolt's slot is reserved at the value's right (a design choice: a fixed slot at the column's right end,
    // so the value stays centred whether or not a bolt is drawn); the read-time line is under the bar.
    internal readonly record struct ColumnLayout(
        Rectangle Label, Rectangle Glyph, Rectangle Bar, Rectangle Percent, Rectangle BoltSlot = default, Rectangle ReadTime = default);

    // The whole card, in client pixels. Switch is Rectangle.Empty when ShowSwitch is false; nothing reads it then.
    // WhereLine is the status row.
    internal sealed record Layout(
        int Width,
        int Height,
        bool ShowColumns,
        ColumnLayout Left,
        ColumnLayout Right,
        ColumnLayout Case,
        Rectangle WhereLine,
        Rectangle Button,
        bool ShowSwitch,
        Rectangle Switch,
        Rectangle Title = default,
        Rectangle Gear = default,
        bool ShowUpdateLine = false,
        Rectangle UpdateLine = default,
        Rectangle UpdateCaption = default,
        Rectangle UpdateButton = default,
        Rectangle Refresh = default);

    // showGear: the view has a button at the title row's end, the settings gear (the case-open card has its close button
    // there instead, in the same place). showRefresh: the view has the battery
    // refresh icon, a button the size of the gear's, left of it (never without the gear). showUpdateLine: a check found a
    // newer version; updateButtonWidth is the "Update" button's own width (its text and padding), or 0 for the
    // least width the design gives it.
    public static Layout Compute(
        int dpi, bool showSwitch, bool showGear = true, bool showUpdateLine = false, int updateButtonWidth = 0, bool showRefresh = true,
        double textScale = 1.0)
    {
        int width = WidthFor(dpi);
        int pad = CardPlacement.Scale(PaddingAt96, dpi);
        int colGap = CardPlacement.Scale(ColumnGapAt96, dpi);
        int section = CardPlacement.Scale(SectionGapAt96, dpi);
        int inner = CardPlacement.Scale(ColumnInnerGapAt96, dpi);
        int markWidth = Follow(MarkWidthAt96, dpi, textScale);
        int markHeight = Follow(MarkHeightAt96, dpi, textScale);
        int valueHeight = Follow(ValueHeightAt96, dpi, textScale);
        int labelHeight = Follow(ColumnLabelHeightAt96, dpi, textScale);
        int readHeight = Follow(ReadTimeHeightAt96, dpi, textScale);
        int barWidth = CardPlacement.Scale(BarWidthAt96, dpi);
        int barHeight = CardPlacement.Scale(BarFillHeightAt96, dpi);
        int boltSlot = Follow(BoltSlotAt96, dpi, textScale);
        int rowHeight = RowHeight(dpi, textScale);
        int switchHeight = rowHeight;

        int contentWidth = Math.Max(1, width - (2 * pad));
        int colWidth = Math.Max(1, (contentWidth - (2 * colGap)) / 3);

        int titleRowHeight = IconButtonSize(dpi, textScale);
        int gearSize = titleRowHeight;
        int gearOverhang = CardPlacement.Scale(GearOverhangAt96, dpi);
        int titleTop = CardPlacement.Scale(TopPaddingAt96, dpi);
        Rectangle gear = showGear
            ? new Rectangle(width - pad + gearOverhang - gearSize, titleTop, gearSize, gearSize)
            : Rectangle.Empty;
        Rectangle refresh = showGear && showRefresh
            ? new Rectangle(gear.X - gearSize, gear.Y, gearSize, gearSize)
            : Rectangle.Empty;
        int titleRight = !refresh.IsEmpty ? refresh.X : showGear ? gear.X : width - pad;
        var title = new Rectangle(pad, titleTop, Math.Max(1, titleRight - pad), titleRowHeight);

        int y = title.Bottom + section;
        int leftX = pad;
        int midX = leftX + colWidth + colGap;
        int rightX = midX + colWidth + colGap;
        ColumnLayout Col(int x) => Column(x, y, colWidth, labelHeight, inner, markWidth, markHeight, valueHeight, barWidth, barHeight, readHeight, boltSlot);
        ColumnLayout left = Col(leftX);
        ColumnLayout right = Col(midX);
        ColumnLayout box = Col(rightX);

        int rowsBottom = left.ReadTime.Bottom;
        var whereLine = new Rectangle(pad, rowsBottom + section, contentWidth, rowHeight);
        int afterLines = whereLine.Bottom;

        Rectangle updateLine = Rectangle.Empty;
        Rectangle updateCaption = Rectangle.Empty;
        Rectangle updateButton = Rectangle.Empty;
        int buttonY = afterLines + section;
        if (showUpdateLine)
        {
            int updateLineHeight = TextFit.Grow(UpdateLineHeightAt96, dpi, textScale);
            int linePad = CardPlacement.Scale(UpdateLinePaddingAt96, dpi);
            int updateButtonHeight = TextFit.Fit(UpdateButtonHeightAt96, TypeRole.Caption, 8, dpi, textScale);
            int buttonWidth = updateButtonWidth > 0 ? updateButtonWidth : CardPlacement.Scale(UpdateButtonMinWidthAt96, dpi);
            updateLine = new Rectangle(0, afterLines + section, width, updateLineHeight);
            updateButton = new Rectangle(width - pad - buttonWidth, updateLine.Y + linePad, buttonWidth, updateButtonHeight);
            updateCaption = new Rectangle(pad, updateLine.Y + linePad, Math.Max(1, updateButton.X - pad - linePad), updateButtonHeight);
            buttonY = updateLine.Bottom + pad;
        }

        var button = new Rectangle(pad, buttonY, contentWidth, rowHeight);

        Rectangle switchRect = Rectangle.Empty;
        int bottom = button.Bottom + pad;
        if (showSwitch)
        {
            switchRect = new Rectangle(pad, button.Bottom + section, contentWidth, switchHeight);
            bottom = switchRect.Bottom + pad;
        }

        return new Layout(
            width, bottom, ShowColumns: true, left, right, box, whereLine, button,
            showSwitch, switchRect,
            title, gear, showUpdateLine, updateLine, updateCaption, updateButton, refresh);
    }

    private static ColumnLayout Column(
        int x, int y, int colWidth, int labelHeight, int gap, int markWidth, int markHeight, int valueHeight, int barWidth, int barHeight, int readHeight, int boltSlot)
    {
        var label = new Rectangle(x, y, colWidth, labelHeight);
        var mark = new Rectangle(x + ((colWidth - markWidth) / 2), label.Bottom + gap, markWidth, markHeight);
        var percent = new Rectangle(x, mark.Bottom + gap, colWidth, valueHeight);
        var bar = new Rectangle(x + ((colWidth - barWidth) / 2), percent.Bottom + gap, barWidth, barHeight);
        var bolt = new Rectangle(x + colWidth - boltSlot - gap, percent.Y + ((valueHeight - boltSlot) / 2), boltSlot, boltSlot);
        var read = new Rectangle(x, bar.Bottom + gap, colWidth, readHeight);
        return new ColumnLayout(label, mark, bar, percent, bolt, read);
    }

    // The parts of one sub-page, in client pixels. Rectangle.Empty for a part the page does not have.
    internal sealed record SetupLayout(
        SubPageFrame.FrameLayout Frame,
        Rectangle Prompt,
        Rectangle Caption,
        Rectangle StatusIcon,
        Rectangle StatusText,
        Rectangle StatusSub,
        Rectangle Progress = default)
    {
        // The updates page (a view with Rows): the status row's surface, the 40 by 40 tile, the title and caption beside it and the
        // action button at its right end (when the page has one button; with more they stay in the footer), then one surface per row.
        public Rectangle StatusSurface { get; init; }

        public Rectangle Tile { get; init; }

        public Rectangle StatusTitle { get; init; }

        public Rectangle StatusCaption { get; init; }

        public Rectangle Action { get; init; }

        public IReadOnlyList<UpdatesRowLayout> UpdateRows { get; init; } = [];

        // The battery history page's parts, or null on every other page.
        public HistoryPageLayout? History { get; init; }
    }

    // One row of the updates page: Index 0 is Check automatically (a toggle), 1 What's new (an external link glyph) and 2 Repair (a
    // button). Control is where the toggle, the glyph or the button is.
    internal sealed record UpdatesRowLayout(int Index, Rectangle Surface, Rectangle Icon, Rectangle Label, Rectangle Control);

    // promptLines, captionLines, subLines and statusLines: how many lines the prompt (14 px), the caption (12 px), the
    // status sub-line (12 px) and the status line (14 px) wrap to at this width, measured by the caller since this layout draws nothing.
    public static SetupLayout Setup(
        SetupViewModel view, int dpi, int promptLines = 1, int captionLines = 1, int subLines = 1, double textScale = 1.0, int actionWidth = 0,
        int titleLines = 1, ICardTextMeasure? measure = null, int statusLines = 1)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Rows is not null)
        {
            return UpdatesPage(view, dpi, captionLines, subLines, textScale, actionWidth, titleLines, measure);
        }

        if (view.History is not null)
        {
            HistoryPageLayout page = HistoryPageLayout.Compute(dpi, textScale);
            return new SetupLayout(page.Frame, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty, Rectangle.Empty) { History = page };
        }

        int side = CardPlacement.Scale(BodySideAt96, dpi);
        int itemGap = CardPlacement.Scale(BodyItemGapAt96, dpi);
        int promptLine = TextFit.Grow(PromptLineAt96, dpi, textScale);
        int captionLine = TextFit.Grow(CaptionLineAt96, dpi, textScale);
        int iconSize = CardPlacement.Scale(StatusIconAt96, dpi);
        int iconGap = CardPlacement.Scale(StatusIconGapAt96, dpi);
        int width = CardPlacement.Scale(SubPageFrame.WidthAt96, dpi);
        int contentWidth = width - (2 * side);

        // Body items top to bottom, positioned relative to the body's own top; the frame's body starts under
        // the header, so they are moved down by it at the end.
        int y = CardPlacement.Scale(BodyTopAt96, dpi);
        bool first = true;
        int Place(int height)
        {
            if (!first)
            {
                y += itemGap;
            }

            first = false;
            int top = y;
            y += height;
            return top;
        }

        Rectangle prompt = Rectangle.Empty;
        Rectangle caption = Rectangle.Empty;
        if (view.Prompt is not null)
        {
            int promptHeight = promptLine * Math.Max(1, promptLines);
            int captionHeight = view.Caption is null ? 0 : CardPlacement.Scale(PromptCaptionGapAt96, dpi) + (captionLine * Math.Max(1, captionLines));
            int top = Place(promptHeight + captionHeight);
            prompt = new Rectangle(side, top, contentWidth, promptHeight);
            if (view.Caption is not null)
            {
                caption = new Rectangle(side, prompt.Bottom + CardPlacement.Scale(PromptCaptionGapAt96, dpi), contentWidth, captionLine * Math.Max(1, captionLines));
            }
        }

        Rectangle statusIcon = Rectangle.Empty;
        Rectangle statusText = Rectangle.Empty;
        Rectangle statusSub = Rectangle.Empty;
        if (view.Status is not null)
        {
            int statusTextHeight = promptLine * Math.Max(1, statusLines);
            int textHeight = statusTextHeight + (view.StatusSub is null ? 0 : captionLine * Math.Max(1, subLines));
            int rowHeight = Math.Max(iconSize, textHeight);
            int top = Place(rowHeight);
            statusIcon = new Rectangle(side, top + ((rowHeight - iconSize) / 2), iconSize, iconSize);
            int textX = statusIcon.Right + iconGap;
            int textTop = top + ((rowHeight - textHeight) / 2);
            statusText = new Rectangle(textX, textTop, width - side - textX, statusTextHeight);
            if (view.StatusSub is not null)
            {
                statusSub = new Rectangle(textX, statusText.Bottom, width - side - textX, captionLine * Math.Max(1, subLines));
            }
        }

        // A caption with no prompt above it (the update page's cause of a failure) is its own item.
        if (view.Prompt is null && view.Caption is not null)
        {
            int captionHeight = captionLine * Math.Max(1, captionLines);
            int top = Place(captionHeight);
            caption = new Rectangle(side, top, contentWidth, captionHeight);
        }

        Rectangle progress = Rectangle.Empty;
        if (view.ShowProgress)
        {
            int progressTop = Place(captionLine);
            progress = new Rectangle(side, progressTop, contentWidth, captionLine);
        }

        int bodyHeight = Math.Max(y + CardPlacement.Scale(BodyBottomAt96, dpi), CardPlacement.Scale(view.MinBodyAt96, dpi));
        SubPageFrame.FrameLayout frame = SubPageFrame.Compute(dpi, bodyHeight, view.Buttons.Count, textScale);
        int offset = frame.Body.Y;
        return new SetupLayout(
            frame, Shift(prompt, offset), Shift(caption, offset), Shift(statusIcon, offset), Shift(statusText, offset), Shift(statusSub, offset),
            Shift(progress, offset));
    }

    // The updates page, in the settings page's own style: body padding 8 and 12, surfaces 4 apart, a status row (the 40 by 40 tile, the
    // title, the caption and the action) and a row each for Check automatically, What's new and, with an install, Repair. A row is as
    // high as a single-line settings row, 20t + 12 for its control and 8 above and below. subLines is how many lines the status
    // caption wraps to; captionLines how many the cause under it wraps to. actionWidth is the action button's width, measured by the
    // caller from its words (0 for a default). titleLines is how many lines the title wraps to; measure counts the lines a row's name
    // wraps to beside its control (without one every name is a line), and a row grows with them.
    internal static SetupLayout UpdatesPage(
        SetupViewModel view, int dpi, int captionLines, int subLines, double textScale, int actionWidth, int titleLines = 1, ICardTextMeasure? measure = null)
    {
        UpdatesRows rows = view.Rows!;
        int width = CardPlacement.Scale(SubPageFrame.WidthAt96, dpi);
        int side = CardPlacement.Scale(SettingsPageLayout.BodySideAt96, dpi);
        int surfaceWidth = width - (2 * side);
        int rowGap = CardPlacement.Scale(SettingsPageLayout.RowGapAt96, dpi);
        int pad = CardPlacement.Scale(SettingsPageLayout.RowPadVerticalAt96, dpi);
        int padLeft = CardPlacement.Scale(SettingsPageLayout.RowPadLeftAt96, dpi);
        int padRight = CardPlacement.Scale(SettingsPageLayout.RowPadRightAt96, dpi);
        int control = RowHeight(dpi, textScale);
        int titleLine = TextFit.Grow(PromptLineAt96, dpi, textScale);
        int captionLine = TextFit.Grow(CaptionLineAt96, dpi, textScale);
        int tileSize = CardPlacement.Scale(UpdatesTileAt96, dpi);
        int gap = CardPlacement.Scale(StatusIconGapAt96, dpi);
        bool inline = view.Buttons.Count == 1;

        int y = CardPlacement.Scale(SettingsPageLayout.BodyTopAt96, dpi);
        int textBlock = (titleLine * Math.Max(1, titleLines)) + (captionLine * Math.Max(1, subLines));
        int statusHeight = Math.Max(Math.Max(tileSize, textBlock) + (2 * pad), CardPlacement.Scale(UpdatesStatusMinAt96, dpi));
        var surface = new Rectangle(side, y, surfaceWidth, statusHeight);
        var tile = new Rectangle(surface.X + padLeft, surface.Y + ((statusHeight - tileSize) / 2), tileSize, tileSize);
        int buttonWidth = actionWidth > 0 ? actionWidth : CardPlacement.Scale(UpdateButtonMinWidthAt96, dpi);
        Rectangle action = inline
            ? new Rectangle(surface.Right - padRight - buttonWidth, surface.Y + ((statusHeight - control) / 2), buttonWidth, control)
            : Rectangle.Empty;
        int iconBox = CardPlacement.Scale(StatusIconAt96, dpi);
        int textLeft = tile.Right + gap;
        int textRight = (inline ? action.X : surface.Right - padRight) - gap;
        int textTop = surface.Y + ((statusHeight - textBlock) / 2);
        var title = new Rectangle(textLeft, textTop, Math.Max(1, textRight - textLeft), titleLine * Math.Max(1, titleLines));
        var caption = new Rectangle(textLeft, title.Bottom, Math.Max(1, textRight - textLeft), captionLine * Math.Max(1, subLines));

        // The turning arc while a check runs sits where the action would be.
        Rectangle statusIcon = new(surface.Right - padRight - iconBox, surface.Y + ((statusHeight - iconBox) / 2), iconBox, iconBox);
        y = surface.Bottom + rowGap;

        Rectangle progress = Rectangle.Empty;
        if (view.ShowProgress)
        {
            progress = new Rectangle(surface.X + padLeft, y + pad, surfaceWidth - padLeft - padRight, captionLine);
            y = progress.Bottom + pad + rowGap;
        }

        Rectangle cause = Rectangle.Empty;
        if (view.Caption is not null)
        {
            int causeHeight = captionLine * Math.Max(1, captionLines);
            cause = new Rectangle(surface.X + padLeft, y, surfaceWidth - padLeft - padRight, causeHeight);
            y = cause.Bottom + rowGap;
        }

        var list = new List<UpdatesRowLayout>();
        int iconSize = CardPlacement.Scale(SettingsPageLayout.IconSizeAt96, dpi);
        int labelLeft = surface.X + padLeft + iconSize + CardPlacement.Scale(SettingsPageLayout.IconGapAt96, dpi);
        int toggleWidth = CardPlacement.Scale(ToggleWidthAt96, dpi);
        int toggleHeight = CardPlacement.Scale(ToggleHeightAt96, dpi);
        int glyphBox = CardPlacement.Scale(SettingsPageLayout.ChevronAt96, dpi) + CardPlacement.Scale(8, dpi);
        int repairWidth = buttonWidth;
        int fourteen = CardPlacement.Scale(14, dpi);
        int labelLine = TextFit.Grow(SettingsPageLayout.LabelLineAt96, dpi, textScale);
        for (int i = 0; i < 3; i++)
        {
            if (i == 2 && !rows.ShowRepair)
            {
                continue;
            }

            int controlWidth = i switch { 0 => toggleWidth, 1 => glyphBox, _ => repairWidth };
            int controlHeight = i == 0 ? toggleHeight : i == 1 ? glyphBox : control;

            // The name wraps in the room left of the control, and the row is as high as it needs: the control row (20t + 12) or the
            // name's lines, whichever is more, with 8 above and below.
            string name = i switch { 0 => WidgetCopy.CheckAutomatically, 1 => WidgetCopy.SettingsWhatsNew, _ => WidgetCopy.RepairEarshot };
            int labelWidth = Math.Max(1, (surface.Right - padRight - controlWidth) - gap - labelLeft);
            int nameLines = measure is null ? 1 : Math.Max(1, measure.Lines(name, labelWidth, fourteen, labelLine));
            int nameHeight = labelLine * nameLines;
            int rowHeight = Math.Max(control, nameHeight) + (2 * pad);

            var rowSurface = new Rectangle(side, y, surfaceWidth, rowHeight);
            var controlRect = new Rectangle(rowSurface.Right - padRight - controlWidth, rowSurface.Y + ((rowHeight - controlHeight) / 2), controlWidth, controlHeight);
            var icon = new Rectangle(surface.X + padLeft, rowSurface.Y + ((rowHeight - iconSize) / 2), iconSize, iconSize);
            var label = new Rectangle(labelLeft, rowSurface.Y + ((rowHeight - nameHeight) / 2), labelWidth, nameHeight);
            list.Add(new UpdatesRowLayout(i, rowSurface, icon, label, controlRect));
            y = rowSurface.Bottom + rowGap;
        }

        int bodyHeight = y - rowGap + CardPlacement.Scale(SettingsPageLayout.BodyBottomAt96, dpi);
        SubPageFrame.FrameLayout frame = SubPageFrame.Compute(dpi, bodyHeight, inline ? 0 : view.Buttons.Count, textScale);
        int offset = frame.Body.Y;
        return new SetupLayout(
            frame, Rectangle.Empty, Shift(cause, offset), Shift(statusIcon, offset), Shift(title, offset), Shift(caption, offset), Shift(progress, offset))
        {
            StatusSurface = Shift(surface, offset),
            Tile = Shift(tile, offset),
            StatusTitle = Shift(title, offset),
            StatusCaption = Shift(caption, offset),
            Action = Shift(action, offset),
            UpdateRows = list.Select(r => new UpdatesRowLayout(r.Index, Shift(r.Surface, offset), Shift(r.Icon, offset), Shift(r.Label, offset), Shift(r.Control, offset))).ToList(),
        };
    }

    // The updates page's status tile, and its least row height (the tile and its padding), in epx at 100%.
    public const int UpdatesTileAt96 = 40;
    public const int UpdatesStatusMinAt96 = 56;

    private static Rectangle Shift(Rectangle rect, int dy) => rect.IsEmpty ? rect : new Rectangle(rect.X, rect.Y + dy, rect.Width, rect.Height);
}
