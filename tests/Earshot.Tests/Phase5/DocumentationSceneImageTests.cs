using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Icons;
using Earshot.Popup;
using Earshot.Tests.Phase1;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

// Renders the other two documentation pictures from the application's own code.
//
//   tray-menu.png  TrayMenu, built from a MenuState that MenuModel.Build made, shown on a private
//                  desktop so nothing appears on screen, and rendered with DrawToBitmap.
//   hero.png       ConnectCard and the tray glyph from EarbudGlyph, placed on a taskbar strip.
//
// Files are written only when EARSHOT_DOC_IMAGE_DIR names a folder.
[TestClass]
public sealed class DocumentationSceneImageTests
{
    private const int WideEnough = 1896;
    private static readonly Color Backdrop = Color.FromArgb(27, 29, 33);

    [TestMethod]
    public void TheMenuPictureShowsTheMenuOfASetUpPc()
    {
        Bitmap? rendered = null;
        string[] texts = [];
        try
        {
            CardDesktop.Run(() =>
            {
                MenuState state = MenuModel.Build(
                    Phase1Fixtures.Target(ConnectionState.Disconnected, name: DocumentationCardImageTests.DeviceName),
                    Phase1Fixtures.Block(BlockState.Blocked),
                    Phase1Fixtures.Protection(AudioProtectionState.Protected),
                    Phase1Fixtures.Settings(),
                    busy: false,
                    StartupState.On);

                using var menu = new TrayMenu(() => state);
                ContextMenuStrip strip = menu.Strip;
                strip.Show(new Point(0, 0));
                CardSta.PumpUntil(() => strip.Visible && strip.Width > 0, TimeSpan.FromSeconds(10));
                Assert.IsTrue(strip.Visible, "The menu never opened on the private desktop.");

                texts = menu.Items.Where(i => i.Available && i is not ToolStripSeparator).Select(i => i.Text ?? string.Empty).ToArray();
                rendered = new Bitmap(strip.Width, strip.Height, PixelFormat.Format24bppRgb);
                strip.DrawToBitmap(rendered, new Rectangle(Point.Empty, strip.Size));
                strip.Close();
            });

            Assert.IsNotNull(rendered);
            CollectionAssert.Contains(texts, "Connect", "A disconnected device's menu leads with Connect.");
            CollectionAssert.Contains(texts, "Block at boot");
            CollectionAssert.Contains(texts, "Exit");
            Assert.IsFalse(texts.Any(t => t.Contains(Phase1Fixtures.AirPodsName, StringComparison.Ordinal)), "The fixture's own device name must not appear.");
            Assert.IsTrue(HasMoreThanOneColour(rendered), "The menu rendered as one flat colour, so nothing was rendered.");

            Save("tray-menu.png", () => OnABackdrop(rendered, scale: 2));
        }
        finally
        {
            rendered?.Dispose();
        }
    }

    [TestMethod]
    public void TheHeroPictureShowsTheIconAndTheCard()
    {
        CardSta.Run(() =>
        {
            using var card = new ConnectCard(new CapturingLog());
            Size size = card.Prepare(new CardContent(DocumentationCardImageTests.DeviceName, TrayStatus.CardConnected), 192, CardTheme.Dark, WideEnough);
            using var cardBitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
            card.DrawToBitmap(cardBitmap, new Rectangle(Point.Empty, size));
            CollectionAssert.AreEqual(
                new[] { DocumentationCardImageTests.DeviceName, TrayStatus.CardConnected },
                card.LastPaintedText().ToArray());

            const int GlyphPx = 48;
            byte[] coverage = EarbudGlyph.Coverage(GlyphPx, GlyphState.Connected);
            Assert.IsTrue(coverage.Any(a => a != 0), "The connected glyph drew nothing.");
            using Bitmap glyph = EarbudGlyph.ToBitmap(coverage, GlyphPx, Color.White);

            Save("hero.png", () => Hero(cardBitmap, glyph));
        });
    }

    // 1600 by 420: a taskbar strip along the bottom, the icon ringed beside the clock area, and the
    // card above the icon where the application puts it. The clock area carries no figures.
    private static Bitmap Hero(Bitmap card, Bitmap glyph)
    {
        var picture = new Bitmap(1600, 420, PixelFormat.Format24bppRgb);
        using Graphics g = Graphics.FromImage(picture);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Backdrop);

        const int BarHeight = 96;
        var bar = new Rectangle(0, picture.Height - BarHeight, picture.Width, BarHeight);
        using (var barBrush = new SolidBrush(Color.FromArgb(38, 41, 47)))
        {
            g.FillRectangle(barBrush, bar);
        }

        using (var line = new Pen(Color.FromArgb(58, 62, 70), 2f))
        {
            g.DrawLine(line, bar.Left, bar.Top, bar.Right, bar.Top);
        }

        // Clock and date as two plain bars.
        using (var quiet = new SolidBrush(Color.FromArgb(120, 126, 138)))
        {
            using GraphicsPath time = Rounded(new Rectangle(picture.Width - 190, bar.Top + 26, 120, 14), 7);
            using GraphicsPath date = Rounded(new Rectangle(picture.Width - 210, bar.Top + 54, 140, 14), 7);
            g.FillPath(quiet, time);
            g.FillPath(quiet, date);

            // Two neighbouring tray items, as plain dots.
            g.FillEllipse(quiet, picture.Width - 470, bar.Top + 36, 24, 24);
            g.FillEllipse(quiet, picture.Width - 410, bar.Top + 36, 24, 24);
        }

        var iconAt = new Point(picture.Width - 340, bar.Top + ((BarHeight - glyph.Height) / 2));
        g.DrawImageUnscaled(glyph, iconAt);
        using (var ring = new Pen(Color.FromArgb(96, 165, 250), 4f))
        {
            g.DrawEllipse(ring, iconAt.X - 14, iconAt.Y - 14, glyph.Width + 28, glyph.Height + 28);
        }

        var cardAt = new Rectangle(picture.Width - 60 - card.Width, bar.Top - 40 - card.Height, card.Width, card.Height);
        using GraphicsPath corners = Rounded(cardAt, 16);
        g.SetClip(corners);
        g.DrawImageUnscaled(card, cardAt.Location);
        g.ResetClip();
        using var edge = new Pen(Color.FromArgb(70, 74, 82), 2f);
        g.DrawPath(edge, corners);
        return picture;
    }

    // The 900 by 560 frame the other documentation pictures use, with the rendered window centred.
    private static Bitmap OnABackdrop(Bitmap window, int scale)
    {
        var picture = new Bitmap(900, 560, PixelFormat.Format24bppRgb);
        using Graphics g = Graphics.FromImage(picture);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(Backdrop);

        int width = window.Width * scale;
        int height = window.Height * scale;
        if (height > picture.Height - 40)
        {
            width = window.Width;
            height = window.Height;
        }

        g.DrawImage(window, new Rectangle((picture.Width - width) / 2, (picture.Height - height) / 2, width, height));
        return picture;
    }

    private static void Save(string fileName, Func<Bitmap> picture)
    {
        string? folder = Environment.GetEnvironmentVariable("EARSHOT_DOC_IMAGE_DIR");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Assert.IsTrue(Directory.Exists(folder), "EARSHOT_DOC_IMAGE_DIR names a folder that is not there: " + folder);
        using Bitmap made = picture();
        made.Save(Path.Combine(folder, fileName), ImageFormat.Png);
    }

    private static bool HasMoreThanOneColour(Bitmap bitmap)
    {
        Color first = bitmap.GetPixel(0, 0);
        for (int y = 0; y < bitmap.Height; y += 2)
        {
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                if (bitmap.GetPixel(x, y) != first)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
