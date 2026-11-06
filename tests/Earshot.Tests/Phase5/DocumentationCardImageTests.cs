using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Tray;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase5;

// Renders the card pictures used in docs\overview.md from the compact card itself, one per status line.
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
            using WidgetCard card = Earshot.Tests.Widget.DesignPixels.NewCard(Earshot.Tests.Widget.DesignTheme.Dark, 1.0, notice: true);
            CompactRequest request = CompactNoticePresenter.RequestFor(new CardContent(DeviceName, status), CardAnchor.NearTray, null, DeviceName);
            card.Render(Earshot.Tests.Widget.CardKit.MainModel() with { Form = CardForm.Compact, Compact = request.Content }, Dpi);
            Assert.IsTrue(card.IsCompactShown);
            Assert.AreEqual(status, request.Content.Text, "The picture shows the status line and no device name.");
            using Bitmap rendered = Earshot.Tests.Widget.CardKit.Render(card);

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

    // The widget card in the light and dark themes, and the taskbar gauge, as docs\overview.md shows them: the real card drawn from a
    // made-up reading (left 70 and charging, right 60, case 90, read five seconds ago, on this PC) at 150% display scale, and the real
    // gauge at the same scale on a transparent background. Written only when EARSHOT_DOC_IMAGE_DIR names a folder.
    [TestMethod]
    [DataRow("widget-card-light.png", false)]
    [DataRow("widget-card-dark.png", true)]
    public void TheWidgetCardPictureIsTheRealCardInTheTheme(string fileName, bool dark)
    {
        CardSta.Run(() =>
        {
            DateTimeOffset now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset at = now - TimeSpan.FromSeconds(5);
            WidgetSnapshot snapshot = Earshot.Tests.Widget.CardKit.Snapshot(new PartReading(70, true, null) { ReadAt = at }, new PartReading(60, false, null) { ReadAt = at }, at) with
            {
                Case = new PartReading(90, false, null) { ReadAt = at },
                Where = AirPodsWhere.ThisPc,
            };
            using WidgetCard card = Earshot.Tests.Widget.DesignPixels.NewCard(dark ? Earshot.Tests.Widget.DesignTheme.Dark : Earshot.Tests.Widget.DesignTheme.Light, 1.0);
            card.OverrideBackgroundForCaptureOnly = dark ? Program.DarkThemeBackdrop : Program.LightThemeBackdrop;
            card.Render(Earshot.Tests.Widget.CardKit.MainModel(snapshot) with { Now = now }, 144);
            using Bitmap rendered = Earshot.Tests.Widget.CardKit.Render(card);
            Assert.IsGreaterThan(0, rendered.Width);

            string? folder = Environment.GetEnvironmentVariable("EARSHOT_DOC_IMAGE_DIR");
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            Assert.IsTrue(Directory.Exists(folder), "EARSHOT_DOC_IMAGE_DIR names a folder that is not there: " + folder);
            rendered.Save(Path.Combine(folder, fileName), ImageFormat.Png);
        });
    }

    [TestMethod]
    public void TheWidgetGaugePictureIsTheRealGaugeCharging()
    {
        DateTimeOffset now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        WidgetSnapshot snapshot = Program.ProbeWidgetDesignGaugeStates(now)[1].Snapshot;
        GaugeContent content = GaugeContent.From(snapshot, now, GaugeDisplaySettings.Default);
        GaugePalette palette = GaugePalette.Create(lightTheme: true, UiSettingsColourSource.DefaultShade(AccentShade.Dark1), highContrast: false, Color.Black);
        using Bitmap gauge = GaugeRenderer.Render(content, palette, GaugeLayout.For(144, GaugeOrder.RingNumberBolt), hover: false, "Segoe UI");
        Assert.AreEqual(new Size(111, 60), gauge.Size);

        string? folder = Environment.GetEnvironmentVariable("EARSHOT_DOC_IMAGE_DIR");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Assert.IsTrue(Directory.Exists(folder), "EARSHOT_DOC_IMAGE_DIR names a folder that is not there: " + folder);
        gauge.Save(Path.Combine(folder, "widget-gauge.png"), ImageFormat.Png);
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
