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

    // The three themes the design has: light, dark and high contrast (drawn in the system colours of the machine it runs on).
    private static readonly IReadOnlyList<(string Name, Color Ink, bool HighContrast)> ProbeWidgetDesignThemes =
    [
        ("light", Color.Black, false),
        ("dark", Color.White, false),
        ("high-contrast", SystemColors.WindowText, true),
    ];

    internal static string ProbeWidgetScaleName(uint dpi) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(dpi * 100 / 96.0)}pct");

    // The gauge's states drawn from made-up content instead of a snapshot: a last reading and an estimate on this PC, and what the
    // gauge shows with the AirPods away, the case mark with its value (a last reading, an estimate, one still charging and a live
    // value). Each is the design's state, in the same words as the snapshot states.
    internal static IReadOnlyList<(string Name, GaugeContent Content)> ProbeWidgetDesignGaugeContents() =>
    [
        ("last-reading", new GaugeContent(GaugeMode.Reading, 62, false, false, "") { Tertiary = true }),
        ("estimate", new GaugeContent(GaugeMode.Reading, 64, false, false, "") { Tertiary = true, Estimated = true }),
        ("away-case-last-reading", new GaugeContent(GaugeMode.CaseAway, 80, false, false, "") { CaseMark = true, Tertiary = true }),
        ("away-case-estimate", new GaugeContent(GaugeMode.CaseAway, 90, false, false, "") { CaseMark = true, Tertiary = true, Estimated = true }),
        ("away-case-charging", new GaugeContent(GaugeMode.CaseAway, 55, false, true, "") { CaseMark = true, Tertiary = true }),
        ("away-case-live", new GaugeContent(GaugeMode.CaseAway, 75, false, false, "") { CaseMark = true }),
    ];

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
            Selection = BroadcastSelectionState.Linked,
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

        // The gauge: every state, then the states of a last reading, an estimate and the case mark, then the six orders (each drawn
        // charging, so the bolt shows).
        var gaugePictures = new List<(string Name, GaugeContent Content, GaugeOrder Order)>();
        foreach ((string state, WidgetSnapshot snapshot) in ProbeWidgetDesignGaugeStates(now))
        {
            gaugePictures.Add(("gauge-" + state, GaugeContent.From(snapshot, now, GaugeDisplaySettings.Default), GaugeOrder.RingNumberBolt));
        }

        foreach ((string state, GaugeContent content) in ProbeWidgetDesignGaugeContents())
        {
            gaugePictures.Add(("gauge-" + state, content, GaugeOrder.RingNumberBolt));
        }

        GaugeContent chargingContent = GaugeContent.From(ProbeWidgetDesignGaugeStates(now)[1].Snapshot, now, GaugeDisplaySettings.Default);
        foreach (GaugeOrder order in Enum.GetValues<GaugeOrder>())
        {
            gaugePictures.Add(("gauge-order-" + ProbeWidgetOrderSlug(order), chargingContent, order));
        }

        foreach ((string name, GaugeContent content, GaugeOrder order) in gaugePictures)
        {
            foreach ((string theme, Color ink, bool highContrast) in ProbeWidgetDesignThemes)
            {
                foreach (uint dpi in ProbeWidgetDesignDpis)
                {
                    string path = Path.Combine(folder, name + "-" + theme + "-" + ProbeWidgetScaleName(dpi) + ".png");
                    files.Add(RenderDesignGauge(name, content, order, dpi, theme, ink, highContrast, fontFamily, path));
                }
            }
        }

        // The card and its pages.
        IReadOnlyList<(string Variant, WidgetCardModel Model)> cardVariants = ProbeWidgetCardVariants(now);
        WidgetCardModel Variant(string name) => cardVariants.Single(v => v.Variant == name).Model;

        var cardPictures = new List<(string Name, WidgetCardModel Model, double TextScale, bool Notice, bool ExpandMore, bool ExpandOrder, bool ExpandCase)>
        {
            ("card-fresh", Variant("this-pc"), 1.0, false, false, false, false),
            ("card-greyed", Variant("greyed"), 1.0, false, false, false, false),
            ("card-refreshing", Variant("refresh-reading"), 1.0, false, false, false, false),
            ("card-refresh-open-the-case", Variant("refresh-nothing-heard"), 1.0, false, false, false, false),
            ("card-fresh-text-150", Variant("this-pc"), ProbeWidgetDesignLargeText, false, false, false, false),
            ("card-greyed-text-150", Variant("greyed"), ProbeWidgetDesignLargeText, false, false, false, false),
            ("card-estimates", DesignEstimatesModel(now), 1.0, false, false, false, false),
            ("card-estimates-text-150", DesignEstimatesModel(now), ProbeWidgetDesignLargeText, false, false, false, false),
            ("card-bluetooth-off", DesignBluetoothOffModel(now), 1.0, false, false, false, false),
            ("card-case-open", Variant("this-pc"), 1.0, true, false, false, false),
            ("card-case-open-estimates", DesignEstimatesModel(now), 1.0, true, false, false, false),
            ("settings", DesignSettingsModel(now), 1.0, false, false, false, false),
            ("settings-text-150", DesignSettingsModel(now), ProbeWidgetDesignLargeText, false, false, false, false),
            ("settings-more-open", DesignSettingsModel(now), 1.0, false, true, false, false),
            ("settings-order-open", DesignSettingsModel(now), 1.0, false, false, true, false),
            ("settings-case-open-card-open", DesignSettingsModel(now), 1.0, false, false, false, true),
            ("history", DesignHistoryModel(now), 1.0, false, false, false, false),
            ("update-up-to-date", DesignUpdateModel(now, UpdateStage.UpToDate, null), 1.0, false, false, false, false),
            ("update-available", DesignUpdateModel(now, UpdateStage.Available, null), 1.0, false, false, false, false),
            ("update-downloading", DesignUpdateModel(now, UpdateStage.Downloading, 40), 1.0, false, false, false, false),
        };

        foreach ((string name, WidgetCardModel model, double textScale, bool notice, bool more, bool order, bool caseCard) in cardPictures)
        {
            foreach ((string theme, Color ink, bool highContrast) in ProbeWidgetDesignThemes)
            {
                foreach (uint dpi in ProbeWidgetDesignDpis)
                {
                    string path = Path.Combine(folder, name + "-" + theme + "-" + ProbeWidgetScaleName(dpi) + ".png");
                    files.Add(RenderDesignCard(log, name, model, textScale, notice, more, order, caseCard, dpi, theme, ink, highContrast, path));
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
            InstalledVersion: "1.4.0",
            CheckAutomatically: false,
            InEarProofMissing: false,
            InstallExists: true)
        {
            HandsFreeMicrophoneOff = true,
            MicrophoneState = MicrophoneRowState.OpenSettings,
            GaugeOrder = GaugeOrder.RingNumberBolt,
            GaugePreview = GaugeContent.From(snapshot, now, new GaugeDisplaySettings(20, "iPhone")),
            FullyChargedNotice = true,
        };

        return new WidgetCardModel(snapshot, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: false,
            ButtonEnabled: true, OtherDeviceLabel: "iPhone", Now: now, View: WidgetCardView.Settings)
        {
            Settings = values,
        };
    }

    // The card with estimates: the buds' values grown from a reading four minutes old, the case's from a reading two hours old. Each is
    // marked "≈" and shows the age of the reading it grew from.
    private static WidgetCardModel DesignEstimatesModel(DateTimeOffset now)
    {
        WidgetSnapshot snapshot = ProbeWidgetDesignGaugeStates(now)[0].Snapshot;
        DateTimeOffset budRead = now - TimeSpan.FromMinutes(4);
        DateTimeOffset caseRead = now - TimeSpan.FromHours(2);
        var parts = new ShownBattery(
            new ShownPart(66, true, ReadingKind.Estimated, budRead) { ReadPercent = 62 },
            new ShownPart(60, false, ReadingKind.Last, budRead),
            new ShownPart(94, true, ReadingKind.Estimated, caseRead) { ReadPercent = 90 },
            null, null, null);
        return new WidgetCardModel(snapshot, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: false,
            ButtonEnabled: true, OtherDeviceLabel: "iPhone", Now: now)
        {
            Parts = parts,
        };
    }

    // The card while Bluetooth is off: the watcher stopped for the radio's absence.
    private static WidgetCardModel DesignBluetoothOffModel(DateTimeOffset now)
    {
        WidgetSnapshot snapshot = ProbeWidgetDesignGaugeStates(now)[3].Snapshot with
        {
            Watcher = WidgetWatcherState.Stopped,
            WatcherErrorCode = AdvertisementSourceCodes.RadioNotAvailableCode,
            WatcherErrorName = AdvertisementSourceCodes.RadioNotAvailableName,
        };
        return new WidgetCardModel(snapshot, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: true,
            ButtonEnabled: true, OtherDeviceLabel: "iPhone", Now: now);
    }

    // The battery history page, with no samples (the probe keeps no history).
    private static WidgetCardModel DesignHistoryModel(DateTimeOffset now)
    {
        WidgetSnapshot snapshot = ProbeWidgetDesignGaugeStates(now)[0].Snapshot;
        var page = new SetupViewModel(WidgetCopy.HistoryTitle, null, null, null, SetupIcon.None, null, null, Array.Empty<SetupButton>(), 0) { History = HistoryDays.View(0, now, TimeZoneInfo.Local, null) };
        return new WidgetCardModel(snapshot, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: false,
            ButtonEnabled: true, OtherDeviceLabel: "iPhone", Now: now, View: WidgetCardView.History, Setup: page);
    }

    private static WidgetCardModel DesignUpdateModel(DateTimeOffset now, UpdateStage stage, int? progress)
    {
        WidgetSnapshot snapshot = ProbeWidgetDesignGaugeStates(now)[0].Snapshot;
        var installed = new ReleaseVersion(1, 2, 1);
        var available = new ReleaseVersion(1, 3, 0);
        SetupViewModel page = WidgetCardUpdatePage.From(
            UpdateViewModel.For(stage, installed, available, progress, reason: null, notice: null), spinnerFrame: 3);
        page = page with { Rows = new UpdatesRows(AutoCheck: false, ShowRepair: true, Version: installed.ToString()) };
        return new WidgetCardModel(snapshot, AutoPauseOn: false, ShowSwitch: false, ConnectIntent: false,
            ButtonEnabled: true, OtherDeviceLabel: "iPhone", Now: now, View: WidgetCardView.Update, Setup: page);
    }

    private static ProbeWidgetFile RenderDesignGauge(
        string name, GaugeContent content, GaugeOrder order, uint dpi, string theme, Color ink, bool highContrast,
        string fontFamily, string path)
    {
        try
        {
            bool light = !highContrast && ink.GetBrightness() < 0.5f;
            Color accent = UiSettingsColourSource.DefaultShade(light ? AccentShade.Dark1 : AccentShade.Light2);
            GaugePalette palette = GaugePalette.Create(light, accent, highContrast, ink);
            GaugeLayout layout = GaugeLayout.For((int)dpi, order);
            using Bitmap gauge = GaugeRenderer.Render(content, palette, layout, hover: false, fontFamily);

            // The gauge is transparent, drawn over the taskbar; a flat sample behind it makes the file readable alone.
            using var onTaskbar = new Bitmap(gauge.Width, gauge.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(onTaskbar))
            {
                g.Clear(highContrast ? SystemColors.Window : light ? LightTaskbarSample : DarkTaskbarSample);
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
        ILog log, string name, WidgetCardModel model, double textScale, bool notice, bool expandMore, bool expandOrder, bool expandCase,
        uint dpi, string theme, Color ink, bool highContrast, string path)
    {
        try
        {
            using var card = new WidgetCard(log, notice);
            var look = new SystemLook(textScale, Transparency: !highContrast, HighContrast: highContrast);
            card.AttachLook(() => look);
            card.SetTheme(ink, highContrast);
            card.OverrideBackgroundForCaptureOnly = highContrast ? SystemColors.Window : ink.GetBrightness() >= 0.5f ? DarkThemeBackdrop : LightThemeBackdrop;
            card.Render(model, (int)dpi);
            if (expandMore || expandOrder || expandCase)
            {
                card.ExpandSettingsRowsForCapture(expandMore, expandOrder, expandCase);
            }

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
