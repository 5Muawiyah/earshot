using Earshot.Popup;

namespace Earshot.Widget;

// Pure row layout for the widget card. No window, no drawing.
//
// Main view: width 360 at 96 DPI like every other page, padding 16: a title row (the title, the battery refresh icon
// and the gear that opens the settings, when the view has them), three columns (Left, Right, Case), the where line, the read line,
// the update line when a check found a newer version, then the Connect/Disconnect button and, only when the
// snapshot says auto-pause is available, the switch row beneath it. A part with no value says so in its own
// column.
//
// Sub-pages (settings, updates): width 360, on the sub-page frame (SubPageFrame): the header and footer
// are the frame's, the body is the page's own.
internal static class WidgetCardLayout
{
    public const int WidthAt96 = 360;
    public const int PaddingAt96 = 16;
    public const int ColumnGapAt96 = 8;
    public const int RowGapAt96 = 10;
    public const int ColumnGlyphSizeAt96 = 28;
    public const int ColumnBarWidthAt96 = 56;
    public const int ColumnBarHeightAt96 = 6;
    public const int ColumnGapToBarAt96 = 4;
    public const int TextLineHeightAt96 = 18;
    public const int ColumnLabelHeightAt96 = 14;
    public const int ColumnGapToLabelAt96 = 2;
    public const int ButtonHeightAt96 = 32;
    public const int SwitchRowHeightAt96 = 24;

    // The title row: 12 above it, 32 high, the gear a 32 by 32 button whose right edge is 8 into the padding.
    public const int TopPaddingAt96 = 12;
    public const int TitleRowAt96 = 32;
    public const int GearSizeAt96 = 32;
    public const int GearOverhangAt96 = 8;
    public const int TitleGapAt96 = 4;

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

    // One column's parts, in client pixels. Label ("L", "R" or "Case") sits above the glyph.
    internal readonly record struct ColumnLayout(Rectangle Label, Rectangle Glyph, Rectangle Bar, Rectangle Percent);

    // The whole card, in client pixels. Switch is Rectangle.Empty when ShowSwitch is false; nothing reads it then.
    internal sealed record Layout(
        int Width,
        int Height,
        bool ShowColumns,
        ColumnLayout Left,
        ColumnLayout Right,
        ColumnLayout Case,
        Rectangle WhereLine,
        Rectangle ReadLine,
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

    // showGear: the view has a settings button (the case-open notice does not). showRefresh: the view has the battery
    // refresh icon, a button the size of the gear's, left of it (never without the gear). showUpdateLine: a check found a
    // newer version; updateButtonWidth is the "Update" button's own width (its text and padding), or 0 for the
    // least width the design gives it.
    public static Layout Compute(
        int dpi, bool showSwitch, bool showGear = true, bool showUpdateLine = false, int updateButtonWidth = 0, bool showRefresh = true)
    {
        int width = WidthFor(dpi);
        int pad = CardPlacement.Scale(PaddingAt96, dpi);
        int colGap = CardPlacement.Scale(ColumnGapAt96, dpi);
        int rowGap = CardPlacement.Scale(RowGapAt96, dpi);
        int glyphSize = CardPlacement.Scale(ColumnGlyphSizeAt96, dpi);
        int barWidth = CardPlacement.Scale(ColumnBarWidthAt96, dpi);
        int barHeight = CardPlacement.Scale(ColumnBarHeightAt96, dpi);
        int gapToBar = CardPlacement.Scale(ColumnGapToBarAt96, dpi);
        int lineHeight = CardPlacement.Scale(TextLineHeightAt96, dpi);
        int labelHeight = CardPlacement.Scale(ColumnLabelHeightAt96, dpi);
        int gapToLabel = CardPlacement.Scale(ColumnGapToLabelAt96, dpi);
        int buttonHeight = CardPlacement.Scale(ButtonHeightAt96, dpi);
        int switchHeight = CardPlacement.Scale(SwitchRowHeightAt96, dpi);

        int contentWidth = Math.Max(1, width - (2 * pad));
        int colWidth = Math.Max(1, (contentWidth - (2 * colGap)) / 3);

        int titleRowHeight = CardPlacement.Scale(TitleRowAt96, dpi);
        int gearSize = CardPlacement.Scale(GearSizeAt96, dpi);
        int gearOverhang = CardPlacement.Scale(GearOverhangAt96, dpi);
        int titleTop = CardPlacement.Scale(TopPaddingAt96, dpi);
        Rectangle gear = showGear
            ? new Rectangle(width - pad + gearOverhang - gearSize, titleTop + ((titleRowHeight - gearSize) / 2), gearSize, gearSize)
            : Rectangle.Empty;
        Rectangle refresh = showGear && showRefresh
            ? new Rectangle(gear.X - gearSize, gear.Y, gearSize, gearSize)
            : Rectangle.Empty;
        int titleRight = !refresh.IsEmpty ? refresh.X : showGear ? gear.X : width - pad;
        var title = new Rectangle(pad, titleTop, Math.Max(1, titleRight - pad), titleRowHeight);

        int y = title.Bottom + CardPlacement.Scale(TitleGapAt96, dpi);
        int leftX = pad;
        int midX = leftX + colWidth + colGap;
        int rightX = midX + colWidth + colGap;
        ColumnLayout left = Column(leftX, y, colWidth, labelHeight, gapToLabel, glyphSize, barWidth, barHeight, gapToBar, lineHeight);
        ColumnLayout right = Column(midX, y, colWidth, labelHeight, gapToLabel, glyphSize, barWidth, barHeight, gapToBar, lineHeight);
        ColumnLayout box = Column(rightX, y, colWidth, labelHeight, gapToLabel, glyphSize, barWidth, barHeight, gapToBar, lineHeight);

        int rowsBottom = left.Percent.Bottom;
        var whereLine = new Rectangle(pad, rowsBottom + rowGap, contentWidth, lineHeight);
        var readLine = new Rectangle(pad, whereLine.Bottom, contentWidth, lineHeight);
        int afterLines = readLine.Bottom;

        Rectangle updateLine = Rectangle.Empty;
        Rectangle updateCaption = Rectangle.Empty;
        Rectangle updateButton = Rectangle.Empty;
        int buttonY = afterLines + rowGap;
        if (showUpdateLine)
        {
            int updateLineHeight = CardPlacement.Scale(UpdateLineHeightAt96, dpi);
            int linePad = CardPlacement.Scale(UpdateLinePaddingAt96, dpi);
            int updateButtonHeight = CardPlacement.Scale(UpdateButtonHeightAt96, dpi);
            int buttonWidth = updateButtonWidth > 0 ? updateButtonWidth : CardPlacement.Scale(UpdateButtonMinWidthAt96, dpi);
            updateLine = new Rectangle(0, afterLines + rowGap, width, updateLineHeight);
            updateButton = new Rectangle(width - pad - buttonWidth, updateLine.Y + linePad, buttonWidth, updateButtonHeight);
            updateCaption = new Rectangle(pad, updateLine.Y + linePad, Math.Max(1, updateButton.X - pad - linePad), updateButtonHeight);
            buttonY = updateLine.Bottom + pad;
        }

        var button = new Rectangle(pad, buttonY, contentWidth, buttonHeight);

        Rectangle switchRect = Rectangle.Empty;
        int bottom = button.Bottom + pad;
        if (showSwitch)
        {
            switchRect = new Rectangle(pad, button.Bottom + rowGap, contentWidth, switchHeight);
            bottom = switchRect.Bottom + pad;
        }

        return new Layout(
            width, bottom, ShowColumns: true, left, right, box, whereLine, readLine, button,
            showSwitch, switchRect,
            title, gear, showUpdateLine, updateLine, updateCaption, updateButton, refresh);
    }

    private static ColumnLayout Column(int x, int y, int colWidth, int labelHeight, int gapToLabel, int glyphSize, int barWidth, int barHeight, int gapToBar, int lineHeight)
    {
        var label = new Rectangle(x, y, colWidth, labelHeight);
        var glyph = new Rectangle(x + ((colWidth - glyphSize) / 2), label.Bottom + gapToLabel, glyphSize, glyphSize);
        var bar = new Rectangle(x + ((colWidth - barWidth) / 2), glyph.Bottom + gapToBar, barWidth, barHeight);
        var percent = new Rectangle(x, bar.Bottom + gapToBar, colWidth, lineHeight);
        return new ColumnLayout(label, glyph, bar, percent);
    }

    // The parts of one sub-page, in client pixels. Rectangle.Empty for a part the page does not have.
    internal sealed record SetupLayout(
        SubPageFrame.FrameLayout Frame,
        Rectangle Prompt,
        Rectangle Caption,
        Rectangle StatusIcon,
        Rectangle StatusText,
        Rectangle StatusSub,
        Rectangle Progress = default);

    // promptLines, captionLines and subLines: how many lines the prompt (14 px), the caption (12 px) and the
    // status sub-line (12 px) wrap to at this width, measured by the caller since this layout draws nothing.
    public static SetupLayout Setup(SetupViewModel view, int dpi, int promptLines = 1, int captionLines = 1, int subLines = 1)
    {
        ArgumentNullException.ThrowIfNull(view);

        int side = CardPlacement.Scale(BodySideAt96, dpi);
        int itemGap = CardPlacement.Scale(BodyItemGapAt96, dpi);
        int promptLine = CardPlacement.Scale(PromptLineAt96, dpi);
        int captionLine = CardPlacement.Scale(CaptionLineAt96, dpi);
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
            int textHeight = promptLine + (view.StatusSub is null ? 0 : captionLine * Math.Max(1, subLines));
            int rowHeight = Math.Max(iconSize, textHeight);
            int top = Place(rowHeight);
            statusIcon = new Rectangle(side, top + ((rowHeight - iconSize) / 2), iconSize, iconSize);
            int textX = statusIcon.Right + iconGap;
            int textTop = top + ((rowHeight - textHeight) / 2);
            statusText = new Rectangle(textX, textTop, width - side - textX, promptLine);
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

        int bodyHeight = y + CardPlacement.Scale(BodyBottomAt96, dpi);
        SubPageFrame.FrameLayout frame = SubPageFrame.Compute(dpi, bodyHeight, view.Buttons.Count);
        int offset = frame.Body.Y;
        return new SetupLayout(
            frame, Shift(prompt, offset), Shift(caption, offset), Shift(statusIcon, offset), Shift(statusText, offset), Shift(statusSub, offset),
            Shift(progress, offset));
    }

    private static Rectangle Shift(Rectangle rect, int dy) => rect.IsEmpty ? rect : new Rectangle(rect.X, rect.Y + dy, rect.Width, rect.Height);
}
