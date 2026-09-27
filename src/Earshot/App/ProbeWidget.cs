using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.App;
using Earshot.Widget;

namespace Earshot;

// probe widget --out <folder>: renders the taskbar gauge from two fixed synthetic snapshots (on this PC
// with a charging left bud, and elsewhere) at three DPIs and two ink colours, and writes each as a PNG.
// No IWidgetStatus, no device, no window shown: GaugeRenderer draws straight to a bitmap, the same
// GDI+ path GaugeWindow pushes through UpdateLayeredWindow. Safe under EARSHOT_SAFE_MODE=1 with
// EARSHOT_DATA_ROOT pointed at a temp folder, since it touches neither.
//
// The dedicated card (three columns, the case-open notice) the design describes is not built in this
// pass (see the report): TrayContext.OnWidgetCardRequested reuses the existing ConnectCard/CardPresenter
// infrastructure instead. This probe therefore covers the gauge only.
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
