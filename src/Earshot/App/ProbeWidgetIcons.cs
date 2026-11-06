using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.App;
using Earshot.Icons;
using Earshot.Infra;
using Earshot.Tray;
using Earshot.Widget;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot;

// probe widget --out <folder> --set icons: the pictures of Earshot's icons, one file for each kind of thing, in light, dark and
// high contrast at 100%, 125%, 150% and 200% display scale. Like the design set it draws off screen, from made-up snapshots, into
// bitmaps: no window is created or shown and Control.DrawToBitmap is never called.
//
//   icons-catalogue-<theme>-<scale>.png  every catalogue icon, line then filled, at the boxes the card, gauge and menu use
//   icons-tray-<theme>-<scale>.png       the tray's four states: disconnected, connected, busy, blocked
//   icons-gauge-<theme>-<scale>.png      the gauge modes, five to a row, in the order of the design set
//   icons-card-<theme>-<scale>.png       the card, with the marks of the left bud, the right bud and the case
//   icons-menu-<theme>-<scale>.png       a sample of the tray menu: each command's icon beside its name
internal static partial class Program
{
    internal const string ProbeWidgetIconsSet = "icons";

    internal static readonly IReadOnlyList<uint> ProbeWidgetIconsDpis = [96, 120, 144, 192];

    // The boxes at 100% the card and menu draw icons in: the 10 (close, minus, plus), 12 (chevrons, bolt, tick), 14 (gauge marks)
    // and 16 pixel classes with the set's art area filling the box (rule A), then the 20 pixel class with its margin (rule B).
    private static readonly IReadOnlyList<(int Px, IconRule Rule)> IconSheetBoxes =
    [
        (10, IconRule.A), (12, IconRule.A), (14, IconRule.A), (16, IconRule.A), (20, IconRule.B),
    ];

    // The menu sample: a command, its icon and whether it is drawn filled (checked, or the Disconnect command).
    private static readonly IReadOnlyList<(string Text, Icon Icon, IconStyle Style)> IconMenuSample =
    [
        ("Connect", Icon.AirPods, IconStyle.Line),
        ("Disconnect", Icon.AirPods, IconStyle.Filled),
        ("Play from a phone", Icon.PlayCircle, IconStyle.Line),
        ("Refresh battery", Icon.Refresh, IconStyle.Line),
        ("Send to", Icon.Send, IconStyle.Line),
        ("Block at boot", Icon.Lock, IconStyle.Filled),
        ("Protect audio quality", Icon.Headphone, IconStyle.Line),
        ("Open on startup", Icon.Rocket, IconStyle.Filled),
        ("Show on taskbar", Icon.LayoutBottom, IconStyle.Line),
        ("Low battery alerts", Icon.Notification, IconStyle.Line),
        ("Set up Earshot", Icon.Settings, IconStyle.Line),
        ("Fix connection", Icon.Bluetooth, IconStyle.Line),
        ("Pair AirPods", Icon.BluetoothSearching, IconStyle.Line),
        ("Check for updates", Icon.Download, IconStyle.Line),
        ("Copy diagnostics", Icon.Copy, IconStyle.Line),
        ("Exit", Icon.Exit, IconStyle.Line),
    ];

    internal static int ProbeWidgetIconsScaled(int px, uint dpi) =>
        (int)Math.Round(px * dpi / 96.0, MidpointRounding.AwayFromZero);

    internal static IReadOnlyList<ProbeWidgetFile> RenderProbeWidgetIconsSet(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var files = new List<ProbeWidgetFile>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string fontFamily = TypeRamp.FamilyFor(TypeRole.Gauge);
        var log = new FileLog(Paths.Current.LogFolder);

        var gauges = new List<(GaugeContent Content, GaugeOrder Order)>();
        foreach ((_, WidgetSnapshot snapshot) in ProbeWidgetDesignGaugeStates(now))
        {
            gauges.Add((GaugeContent.From(snapshot, now, GaugeDisplaySettings.Default), GaugeOrder.RingNumberBolt));
        }

        foreach ((_, GaugeContent content) in ProbeWidgetDesignGaugeContents())
        {
            gauges.Add((content, GaugeOrder.RingNumberBolt));
        }

        WidgetCardModel cardModel = ProbeWidgetCardVariants(now).Single(v => v.Variant == "this-pc").Model;

        foreach ((string theme, Color ink, bool highContrast) in ProbeWidgetDesignThemes)
        {
            foreach (uint dpi in ProbeWidgetIconsDpis)
            {
                string suffix = "-" + theme + "-" + ProbeWidgetScaleName(dpi) + ".png";
                files.Add(WriteIconPicture("icons-catalogue", theme, dpi, Path.Combine(folder, "icons-catalogue" + suffix),
                    () => DrawIconCatalogue(dpi, ink, highContrast)));
                files.Add(WriteIconPicture("icons-tray", theme, dpi, Path.Combine(folder, "icons-tray" + suffix),
                    () => DrawIconTray(dpi, ink, highContrast)));
                files.Add(WriteIconPicture("icons-gauge", theme, dpi, Path.Combine(folder, "icons-gauge" + suffix),
                    () => DrawIconGauges(gauges, dpi, ink, highContrast, fontFamily)));
                files.Add(RenderDesignCard(log, "icons-card", cardModel, 1.0, false, false, false, false, dpi, theme, ink, highContrast,
                    Path.Combine(folder, "icons-card" + suffix)));
                files.Add(WriteIconPicture("icons-menu", theme, dpi, Path.Combine(folder, "icons-menu" + suffix),
                    () => DrawIconMenu(dpi, ink, highContrast)));
            }
        }

        return files;
    }

    private static ProbeWidgetFile WriteIconPicture(string name, string theme, uint dpi, string path, Func<Bitmap> draw)
    {
        try
        {
            using Bitmap bitmap = draw();
            bitmap.Save(path, ImageFormat.Png);
            return new ProbeWidgetFile(name, dpi, theme, path, (int)new FileInfo(path).Length, null);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new ProbeWidgetFile(name, dpi, theme, path, 0, ex.Message);
        }
    }

    // A flat background for the theme: white or the dark taskbar sample, and the window colour in high contrast.
    private static Color IconSheetBackground(Color ink, bool highContrast) =>
        highContrast ? SystemColors.Window : ink.GetBrightness() >= 0.5f ? DarkTaskbarSample : Color.White;

    private static Bitmap DrawIconCatalogue(uint dpi, Color ink, bool highContrast)
    {
        int pad = ProbeWidgetIconsScaled(6, dpi);
        int cell = ProbeWidgetIconsScaled(20, dpi) + (2 * pad);
        int labelWidth = ProbeWidgetIconsScaled(120, dpi);
        const int styles = 2;
        int columns = IconSheetBoxes.Count * styles;
        IReadOnlyList<Icon> icons = IconCatalogue.All;
        int header = cell;
        int width = labelWidth + (columns * cell) + pad;
        int height = header + (icons.Count * cell) + pad;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(IconSheetBackground(ink, highContrast));
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", 9f * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(ink);
        using var rule = new Pen(Color.FromArgb(60, ink));

        for (int style = 0; style < styles; style++)
        {
            for (int b = 0; b < IconSheetBoxes.Count; b++)
            {
                int x = labelWidth + (((style * IconSheetBoxes.Count) + b) * cell);
                int box = ProbeWidgetIconsScaled(IconSheetBoxes[b].Px, dpi);
                g.DrawString((style == 0 ? "L" : "F") + box.ToString(CultureInfo.InvariantCulture), font, brush, x + pad, pad);
            }
        }

        for (int row = 0; row < icons.Count; row++)
        {
            int y = header + (row * cell);
            g.DrawLine(rule, 0, y, width, y);
            g.DrawString(icons[row].ToString(), font, brush, pad, y + pad);
            for (int style = 0; style < styles; style++)
            {
                for (int b = 0; b < IconSheetBoxes.Count; b++)
                {
                    var bounds = new Rectangle(labelWidth + (((style * IconSheetBoxes.Count) + b) * cell), y, cell, cell);
                    IconPainter.Draw(g, icons[row], style == 0 ? IconStyle.Line : IconStyle.Filled, bounds,
                        ProbeWidgetIconsScaled(IconSheetBoxes[b].Px, dpi), ink, IconSheetBoxes[b].Rule);
                }
            }
        }

        return bitmap;
    }

    // The tray's four states at the small icon size of the display, on a flat taskbar sample.
    private static Bitmap DrawIconTray(uint dpi, Color ink, bool highContrast)
    {
        int px = TrayMenuScale.IconPx(dpi);
        int pad = px / 2;
        GlyphState[] states = [GlyphState.Disconnected, GlyphState.Connected, GlyphState.Busy, GlyphState.Blocked];
        var bitmap = new Bitmap((states.Length * (px + pad)) + pad, px + (2 * pad), PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(highContrast ? SystemColors.Window : ink.GetBrightness() >= 0.5f ? DarkTaskbarSample : LightTaskbarSample);
        for (int i = 0; i < states.Length; i++)
        {
            using Bitmap glyph = TrayGlyph.ToBitmap(TrayGlyph.Coverage(px, states[i]), px, ink);
            g.DrawImageUnscaled(glyph, pad + (i * (px + pad)), pad);
        }

        return bitmap;
    }

    // Every gauge mode on a flat taskbar sample, five to a row.
    private static Bitmap DrawIconGauges(
        List<(GaugeContent Content, GaugeOrder Order)> gauges, uint dpi, Color ink, bool highContrast, string fontFamily)
    {
        bool light = !highContrast && ink.GetBrightness() < 0.5f;
        Color accent = UiSettingsColourSource.DefaultShade(light ? AccentShade.Dark1 : AccentShade.Light2);
        GaugePalette palette = GaugePalette.Create(light, accent, highContrast, ink);
        Size size = GaugeLayout.SizeFor((int)dpi);
        int pad = ProbeWidgetIconsScaled(8, dpi);
        const int perRow = 5;
        int rows = (gauges.Count + perRow - 1) / perRow;
        var bitmap = new Bitmap(
            (perRow * (size.Width + pad)) + pad, (rows * (size.Height + pad)) + pad, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(highContrast ? SystemColors.Window : light ? LightTaskbarSample : DarkTaskbarSample);
        g.CompositingMode = CompositingMode.SourceOver;
        for (int i = 0; i < gauges.Count; i++)
        {
            using Bitmap gauge = GaugeRenderer.Render(gauges[i].Content, palette, GaugeLayout.For((int)dpi, gauges[i].Order), hover: false, fontFamily);
            g.DrawImageUnscaled(gauge, pad + ((i % perRow) * (size.Width + pad)), pad + ((i / perRow) * (size.Height + pad)));
        }

        return bitmap;
    }

    // The tray menu's commands in a column, each with its icon at the menu's icon size (rule B) and its name.
    private static Bitmap DrawIconMenu(uint dpi, Color ink, bool highContrast)
    {
        int px = TrayMenuScale.IconPx(dpi);
        int pad = ProbeWidgetIconsScaled(8, dpi);
        int row = px + pad;
        int width = ProbeWidgetIconsScaled(260, dpi);
        var bitmap = new Bitmap(width, (IconMenuSample.Count * row) + pad, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(IconSheetBackground(ink, highContrast));
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var font = new Font("Segoe UI", 9f * dpi / 96f, FontStyle.Regular, GraphicsUnit.Point);
        using var brush = new SolidBrush(ink);
        for (int i = 0; i < IconMenuSample.Count; i++)
        {
            int y = (pad / 2) + (i * row);
            var iconBounds = new Rectangle(pad, y, px, row);
            IconPainter.Draw(g, IconMenuSample[i].Icon, IconMenuSample[i].Style, iconBounds, px, ink, IconRule.B);
            g.DrawString(IconMenuSample[i].Text, font, brush, pad + px + pad, y + ((row - font.Height) / 2f));
        }

        return bitmap;
    }
}
