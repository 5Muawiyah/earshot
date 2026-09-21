using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Popup;
using Earshot.Tray;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

// Renders the card pictures used in docs\overview.md from ConnectCard itself, one per status line.
// The test always renders and checks the card. It writes the files only when
// EARSHOT_DOC_IMAGE_DIR names a folder, so an ordinary test run changes nothing on disk:
//
//   $env:EARSHOT_DOC_IMAGE_DIR = "docs\images"
//   dotnet test --filter FullyQualifiedName~DocumentationCardImageTests
[TestClass]
public sealed class DocumentationCardImageTests
{
    internal const string DeviceName = "AirPods Pro";
    private const int WideEnough = 1896;

    // Twice the base 96, so the text is crisp when the page shows the picture at half size.
    private const int Dpi = 192;

    [TestMethod]
    [DataRow("connect-card.png", TrayStatus.CardConnected)]
    [DataRow("boot-block.png", TrayStatus.CardBlockedAtBoot)]
    [DataRow("audio-protection.png", TrayStatus.MicrophoneNotice)]
    public void TheDocumentationPictureIsTheRealCardWithADeviceName(string fileName, string status)
    {
        CardSta.Run(() =>
        {
            using var card = new ConnectCard(new CapturingLog());
            var content = new CardContent(DeviceName, status);
            Size size = card.Prepare(content, Dpi, CardTheme.Dark, WideEnough);

            using var rendered = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
            card.DrawToBitmap(rendered, new Rectangle(Point.Empty, size));

            CollectionAssert.AreEqual(
                new[] { DeviceName, status },
                card.LastPaintedText().ToArray(),
                "The picture shows the name and the status line, and nothing else.");

            string? folder = Environment.GetEnvironmentVariable("EARSHOT_DOC_IMAGE_DIR");
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            Assert.IsTrue(Directory.Exists(folder), "EARSHOT_DOC_IMAGE_DIR names a folder that is not there: " + folder);
            using Bitmap picture = OnABackdrop(rendered);
            picture.Save(Path.Combine(folder, fileName), ImageFormat.Png);
        });
    }

    // The 900 by 560 frame the documentation pictures use, with the card centred at its rendered
    // size. Windows rounds the card's corners on screen; DrawToBitmap does not, so they are
    // clipped here to match.
    private static Bitmap OnABackdrop(Bitmap card)
    {
        var picture = new Bitmap(900, 560, PixelFormat.Format24bppRgb);
        using Graphics graphics = Graphics.FromImage(picture);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.FromArgb(27, 29, 33));

        var where = new Rectangle((picture.Width - card.Width) / 2, (picture.Height - card.Height) / 2, card.Width, card.Height);
        using GraphicsPath corners = Rounded(where, 16);
        graphics.SetClip(corners);
        graphics.DrawImageUnscaled(card, where.Location);
        graphics.ResetClip();
        using var edge = new Pen(Color.FromArgb(70, 74, 82), 2f);
        graphics.DrawPath(edge, corners);
        return picture;
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
