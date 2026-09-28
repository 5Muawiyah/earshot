using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Widget;

namespace Earshot;

// probe widget --out <folder>: renders the taskbar gauge, the dedicated three-column card and the
// case-open notice card from fixed synthetic snapshots at three DPIs and two ink colours, and writes each
// as a PNG. No IWidgetStatus, no device, no window shown: GaugeRenderer draws straight to a bitmap, the
// same GDI+ path GaugeWindow pushes through UpdateLayeredWindow, and WidgetCard is drawn with
// DrawToBitmap on an unshown Form, the same pattern WidgetCardTests' own pure checks use (never Show() or
// Activate(), so nothing ever reaches the input desktop). Safe under EARSHOT_SAFE_MODE=1 with
// EARSHOT_DATA_ROOT pointed at a temp folder: the card's own log writes land under that redirected folder
// too, the same FileLog(Paths.Current.LogFolder) every other probe target already uses.
internal static partial class Program
{
    internal static readonly IReadOnlyList<uint> ProbeWidgetDpis = [96, 120, 144];

    internal static readonly IReadOnlyList<(string Name, Color Ink)> ProbeWidgetInks =
    [
        ("dark-taskbar-white-ink", Color.White),
        ("light-taskbar-black-ink", Color.Black),
    ];

    internal sealed record ProbeWidgetFile(string Snapshot, uint Dpi, string Ink, string Path, int Bytes, string? Problem);

    static partial void ProbeWidget(ProbeContext ctx, string? folder)
    {
        ctx.Handled = true;
        if (string.IsNullOrWhiteSpace(folder))
        {
            ctx.Out.WriteLine("probe widget needs --out <folder>.");
            ctx.ExitCode = ExitCodes.Usage;
            return;
        }

        string target;
        try
        {
            target = Path.GetFullPath(folder);
            Directory.CreateDirectory(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ctx.Out.WriteLine("The folder " + folder + " could not be made: " + ex.Message);
            ctx.ExitCode = ExitCodes.IoError;
            return;
        }

        IReadOnlyList<ProbeWidgetFile> files = RenderProbeWidgets(target);
        WriteProbeWidgets(ctx, target, files);
        ctx.ExitCode = files.Any(f => f.Bytes == 0) ? ExitCodes.IoError : ExitCodes.Ok;
    }

    // The two fixed synthetic snapshots the design asks for: on this PC with the left bud charging and
    // the right bud in the ear, read two minutes ago; and elsewhere.
    internal static IReadOnlyList<(string Name, WidgetSnapshot Snapshot)> ProbeWidgetSnapshots(DateTimeOffset now) =>
    [
        ("this-pc", WidgetSnapshot.Empty(WidgetWatcherState.Started, claimExists: true) with
        {
            Where = AirPodsWhere.ThisPc,
            Left = new PartReading(70, true, null) { ReadAt = now - TimeSpan.FromMinutes(2) },
            Right = new PartReading(60, false, true) { ReadAt = now - TimeSpan.FromMinutes(2) },
            Case = new PartReading(90, false, null) { ReadAt = now - TimeSpan.FromMinutes(2) },
            BatteryReadAt = now - TimeSpan.FromMinutes(2),
        }),
        ("elsewhere", WidgetSnapshot.Empty(WidgetWatcherState.Started, claimExists: true) with
        {
            Where = AirPodsWhere.Elsewhere,
            Left = new PartReading(70, true, null) { ReadAt = now - TimeSpan.FromMinutes(2) },
            Right = new PartReading(60, false, true) { ReadAt = now - TimeSpan.FromMinutes(2) },
            Case = new PartReading(90, false, null) { ReadAt = now - TimeSpan.FromMinutes(2) },
            BatteryReadAt = now - TimeSpan.FromMinutes(2),
        }),
    ];

    internal static IReadOnlyList<ProbeWidgetFile> RenderProbeWidgets(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var files = new List<ProbeWidgetFile>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string fontFamily = SystemFonts.MessageBoxFont?.Name ?? FontFamily.GenericSansSerif.Name;

        foreach ((string snapshotName, WidgetSnapshot snapshot) in ProbeWidgetSnapshots(now))
        {
            foreach (uint dpi in ProbeWidgetDpis)
            {
                foreach ((string inkName, Color ink) in ProbeWidgetInks)
                {
                    int width = GaugeRenderer.WidthFor((int)dpi);
                    int height = (int)Math.Round(48 * dpi / 96.0);
                    string name = string.Create(CultureInfo.InvariantCulture,
                        $"gauge-{snapshotName}-{dpi}dpi-{width}x{height}-{inkName}.png");
                    string path = Path.Combine(folder, name);
                    files.Add(RenderProbeWidget(snapshotName, snapshot, dpi, height, inkName, ink, fontFamily, path));
                }
            }
        }

        // The card's own log (SetTheme/OnHandleCreated warnings if DWM ever refuses a corner or backdrop
        // call) goes to the same FileLog every other probe target already writes through, under whatever
        // folder Paths.Current resolves to; EARSHOT_DATA_ROOT redirects it away from the real profile.
        var log = new FileLog(Paths.Current.LogFolder);
        IReadOnlyList<(string Variant, WidgetCardModel Model)> cardVariants = ProbeWidgetCardVariants(now);

        foreach ((string variant, WidgetCardModel model) in cardVariants)
        {
            foreach (uint dpi in ProbeWidgetDpis)
            {
                foreach ((string inkName, Color ink) in ProbeWidgetInks)
                {
                    files.Add(RenderProbeWidgetCard(log, "card-" + variant, variant, model, dpi, inkName, ink, notice: false, folder));
                }
            }
        }

        // WidgetCard always overrides the Where line to "Case open" for a notice-mode instance regardless
        // of what the model's own snapshot says, so which fixture backs it barely matters; the "this-pc"
        // card model (already built above) is reused rather than building a second one.
        (string noticeVariant, WidgetCardModel noticeModel) = cardVariants[0];
        foreach (uint dpi in ProbeWidgetDpis)
        {
            foreach ((string inkName, Color ink) in ProbeWidgetInks)
            {
                files.Add(RenderProbeWidgetCard(log, "case-open-card", noticeVariant, noticeModel, dpi, inkName, ink, notice: true, folder));
            }
        }

        return files;
    }

    private static ProbeWidgetFile RenderProbeWidget(
        string snapshotName, WidgetSnapshot snapshot, uint dpi, int height, string inkName, Color ink, string fontFamily, string path)
    {
        try
        {
            using Bitmap bitmap = GaugeRenderer.Render(snapshot, (int)dpi, height, ink, hover: false, fontFamily);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            var info = new FileInfo(path);
            return new ProbeWidgetFile(snapshotName, dpi, inkName, path, (int)info.Length, null);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new ProbeWidgetFile(snapshotName, dpi, inkName, path, 0, ex.Message);
        }
    }

    // The card variants the widget card probe renders, from the same two fixed synthetic snapshots the
    // gauge already uses. "this-pc" and "elsewhere" are the shipped card as production actually renders it
    // today (AutoPauseAvailable false in both, matching every real snapshot until a device proves the
    // in-ear bits, so ShowSwitch is false and the switch row never appears). "auto-pause-preview" is
    // clearly not that: production never sets AutoPauseAvailable true today, so this third variant exists
    // only to preview the switch row's look, from a snapshot no real device produces yet, not as a claim
    // about current shipped behaviour.
    internal static IReadOnlyList<(string Variant, WidgetCardModel Model)> ProbeWidgetCardVariants(DateTimeOffset now)
    {
        IReadOnlyList<(string Name, WidgetSnapshot Snapshot)> snapshots = ProbeWidgetSnapshots(now);
        WidgetSnapshot thisPc = snapshots[0].Snapshot;
        WidgetSnapshot elsewhere = snapshots[1].Snapshot;

        return
        [
            ("this-pc", new WidgetCardModel(thisPc, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: false,
                ButtonEnabled: true, OtherDeviceLabel: "", Now: now)),
            ("elsewhere", new WidgetCardModel(elsewhere, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: true,
                ButtonEnabled: true, OtherDeviceLabel: "iPhone", Now: now)),
            ("auto-pause-preview", new WidgetCardModel(thisPc with { AutoPauseAvailable = true },
                AutoPauseOn: false, ShowSwitch: true, ConnectIntent: false, ButtonEnabled: true,
                OtherDeviceLabel: "", Now: now)),
        ];
    }

    // Renders one WidgetCard(notice) to a bitmap with DrawToBitmap, exactly the pattern
    // WidgetCardTests' own private Render(WidgetCard) helper uses (never Show() or Activate()), and saves
    // it as a PNG named "{namePrefix}-{dpi}dpi-{w}x{h}-{ink}.png" where w x h is the card's own ClientSize
    // after Render, since the card's height comes from its content, not a fixed constant.
    private static ProbeWidgetFile RenderProbeWidgetCard(
        ILog log, string namePrefix, string variant, WidgetCardModel model, uint dpi, string inkName, Color ink, bool notice, string folder)
    {
        string fallbackPath = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture,
            $"{namePrefix}-{dpi}dpi-{inkName}.png"));
        try
        {
            using var card = new WidgetCard(log, notice);
            card.SetTheme(ink, highContrast: false);
            card.Render(model, (int)dpi);

            Size size = card.ClientSize;
            using var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
            card.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));

            string name = string.Create(CultureInfo.InvariantCulture,
                $"{namePrefix}-{dpi}dpi-{size.Width}x{size.Height}-{inkName}.png");
            string path = Path.Combine(folder, name);
            bitmap.Save(path, ImageFormat.Png);
            var info = new FileInfo(path);
            return new ProbeWidgetFile(variant, dpi, inkName, path, (int)info.Length, null);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ProbeWidgetFile(variant, dpi, inkName, fallbackPath, 0, ex.Message);
        }
    }

    private static void WriteProbeWidgets(ProbeContext ctx, string folder, IReadOnlyList<ProbeWidgetFile> files)
    {
        if (ctx.Json)
        {
            ctx.WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", ProbeWidgetTarget);
                w.WriteString("folder", folder);
                w.WriteStartArray("files");
                foreach (ProbeWidgetFile file in files)
                {
                    w.WriteStartObject();
                    w.WriteString("snapshot", file.Snapshot);
                    w.WriteNumber("dpi", file.Dpi);
                    w.WriteString("ink", file.Ink);
                    w.WriteString("file", file.Path);
                    w.WriteNumber("bytes", file.Bytes);
                    w.WriteString("problem", file.Problem);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            });
            return;
        }

        ctx.Out.WriteLine("Folder: " + folder);
        foreach (ProbeWidgetFile file in files)
        {
            ctx.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {file.Snapshot,-10} {file.Dpi} dpi  {file.Ink,-22}  {Path.GetFileName(file.Path)}") +
                (file.Problem is null ? "" : "  " + file.Problem));
        }
    }
}
