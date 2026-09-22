using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Earshot.Icons;

namespace Earshot.Tests.TestWindow;

// Draws the first-draft set of how-to pictures as simple, clean, light-and-dark-neutral schematics
// (never a real screen capture: this repository is public, a capture would show the owner's other
// apps and his AirPods' own name, and Windows will not let its permission box be captured at all).
// Ink is a single mid-grey so the drawing reads on a light or a dark surface alike; the taskbar
// icon uses Earshot's own real glyph (EarbudGlyph, from src\Earshot) so it looks right. Every word
// drawn into a picture also appears in its how-to block's own steps (wording.json), never only
// here. Re-run PictureGeneratorTests.RegeneratesTheCheckedInPngFiles (with
// EARSHOT_REGENERATE_PICTURES=1 set) to refresh the checked-in files after changing anything below.
internal static class HowToPictureGenerator
{
    private static readonly Color Ink = Color.FromArgb(255, 90, 90, 90);
    private static readonly Color Highlight = Color.FromArgb(255, 30, 120, 220);

    internal static readonly IReadOnlyDictionary<string, Func<byte[]>> Generators = new Dictionary<string, Func<byte[]>>(StringComparer.Ordinal)
    {
        ["earshot-icon-taskbar"] = EarshotIconTaskbar,
        ["earshot-icon-menu"] = EarshotIconMenu,
        ["start-power-shutdown"] = StartPowerShutdown,
        ["start-power-sleep"] = StartPowerSleep,
        ["bluetooth-connect"] = BluetoothConnect,
        ["airpods-playing"] = AirPodsPlaying,
        ["permission-box"] = PermissionBox,
    };

    private static byte[] EarshotIconTaskbar()
    {
        const int width = 400, height = 140;
        using var bitmap = new Bitmap(width, height);
        using var g = BeginDraw(bitmap);

        // The taskbar strip along the bottom.
        var taskbar = new Rectangle(0, height - 48, width, 48);
        using var taskbarPen = new Pen(Ink, 2);
        g.DrawRectangle(taskbarPen, taskbar);

        // The "show hidden icons" arrow.
        DrawCaret(g, 60, height - 24);

        // Earshot's own real glyph, drawn at 32 px, ringed to show where to click.
        using Bitmap glyph = EarbudGlyph.ToBitmap(EarbudGlyph.Coverage(32, GlyphState.Disconnected), 32, Ink);
        int glyphX = 130, glyphY = height - 40;
        g.DrawImage(glyph, glyphX, glyphY, 32, 32);
        using var ringPen = new Pen(Highlight, 3);
        g.DrawEllipse(ringPen, glyphX - 8, glyphY - 8, 48, 48);

        // The clock.
        DrawClock(g, 340, height - 24, 16);

        return ToPngBytes(bitmap);
    }

    private static byte[] EarshotIconMenu()
    {
        const int width = 300, height = 220;
        using var bitmap = new Bitmap(width, height);
        using var g = BeginDraw(bitmap);

        using Bitmap glyph = EarbudGlyph.ToBitmap(EarbudGlyph.Coverage(32, GlyphState.Disconnected), 32, Ink);
        int glyphX = width - 60, glyphY = height - 40;
        g.DrawImage(glyph, glyphX, glyphY, 32, 32);

        // The menu opening above the icon: an outlined panel with a few blank rows (no baked-in
        // item text, so nothing here can drift out of step with the real menu).
        var menu = new Rectangle(40, 20, 220, 140);
        using var pen = new Pen(Ink, 2);
        g.DrawRectangle(pen, menu);
        for (int row = 0; row < 5; row++)
        {
            int y = menu.Top + 16 + (row * 24);
            g.DrawLine(pen, menu.Left + 16, y, menu.Right - 16, y);
        }

        return ToPngBytes(bitmap);
    }

    private static byte[] StartPowerShutdown()
    {
        const int width = 420, height = 160;
        using var bitmap = new Bitmap(width, height);
        using var g = BeginDraw(bitmap);
        using var font = new Font(FontFamily.GenericSansSerif, 14f);
        using var pen = new Pen(Ink, 2);

        DrawLabelledStep(g, pen, font, 20, "Start");
        DrawArrow(g, pen, 110, 40);
        DrawLabelledStep(g, pen, font, 150, "Power");
        DrawArrow(g, pen, 240, 40);
        DrawLabelledStep(g, pen, font, 280, "Shut down");

        // Ring "Shut down".
        using var ringPen = new Pen(Highlight, 3);
        g.DrawRectangle(ringPen, 272, 12, 116, 56);

        // "Restart" shown crossed out beneath, so the one to avoid is unmistakable.
        using var restartFont = new Font(FontFamily.GenericSansSerif, 13f);
        var restartBounds = new Rectangle(272, 100, 116, 36);
        g.DrawRectangle(pen, restartBounds);
        g.DrawString("Restart", restartFont, new SolidBrush(Ink), restartBounds, CentreFormat);
        using var crossPen = new Pen(Color.FromArgb(255, 200, 40, 40), 3);
        g.DrawLine(crossPen, restartBounds.Left, restartBounds.Top, restartBounds.Right, restartBounds.Bottom);
        g.DrawLine(crossPen, restartBounds.Right, restartBounds.Top, restartBounds.Left, restartBounds.Bottom);

        return ToPngBytes(bitmap);
    }

    // Start, Power, Sleep: the same three-step shape as StartPowerShutdown, but with nothing to
    // avoid, so only the last step is ringed.
    private static byte[] StartPowerSleep()
    {
        const int width = 420, height = 100;
        using var bitmap = new Bitmap(width, height);
        using var g = BeginDraw(bitmap);
        using var font = new Font(FontFamily.GenericSansSerif, 14f);
        using var pen = new Pen(Ink, 2);

        DrawLabelledStep(g, pen, font, 20, "Start");
        DrawArrow(g, pen, 110, 40);
        DrawLabelledStep(g, pen, font, 150, "Power");
        DrawArrow(g, pen, 240, 40);
        DrawLabelledStep(g, pen, font, 280, "Sleep");

        // Ring "Sleep".
        using var ringPen = new Pen(Highlight, 3);
        g.DrawRectangle(ringPen, 272, 12, 116, 56);

        return ToPngBytes(bitmap);
    }

    private static byte[] BluetoothConnect()
    {
        const int width = 420, height = 160;
        using var bitmap = new Bitmap(width, height);
        using var g = BeginDraw(bitmap);
        using var pen = new Pen(Ink, 2);
        using var font = new Font(FontFamily.GenericSansSerif, 13f);

        var panel = new Rectangle(16, 16, width - 32, height - 32);
        g.DrawRectangle(pen, panel);
        g.DrawString("Bluetooth and devices", font, new SolidBrush(Ink), panel.Left + 12, panel.Top + 10);
        g.DrawLine(pen, panel.Left, panel.Top + 40, panel.Right, panel.Top + 40);

        g.DrawString("AirPods", font, new SolidBrush(Ink), panel.Left + 12, panel.Top + 56);

        var connectButton = new Rectangle(panel.Right - 120, panel.Top + 48, 96, 32);
        using var ringPen = new Pen(Highlight, 3);
        g.DrawRectangle(ringPen, connectButton);
        g.DrawString("Connect", font, new SolidBrush(Ink), connectButton, CentreFormat);

        return ToPngBytes(bitmap);
    }

    private static byte[] AirPodsPlaying()
    {
        const int width = 260, height = 200;
        using var bitmap = new Bitmap(width, height);
        using var g = BeginDraw(bitmap);
        using var pen = new Pen(Ink, 3);

        // A simple phone outline with a musical note, and one earbud beside it.
        var phone = new Rectangle(30, 30, 90, 150);
        g.DrawRectangle(pen, phone);
        g.DrawEllipse(pen, phone.Left + phone.Width / 2 - 4, phone.Bottom - 20, 8, 8);
        DrawNote(g, pen, phone.Left + 25, phone.Top + 55);

        using Bitmap glyph = EarbudGlyph.ToBitmap(EarbudGlyph.Coverage(64, GlyphState.Connected), 64, Ink);
        g.DrawImage(glyph, 150, 70, 64, 64);

        return ToPngBytes(bitmap);
    }

    private static byte[] PermissionBox()
    {
        const int width = 380, height = 180;
        using var bitmap = new Bitmap(width, height);
        using var g = BeginDraw(bitmap);
        using var pen = new Pen(Ink, 2);
        using var font = new Font(FontFamily.GenericSansSerif, 12f);

        var box = new Rectangle(16, 16, width - 32, height - 32);
        g.DrawRectangle(pen, box);

        // The shield: two arcs forming a simple shield outline, never Windows' own artwork.
        var shield = new Rectangle(box.Left + 16, box.Top + 16, 40, 48);
        g.DrawArc(pen, shield, 180, 180);
        g.DrawLine(pen, shield.Left, shield.Top + shield.Height / 2, shield.Left, shield.Bottom - 8);
        g.DrawLine(pen, shield.Right, shield.Top + shield.Height / 2, shield.Right, shield.Bottom - 8);
        g.DrawLine(pen, shield.Left, shield.Bottom - 8, shield.Left + shield.Width / 2, shield.Bottom);
        g.DrawLine(pen, shield.Right, shield.Bottom - 8, shield.Left + shield.Width / 2, shield.Bottom);

        g.DrawString("Do you want to allow this app", font, new SolidBrush(Ink), box.Left + 72, box.Top + 16);
        g.DrawString("to make changes to your device?", font, new SolidBrush(Ink), box.Left + 72, box.Top + 36);

        var yes = new Rectangle(box.Right - 180, box.Bottom - 44, 72, 30);
        var no = new Rectangle(box.Right - 96, box.Bottom - 44, 72, 30);
        using var ringPen = new Pen(Highlight, 3);
        g.DrawRectangle(ringPen, yes);
        g.DrawRectangle(pen, no);
        g.DrawString("Yes", font, new SolidBrush(Ink), yes, CentreFormat);
        g.DrawString("No", font, new SolidBrush(Ink), no, CentreFormat);

        return ToPngBytes(bitmap);
    }

    private static readonly StringFormat CentreFormat = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    private static Graphics BeginDraw(Bitmap bitmap)
    {
        Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        return g;
    }

    private static void DrawLabelledStep(Graphics g, Pen pen, Font font, int left, string label)
    {
        var box = new Rectangle(left, 12, 100, 56);
        g.DrawRectangle(pen, box);
        g.DrawString(label, font, new SolidBrush(Ink), box, CentreFormat);
    }

    private static void DrawArrow(Graphics g, Pen pen, int x, int y)
    {
        g.DrawLine(pen, x, y, x + 40, y);
        g.DrawLine(pen, x + 32, y - 6, x + 40, y);
        g.DrawLine(pen, x + 32, y + 6, x + 40, y);
    }

    private static void DrawCaret(Graphics g, int x, int y)
    {
        using var pen = new Pen(Ink, 2);
        g.DrawLine(pen, x - 6, y + 4, x, y - 4);
        g.DrawLine(pen, x, y - 4, x + 6, y + 4);
    }

    private static void DrawClock(Graphics g, int x, int y, int radius)
    {
        using var pen = new Pen(Ink, 2);
        g.DrawEllipse(pen, x - radius, y - radius, radius * 2, radius * 2);
        g.DrawLine(pen, x, y, x, y - radius + 3);
        g.DrawLine(pen, x, y, x + radius - 5, y);
    }

    private static void DrawNote(Graphics g, Pen pen, int x, int y)
    {
        g.DrawLine(pen, x, y, x, y + 30);
        g.DrawEllipse(pen, x - 6, y + 24, 10, 8);
        g.DrawLine(pen, x, y, x + 10, y - 3);
        g.DrawLine(pen, x + 10, y - 3, x + 10, y + 10);
    }

    private static byte[] ToPngBytes(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
