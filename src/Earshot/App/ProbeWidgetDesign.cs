using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.App;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Update;
using Earshot.Widget;

namespace Earshot;

// probe widget --out <folder> --set design: the pictures a designer needs to see the widget, one file for
// each thing it shows, named by that thing. Everything is drawn from made-up snapshots (device name
// "AirPods", invented percentages and times) through the same off-screen paths the default probe set uses:
// GaugeRenderer straight to a bitmap, and WidgetCard.RenderContent into a bitmap's Graphics. No window is
// created, shown or activated, and Control.DrawToBitmap is never called.
//
// Each picture is made at 100% and 150% display scale (96 and 144 dpi). The card is also drawn with Windows'
// text size at 150%, which is a setting of its own and changes the layout.
internal static partial class Program
{
    internal const string ProbeWidgetDesignSet = "design";

    internal static readonly IReadOnlyList<uint> ProbeWidgetDesignDpis = [96, 144];

    // The text size, as a fraction, the larger-text pictures ask the card for.
    internal const double ProbeWidgetDesignLargeText = 1.5;

    // One flat colour per theme for the gauge to sit on, so a picture of it can be looked at on its own. They
    // stand for a taskbar and are samples, not the colour of any taskbar.
    internal static readonly Color DarkTaskbarSample = Color.FromArgb(0x20, 0x20, 0x20);
    internal static readonly Color LightTaskbarSample = Color.FromArgb(0xF3, 0xF3, 0xF3);

    private static readonly IReadOnlyList<(string Name, Color Ink)> ProbeWidgetDesignThemes =
    [
        ("light", Color.Black),
        ("dark", Color.White),
    ];

    internal static string ProbeWidgetScaleName(uint dpi) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(dpi * 100 / 96.0)}pct");

    // The gauge's states: what a reading, a charging bud, a low battery, nothing recent, not on this PC and in
    // use on the other device each look like.
    internal static IReadOnlyList<(string Name, WidgetSnapshot Snapshot)> ProbeWidgetDesignGaugeStates(DateTimeOffset now)
    {
        DateTimeOffset fresh = now - TimeSpan.FromSeconds(5);
        return
        [
            ("reading", DesignSnapshot(AirPodsWhere.ThisPc, fresh, left: 70, right: 60, leftCharging: false, rightCharging: false)),
            ("charging", DesignSnapshot(AirPodsWhere.ThisPc, fresh, left: 55, right: 80, leftCharging: true, rightCharging: true)),
            ("low", DesignSnapshot(AirPodsWhere.ThisPc, fresh, left: 15, right: 18, leftCharging: false, rightCharging: false)),
            ("no-recent-reading", DesignSnapshot(AirPodsWhere.ThisPc, now - TimeSpan.FromHours(2), left: 70, right: 60, leftCharging: false, rightCharging: false)),
            ("not-on-this-pc", DesignSnapshot(AirPodsWhere.NotInUse, fresh, left: 70, right: 60, leftCharging: false, rightCharging: false)),
            ("on-the-other-device", DesignSnapshot(AirPodsWhere.Elsewhere, fresh, left: 70, right: 60, leftCharging: false, rightCharging: false)),
        ];
    }

    private static WidgetSnapshot DesignSnapshot(
        AirPodsWhere where, DateTimeOffset readAt, int left, int right, bool leftCharging, bool rightCharging) =>
        WidgetSnapshot.Empty(WidgetWatcherState.Started) with
        {
            Where = where,
            Left = new PartReading(left, leftCharging, null) { ReadAt = readAt },
            Right = new PartReading(right, rightCharging, null) { ReadAt = readAt },
            Case = new PartReading(90, false, null) { ReadAt = readAt },
            BatteryReadAt = readAt,
        };

    // The file-name form of an order: "Ring, number, bolt" is "ring-number-bolt".
    internal static string ProbeWidgetOrderSlug(GaugeOrder order) =>
        GaugeOrders.AccessibleName(order).Replace(",", "", StringComparison.Ordinal).Replace(' ', '-').ToLowerInvariant();

    internal static IReadOnlyList<ProbeWidgetFile> RenderProbeWidgetDesignSet(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var files = new List<ProbeWidgetFile>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string fontFamily = TypeRamp.FamilyFor(TypeRole.Gauge);
        var log = new FileLog(Paths.Current.LogFolder);

        // The gauge: every state, then the six orders (each drawn charging, so the bolt shows).
        var gaugePictures = new List<(string Name, WidgetSnapshot Snapshot, GaugeOrder Order)>();
        foreach ((string state, WidgetSnapshot snapshot) in ProbeWidgetDesignGaugeStates(now))
        {
            gaugePictures.Add(("gauge-" + state, snapshot, GaugeOrder.RingNumberBolt));
        }

        WidgetSnapshot charging = ProbeWidgetDesignGaugeStates(now)[1].Snapshot;
        foreach (GaugeOrder order in Enum.GetValues<GaugeOrder>())
        {
            gaugePictures.Add(("gauge-order-" + ProbeWidgetOrderSlug(order), charging, order));
        }

        foreach ((string name, WidgetSnapshot snapshot, GaugeOrder order) in gaugePictures)
        {
            foreach ((string theme, Color ink) in ProbeWidgetDesignThemes)
            {
                foreach (uint dpi in ProbeWidgetDesignDpis)
                {
                    string path = Path.Combine(folder, name + "-" + theme + "-" + ProbeWidgetScaleName(dpi) + ".png");
                    files.Add(RenderDesignGauge(name, snapshot, order, now, dpi, theme, ink, fontFamily, path));
                }
            }
        }

        // The card and its pages.
        IReadOnlyList<(string Variant, WidgetCardModel Model)> cardVariants = ProbeWidgetCardVariants(now);
        WidgetCardModel Variant(string name) => cardVariants.Single(v => v.Variant == name).Model;

        var cardPictures = new List<(string Name, WidgetCardModel Model, double TextScale)>
        {
            ("card-fresh", Variant("this-pc"), 1.0),
            ("card-greyed", Variant("greyed"), 1.0),
            ("card-refreshing", Variant("refresh-reading"), 1.0),
            ("card-refresh-open-the-case", Variant("refresh-nothing-heard"), 1.0),
            ("card-fresh-text-150", Variant("this-pc"), ProbeWidgetDesignLargeText),
            ("card-greyed-text-150", Variant("greyed"), ProbeWidgetDesignLargeText),
            ("settings", DesignSettingsModel(now), 1.0),
            ("update-available", DesignUpdateModel(now, UpdateStage.Available, null), 1.0),
            ("update-downloading", DesignUpdateModel(now, UpdateStage.Downloading, 40), 1.0),
        };

        foreach ((string name, WidgetCardModel model, double textScale) in cardPictures)
        {
            foreach ((string theme, Color ink) in ProbeWidgetDesignThemes)
            {
                foreach (uint dpi in ProbeWidgetDesignDpis)
                {
                    string path = Path.Combine(folder, name + "-" + theme + "-" + ProbeWidgetScaleName(dpi) + ".png");
                    files.Add(RenderDesignCard(log, name, model, textScale, dpi, theme, ink, path));
                }
            }
        }

        return files;
    }

    // The settings page as it stands with the gauge showing a charging reading, so the order pictures are all the
    // real gauge, and the Hands-Free microphone mode on.
    private static WidgetCardModel DesignSettingsModel(DateTimeOffset now)
    {
        WidgetSnapshot snapshot = ProbeWidgetDesignGaugeStates(now)[1].Snapshot;
        var values = new CardSettingsValues(
            GaugePosition.RightEnd,
            "iPhone",
            PauseWhenBudComesOut: true,
            PauseWhenAirPodsLeave: true,
            LowBatteryPercent: 20,
            LeftClickConnects: false,
            HandBack: true,
            ConnectChord: "Ctrl+Alt+Shift+A",
            DisconnectChord: "Ctrl+Alt+Shift+D",
            ConnectFailure: null,
            DisconnectFailure: null,
            InstalledVersion: "1.3.0",
            CheckAutomatically: false,
            InEarProofMissing: false,
            InstallExists: true)
        {
            HandsFreeMicrophoneOff = true,
            MicrophoneState = MicrophoneRowState.OpenSettings,
            GaugeOrder = GaugeOrder.RingNumberBolt,
            GaugePreview = GaugeContent.From(snapshot, now, new GaugeDisplaySettings(20, "iPhone")),
        };

        return new WidgetCardModel(snapshot, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: false,
            ButtonEnabled: true, OtherDeviceLabel: "iPhone", Now: now, View: WidgetCardView.Settings)
        {
            Settings = values,
        };
    }

    private static WidgetCardModel DesignUpdateModel(DateTimeOffset now, UpdateStage stage, int? progress)
    {
        WidgetSnapshot snapshot = ProbeWidgetDesignGaugeStates(now)[0].Snapshot;
        var installed = new ReleaseVersion(1, 2, 1);
        var available = new ReleaseVersion(1, 3, 0);
        SetupViewModel page = WidgetCardUpdatePage.From(
            UpdateViewModel.For(stage, installed, available, progress, reason: null, notice: null), spinnerFrame: 3);
        return new WidgetCardModel(snapshot, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: false,
            ButtonEnabled: true, OtherDeviceLabel: "iPhone", Now: now, View: WidgetCardView.Update, Setup: page);
    }

    private static ProbeWidgetFile RenderDesignGauge(
        string name, WidgetSnapshot snapshot, GaugeOrder order, DateTimeOffset now, uint dpi, string theme, Color ink,
        string fontFamily, string path)
    {
        try
        {
            bool light = ink.GetBrightness() < 0.5f;
            Color accent = UiSettingsColourSource.DefaultShade(light ? AccentShade.Dark1 : AccentShade.Light2);
            GaugePalette palette = GaugePalette.Create(light, accent, highContrast: false, ink);
            GaugeContent content = GaugeContent.From(snapshot, now, GaugeDisplaySettings.Default);
            GaugeLayout layout = GaugeLayout.For((int)dpi, order);
            using Bitmap gauge = GaugeRenderer.Render(content, palette, layout, hover: false, fontFamily);

            // The gauge is transparent, drawn over the taskbar; a flat sample behind it makes the file readable alone.
            using var onTaskbar = new Bitmap(gauge.Width, gauge.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(onTaskbar))
            {
                g.Clear(light ? LightTaskbarSample : DarkTaskbarSample);
                g.CompositingMode = CompositingMode.SourceOver;
                g.DrawImage(gauge, 0, 0, gauge.Width, gauge.Height);
            }

            onTaskbar.Save(path, ImageFormat.Png);
            return new ProbeWidgetFile(name, dpi, theme, path, (int)new FileInfo(path).Length, null);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new ProbeWidgetFile(name, dpi, theme, path, 0, ex.Message);
        }
    }

    private static ProbeWidgetFile RenderDesignCard(
        ILog log, string name, WidgetCardModel model, double textScale, uint dpi, string theme, Color ink, string path)
    {
        try
        {
            using var card = new WidgetCard(log, notice: false);
            var look = new SystemLook(textScale, Transparency: true, HighContrast: false);
            card.AttachLook(() => look);
            card.SetTheme(ink, highContrast: false);
            card.OverrideBackgroundForCaptureOnly = ink.GetBrightness() >= 0.5f ? DarkThemeBackdrop : LightThemeBackdrop;
            card.Render(model, (int)dpi);

            Size size = card.ClientSize;
            using var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                card.RenderContent(g);
            }

            bitmap.Save(path, ImageFormat.Png);
            return new ProbeWidgetFile(name, dpi, theme, path, (int)new FileInfo(path).Length, null);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ProbeWidgetFile(name, dpi, theme, path, 0, ex.Message);
        }
    }
}
