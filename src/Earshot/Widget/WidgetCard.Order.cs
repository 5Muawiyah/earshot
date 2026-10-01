using System.Drawing.Drawing2D;
using Earshot.Popup;

namespace Earshot.Widget;

// The pictures of the six gauge orders on the settings page. Each is the gauge itself, drawn by the same renderer as
// the taskbar's, at the card's scale: choosing one changes the gauge on the taskbar at once, which is the preview.
internal sealed partial class WidgetCard
{
    private void DrawOrderTiles(Graphics g, SettingsItem item, CardSettingsValues values, CardColours colours, bool focusVisible, SettingsTarget focus)
    {
        GaugeContent content = values.GaugePreview
            ?? GaugeContent.From(WidgetSnapshot.Empty(WidgetWatcherState.NotStarted), DateTimeOffset.UtcNow, GaugeDisplaySettings.Default);
        GaugePalette palette = GaugePalette.Create(!_dark, colours.Accent, colours.HighContrast, colours.Text);
        string family = TypeRamp.FamilyFor(TypeRole.Gauge);
        int radius = CardPlacement.Scale(FocusVisual.ControlRadiusAt96, _dpi);
        int border = Math.Max(2, CardPlacement.Scale(2, _dpi));
        GaugeOrder chosen = GaugeOrders.FromStored(values.GaugeOrder);

        for (int i = 0; i < item.Tiles.Count; i++)
        {
            Rectangle tile = item.Tiles[i];
            var order = (GaugeOrder)i;
            bool selected = order == chosen;

            using (GraphicsPath box = CardPaint.RoundedRectangle(new RectangleF(tile.X + 0.5f, tile.Y + 0.5f, tile.Width - 1, tile.Height - 1), radius))
            {
                using var fill = new SolidBrush(colours.ControlFill);
                g.FillPath(fill, box);
                using var pen = new Pen(selected ? colours.Accent : colours.ControlStroke, 1f);
                g.DrawPath(pen, box);
            }

            GaugeLayout layout = GaugeLayout.For(_dpi, order);
            using (Bitmap picture = GaugeRenderer.RenderPreview(content, palette, layout, family, colours.TextTertiary))
            {
                g.DrawImageUnscaled(picture, tile.X + ((tile.Width - picture.Width) / 2), tile.Y + ((tile.Height - picture.Height) / 2));
            }

            if (selected)
            {
                // A 2 px accent border inside the tile.
                float half = border / 2f;
                using GraphicsPath inner = CardPaint.RoundedRectangle(
                    new RectangleF(tile.X + half, tile.Y + half, tile.Width - border, tile.Height - border), Math.Max(0, radius - (half / 2f)));
                using var pen = new Pen(colours.Accent, border);
                g.DrawPath(pen, inner);
            }

            if (focusVisible && focus.SameStop(new SettingsTarget(item.Row, SettingsPart.Tile)) && focus.Index == i)
            {
                CardPaint.Focus(g, tile, radius, colours, _dpi);
            }
        }
    }
}
