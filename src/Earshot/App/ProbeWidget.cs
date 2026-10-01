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
// as a PNG. No IWidgetStatus, no device, no window ever shown: GaugeRenderer draws straight to a bitmap,
// the same GDI+ path GaugeWindow pushes through UpdateLayeredWindow, and WidgetCard is drawn with its own
// RenderContent(Graphics) straight into an off-screen bitmap's Graphics, with no Form handle, no
// Show()/Activate() and no Control.DrawToBitmap call anywhere in the path. An earlier version of this file
// used DrawToBitmap instead, on the documented assumption that an unshown Form reaches nothing on the
// input desktop; a live run found that claim false; the probe wins. Control.DrawToBitmap on a top-level,
// activatable Form (WidgetCard in its normal, non-notice mode has no WS_EX_NOACTIVATE, since it takes real
// keyboard focus when actually shown) briefly makes the window visible, and briefly takes the foreground,
// on whatever desktop this process is attached to, to do its own internal layout - the owner's own desktop
// for a command line tool with no private one of its own. Safe under EARSHOT_SAFE_MODE=1 with
// EARSHOT_DATA_ROOT pointed at a temp folder: the card's own log writes land under that redirected folder
// too, the same FileLog(Paths.Current.LogFolder) every other probe target already uses.
//
// Every fixture rendered here is synthetic: the battery percentages, the read time and the where-state are
// all made up for layout purposes, not read from a device. These captures preview the layout, including a part
// whose value is old and so drawn greyed, not a claim about what the widget shows on the owner's own hardware.
//
// The card's window handle, if RenderContent's caller ever created one at all, is never shown, so
// the transient backdrop the card asks DWM for (DwmExtendFrameIntoClientArea with a transient window
// backdrop) is never actually composited by the desktop: WidgetCard.RenderContent clears to fully
// transparent whenever _dwmBackdropOk reads true regardless, which it does even without a handle, and
// painting straight onto an already-opaque destination bitmap bakes that to opaque black with no alpha
// left to recover afterwards. RenderProbeWidgetCard below instead sets the card's own
// OverrideBackgroundForCaptureOnly before calling Render, so RenderContent clears to a solid colour
// directly. The colours are one sample each, read off the screen on this PC and not a citation, and the
// backdrop is translucent, so what a desktop really shows behind the card changes with the wallpaper and
// the theme: #545454 was read for the dark theme and #D3D3D3 for the light one. They stand in so a
// light-theme capture is not just pale content on what would otherwise be an opaque black square; they
// are not the backdrop's colour.
internal static partial class Program
{
    internal static readonly IReadOnlyList<uint> ProbeWidgetDpis = [96, 120, 144];

    internal static readonly IReadOnlyList<(string Name, Color Ink)> ProbeWidgetInks =
    [
        ("dark-taskbar-white-ink", Color.White),
        ("light-taskbar-black-ink", Color.Black),
    ];

    // One sample of the transient backdrop's colour per theme, read off the screen on this PC (dark theme
    // #545454, light theme #D3D3D3), not a property of the material: the backdrop is translucent and shows
    // the wallpaper through. Used only to give an offscreen, never-composited card capture a readable,
    // theme-appropriate background; never drawn by the real, on-screen card, which gets the real backdrop
    // DWM itself composites.
    internal static readonly Color DarkThemeBackdrop = Color.FromArgb(0x54, 0x54, 0x54);
    internal static readonly Color LightThemeBackdrop = Color.FromArgb(0xD3, 0xD3, 0xD3);

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

        IReadOnlyList<ProbeWidgetFile> files = ctx.Set == ProbeWidgetDesignSet
            ? RenderProbeWidgetDesignSet(target)
            : RenderProbeWidgets(target);
        WriteProbeWidgets(ctx, target, files);
        ctx.ExitCode = files.Any(f => f.Bytes == 0) ? ExitCodes.IoError : ExitCodes.Ok;
    }

    // The fixed synthetic snapshots the design asks for: on this PC with the left bud charging and the right bud
    // in the ear, read five seconds ago (every value fresh); the same read four minutes ago (every value greyed);
    // and elsewhere.
    internal static IReadOnlyList<(string Name, WidgetSnapshot Snapshot)> ProbeWidgetSnapshots(DateTimeOffset now) =>
    [
        ("this-pc", ProbeBatterySnapshot(AirPodsWhere.ThisPc, now - TimeSpan.FromSeconds(5))),
        ("elsewhere", ProbeBatterySnapshot(AirPodsWhere.Elsewhere, now - TimeSpan.FromSeconds(5))),
        ("greyed", ProbeBatterySnapshot(AirPodsWhere.ThisPc, now - TimeSpan.FromMinutes(4))),
    ];

    private static WidgetSnapshot ProbeBatterySnapshot(AirPodsWhere where, DateTimeOffset readAt) =>
        WidgetSnapshot.Empty(WidgetWatcherState.Started) with
        {
            Where = where,
            Selection = BroadcastSelectionState.Linked,
            Left = new PartReading(70, true, null) { ReadAt = readAt },
            Right = new PartReading(60, false, true) { ReadAt = readAt },
            Case = new PartReading(90, false, null) { ReadAt = readAt },
            BatteryReadAt = readAt,
        };

    internal static IReadOnlyList<ProbeWidgetFile> RenderProbeWidgets(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var files = new List<ProbeWidgetFile>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string fontFamily = TypeRamp.FamilyFor(TypeRole.Gauge);

        foreach ((string snapshotName, WidgetSnapshot snapshot) in ProbeWidgetSnapshots(now))
        {
            foreach (uint dpi in ProbeWidgetDpis)
            {
                foreach ((string inkName, Color ink) in ProbeWidgetInks)
                {
                    int width = GaugeLayout.For((int)dpi).Width;
                    int height = (int)Math.Round(48 * dpi / 96.0);
                    string name = string.Create(CultureInfo.InvariantCulture,
                        $"gauge-{snapshotName}-{dpi}dpi-{width}x{height}-{inkName}.png");
                    string path = Path.Combine(folder, name);
                    files.Add(RenderProbeWidget(snapshotName, snapshot, now, dpi, height, inkName, ink, fontFamily, path));
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
        string snapshotName, WidgetSnapshot snapshot, DateTimeOffset now, uint dpi, int height, string inkName, Color ink,
        string fontFamily, string path)
    {
        try
        {
            using Bitmap bitmap = GaugeRenderer.Render(snapshot, now, (int)dpi, height, ink, hover: false, fontFamily);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            var info = new FileInfo(path);
            return new ProbeWidgetFile(snapshotName, dpi, inkName, path, (int)info.Length, null);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new ProbeWidgetFile(snapshotName, dpi, inkName, path, 0, ex.Message);
        }
    }

    // The card variants the widget card probe renders, from the same fixed synthetic snapshots the gauge already
    // uses. "this-pc" and "elsewhere" are the card as it renders with fresh values (AutoPauseAvailable false in both,
    // so the switch row never appears); "greyed" is the same values read four minutes ago, drawn greyed with their
    // age; "no-reading" is a card with no value for any part; "open-the-case" is the connected card with no pair linked;
    // "refresh-reading" and "refresh-nothing-heard" are a battery refresh in progress and one that heard nothing. "auto-pause-preview" is a preview only: production
    // never sets AutoPauseAvailable true until the in-ear signal is known.
    internal static IReadOnlyList<(string Variant, WidgetCardModel Model)> ProbeWidgetCardVariants(DateTimeOffset now)
    {
        IReadOnlyList<(string Name, WidgetSnapshot Snapshot)> snapshots = ProbeWidgetSnapshots(now);
        WidgetSnapshot thisPc = snapshots[0].Snapshot;
        WidgetSnapshot elsewhere = snapshots[1].Snapshot;
        WidgetSnapshot greyed = snapshots[2].Snapshot;
        WidgetSnapshot none = WidgetSnapshot.Empty(WidgetWatcherState.Started) with { Where = AirPodsWhere.ThisPc };

        return
        [
            ("this-pc", new WidgetCardModel(thisPc, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: false,
                ButtonEnabled: true, OtherDeviceLabel: "", Now: now)),
            ("elsewhere", new WidgetCardModel(elsewhere, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: true,
                ButtonEnabled: true, OtherDeviceLabel: "iPhone", Now: now)),
            ("greyed", new WidgetCardModel(greyed, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: false,
                ButtonEnabled: true, OtherDeviceLabel: "", Now: now)),
            ("no-reading", new WidgetCardModel(none, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: true,
                ButtonEnabled: true, OtherDeviceLabel: "", Now: now)),
            // Connected, with no pair linked yet: the line that says opening the case next to the PC links one.
            ("open-the-case", new WidgetCardModel(none with { Selection = BroadcastSelectionState.Listening }, AutoPauseOn: false,
                ShowSwitch: false, ConnectIntent: false, ButtonEnabled: true, OtherDeviceLabel: "", Now: now)),
            ("auto-pause-preview", new WidgetCardModel(thisPc with { AutoPauseAvailable = true },
                AutoPauseOn: false, ShowSwitch: true, ConnectIntent: false, ButtonEnabled: true,
                OtherDeviceLabel: "", Now: now)),
            // A refresh in progress, its icon a third of a turn on, and one that ended with nothing heard.
            ("refresh-reading", new WidgetCardModel(greyed, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: false,
                ButtonEnabled: true, OtherDeviceLabel: "", Now: now)
            {
                Refresh = new BatteryRefreshView(Reading: true, SpinFrame: 3),
            }),
            ("refresh-nothing-heard", new WidgetCardModel(greyed, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: false,
                ButtonEnabled: true, OtherDeviceLabel: "", Now: now)
            {
                Refresh = new BatteryRefreshView(Reading: false, Outcome: BatteryRefreshOutcome.NothingHeard),
            }),
        ];
    }

    // Renders one WidgetCard(notice) straight into an off-screen bitmap's Graphics (WidgetCard.RenderContent),
    // never Control.DrawToBitmap: that call was found to make a top-level, activatable Form like this one
    // briefly visible, and a normal-mode card briefly take the foreground, on whatever desktop this process
    // runs on to do its own internal layout - the owner's real desktop for this command line tool. Saved as
    // a PNG named "{namePrefix}-{dpi}dpi-{w}x{h}-{ink}.png" where w x h is the card's own ClientSize after
    // Render, since the card's height comes from its content, not a fixed constant.
    private static ProbeWidgetFile RenderProbeWidgetCard(
        ILog log, string namePrefix, string variant, WidgetCardModel model, uint dpi, string inkName, Color ink, bool notice, string folder)
    {
        string fallbackPath = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture,
            $"{namePrefix}-{dpi}dpi-{inkName}.png"));
        try
        {
            using var card = new WidgetCard(log, notice);
            card.SetTheme(ink, highContrast: false);
            // The card is never actually composited by DWM here (the window handle, if it exists at all,
            // is never shown), so its own translucent-backdrop clear leaves nothing for the capture to
            // preserve but opaque black. Overriding the clear colour outright, to the measured transient
            // material DWM's own backdrop actually reads at, is what a probe capture needs instead.
            bool dark = ink.GetBrightness() >= 0.5f;
            card.OverrideBackgroundForCaptureOnly = dark ? DarkThemeBackdrop : LightThemeBackdrop;
            card.Render(model, (int)dpi);

            Size size = card.ClientSize;
            using var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                card.RenderContent(g);
            }

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
