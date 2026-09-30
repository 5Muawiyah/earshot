using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Earshot.App;
using Earshot.Update;
using Earshot.Widget;

namespace Earshot.Tests.Widget;

// What the settings and update page tests share: a fake for the seam between the card and the tray, the card's
// callbacks over fakes, and the small helpers that paint a card into a bitmap and click on it. Nothing here shows
// a window on its own; a test that needs a real card runs it on a private desktop (Phase5.CardDesktop.Run).
internal sealed class FakeCardHost : IWidgetCardHost
{
    public CardSettingsValues Values { get; set; } = Defaults();

    // Every call the card makes, in order, as short text such as "gauge:NextToApps".
    public List<string> Calls { get; } = [];

    // Set to make SetShortcut refuse, with this as the reason. Nothing is stored then.
    public string? ShortcutRefusal { get; set; }

    public UpdateViewModel? View { get; set; }

    public string? AvailableVersion { get; set; }

    public int CheckCalls { get; private set; }

    public int StartUpdateCalls { get; private set; }

    public int CancelCalls { get; private set; }

    public int TryAgainCalls { get; private set; }

    public event EventHandler? UpdateChanged;

    public void RaiseUpdateChanged() => UpdateChanged?.Invoke(this, EventArgs.Empty);

    // The design's defaults, with invented version and chord texts.
    public static CardSettingsValues Defaults() => new(
        GaugePosition.RightEnd, "iPhone", PauseWhenBudComesOut: true, PauseWhenAirPodsLeave: true, CaseOpenCard: true, LowBatteryPercent: 20,
        LeftClickConnects: false, HandBack: true, ConnectChord: "Ctrl+Alt+Shift+A", DisconnectChord: "Ctrl+Alt+Shift+D",
        ConnectFailure: null, DisconnectFailure: null, InstalledVersion: "1.1.0", CheckAutomatically: false,
        InEarProofMissing: false, LidProofMissing: false);

    public CardSettingsValues ReadSettings() => Values;

    public void SetGaugePosition(GaugePosition value, CardPlace place)
    {
        Calls.Add("gauge:" + value);
        Values = Values with { GaugePosition = value };
    }

    public void SetOtherDeviceLabel(string value, CardPlace place)
    {
        Calls.Add("label:" + value);
        Values = Values with { OtherDeviceLabel = WidgetSettings.CleanedLabel(value) };
    }

    public void SetPauseWhenBudComesOut(bool on, CardPlace place)
    {
        Calls.Add("pauseBud:" + on);
        Values = Values with { PauseWhenBudComesOut = on };
    }

    public void SetPauseWhenAirPodsLeave(bool on, CardPlace place)
    {
        Calls.Add("pauseLeave:" + on);
        Values = Values with { PauseWhenAirPodsLeave = on };
    }

    public void SetCaseOpenCard(bool on, CardPlace place)
    {
        Calls.Add("caseCard:" + on);
        Values = Values with { CaseOpenCard = on };
    }

    public void SetLowBatteryPercent(int percent, CardPlace place)
    {
        Calls.Add("low:" + percent);
        Values = Values with { LowBatteryPercent = percent };
    }

    public void SetLeftClickConnects(bool on, CardPlace place)
    {
        Calls.Add("leftClick:" + on);
        Values = Values with { LeftClickConnects = on };
    }

    public void SetHandBack(bool on, CardPlace place)
    {
        Calls.Add("handBack:" + on);
        Values = Values with { HandBack = on };
    }

    public void SetCheckAutomatically(bool on, CardPlace place)
    {
        Calls.Add("checkAuto:" + on);
        Values = Values with { CheckAutomatically = on };
    }

    public string? SetShortcut(CardShortcut shortcut, Keys key, bool control, bool alt, bool shift, CardPlace place)
    {
        string chord = string.Join('+', new[] { control ? "Ctrl" : null, alt ? "Alt" : null, shift ? "Shift" : null, (key & Keys.KeyCode).ToString() }.Where(p => p is not null));
        Calls.Add("shortcut:" + shortcut + ":" + chord);
        if (ShortcutRefusal is not null)
        {
            return ShortcutRefusal;
        }

        Values = shortcut == CardShortcut.Connect ? Values with { ConnectChord = chord } : Values with { DisconnectChord = chord };
        return null;
    }

    public void ClearShortcut(CardShortcut shortcut, CardPlace place)
    {
        Calls.Add("clear:" + shortcut);
        Values = shortcut == CardShortcut.Connect ? Values with { ConnectChord = string.Empty } : Values with { DisconnectChord = string.Empty };
    }

    public SetupViewModel UpdatePage(int spinnerFrame) =>
        View is { } view ? WidgetCardUpdatePage.From(view, spinnerFrame) : WidgetCardUpdatePage.Unavailable();

    public string? AvailableUpdateVersion() => AvailableVersion;

    public void CheckForUpdates() => CheckCalls++;

    public void StartUpdate() => StartUpdateCalls++;

    public void CancelUpdate() => CancelCalls++;

    public void TryUpdateAgain() => TryAgainCalls++;

    public int SetUpCalls { get; private set; }

    public int RepairCalls { get; private set; }

    public int SwitchCalls { get; private set; }

    public void SetUpEarshot() => SetUpCalls++;

    public void RepairEarshot() => RepairCalls++;

    public void SwitchToInstalled() => SwitchCalls++;
}

internal sealed class FakeAccent : IAccentColours
{
    private EventHandler? _changed;

    public Color Light { get; set; } = Color.FromArgb(0xC0, 0x30, 0x20);

    public Color Dark { get; set; } = Color.FromArgb(0x20, 0xC0, 0x50);

    public int Subscribers { get; private set; }

    public Color AccentFor(bool lightTheme) => lightTheme ? Light : Dark;

    public event EventHandler? Changed
    {
        add
        {
            _changed += value;
            Subscribers++;
        }

        remove
        {
            _changed -= value;
            Subscribers--;
        }
    }

    public void Raise() => _changed?.Invoke(this, EventArgs.Empty);
}

internal static class CardKit
{
    public static readonly Rectangle Gauge = new(100, 900, 74, 40);

    public static void Inline(Action action) => action();

    // Both themes, and the scales the card is drawn at, for a test that loops over them.
    public static readonly bool[] Themes = [false, true];

    public static readonly int[] Scales = [96, 120, 144, 192];

    // The host's calls so far, in order, compared with the ones expected.
    public static void AssertCalls(FakeCardHost host, params string[] expected) =>
        Microsoft.VisualStudio.TestTools.UnitTesting.CollectionAssert.AreEqual(expected, host.Calls);

    public static WidgetCardPresenterCallbacks Callbacks(GaugePosition? position = null, WidgetSnapshot? snapshot = null) => new(
        CurrentSnapshot: () => snapshot ?? Snapshot(),
        AutoPauseOn: () => false,
        CurrentIntent: () => null,
        IsBusy: () => false,
        Dpi: () => 96,
        Ink: () => Color.Black,
        HighContrast: () => false,
        OtherDeviceLabel: () => "iPhone",
        RequestToggle: _ => { },
        SetAutoPause: (_, _) => { },
        ListenForSetup: _ => new TaskCompletionSource<BatterySetupListen>().Task,
        CompleteSetup: (_, _) => throw new NotSupportedException("No set-up runs in these tests."),
        GaugePosition: position is { } p ? () => p : null);

    public static WidgetSnapshot Snapshot(PartReading? left = null, PartReading? right = null, DateTimeOffset? readAt = null) =>
        new(
            AirPodsWhere.ThisPc,
            left ?? PartReading.Unknown,
            right ?? PartReading.Unknown,
            PartReading.Unknown,
            BatteryReadAt: readAt,
            EarReadAt: null,
            LidOpen: null,
            WidgetWatcherState.Started,
            WatcherErrorCode: null,
            WatcherErrorName: null,
            ClaimExists: true,
            AutoPauseAvailable: false,
            WidgetCounters.Empty);

    public static WidgetCardModel MainModel(WidgetSnapshot? snapshot = null, string? updateVersion = null, bool showSetupButton = false) =>
        new(snapshot ?? Snapshot(), false, false, true, true, "iPhone", DateTimeOffset.UtcNow, showSetupButton, UpdateVersion: updateVersion);

    public static WidgetCardModel SettingsModel(CardSettingsValues values) =>
        new(Snapshot(), false, false, true, true, "iPhone", DateTimeOffset.UtcNow, false, WidgetCardView.Settings, Settings: values);

    public static WidgetCardModel UpdateModel(UpdateViewModel view) =>
        new(Snapshot(), false, false, true, true, "iPhone", DateTimeOffset.UtcNow, false, WidgetCardView.Update, WidgetCardUpdatePage.From(view));

    // A card painted into an off-screen bitmap through its own paint routine: never shown, never given a handle.
    public static Bitmap Render(WidgetCard card)
    {
        Size size = card.ClientSize;
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            card.RenderContent(g);
        }

        return bitmap;
    }

    public static bool HasInk(Bitmap bitmap, Rectangle rect, Color background) => CountInk(bitmap, rect, background) > 0;

    public static int CountInk(Bitmap bitmap, Rectangle rect, Color background)
    {
        Rectangle bounds = Rectangle.Intersect(rect, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        int count = 0;
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (int x = bounds.Left; x < bounds.Right; x++)
            {
                if (bitmap.GetPixel(x, y) != background)
                {
                    count++;
                }
            }
        }

        return count;
    }

    // The ink colours of the two themes the card is told about: dark ink means the taskbar is light, so a light card.
    public static Color InkFor(bool dark) => dark ? Color.White : Color.Black;

    public static WidgetCard NewCard(bool dark, bool notice = false)
    {
        var card = new WidgetCard(new CapturingLog(), notice);
        card.SetTheme(InkFor(dark), highContrast: false);
        return card;
    }

    // A real left click on a control of a shown card: a left down then a left up at the centre of rect.
    public static void Click(WidgetCard card, Rectangle rect)
    {
        var point = new Point(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
        nint lParam = (nint)(((point.Y & 0xFFFF) << 16) | (point.X & 0xFFFF));
        Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONDOWN, 0, lParam);
        Phase5.TestWindows.Send(card.Handle, Phase5.TestWindows.WM_LBUTTONUP, 0, lParam);
    }

    public static SettingsItem Row(WidgetCard card, SettingsRowId row) =>
        card.CurrentSettingsLayout!.Items.Single(i => i.Kind == SettingsItemKind.Row && i.Row == row);

    public static Rectangle Part(WidgetCard card, SettingsRowId row, SettingsPart part)
    {
        SettingsItem item = Row(card, row);
        return part is SettingsPart.SegmentSecond or SettingsPart.Plus or SettingsPart.Clear ? item.B : item.A;
    }

    public static UpdateViewModel Update(UpdateStage stage, int? percent = null, string? reason = null, string? notice = null) =>
        UpdateViewModel.For(stage, new ReleaseVersion(1, 1, 0), new ReleaseVersion(1, 2, 0), percent, reason, notice);

    // The update page when there is no install to hand an update to: Update is replaced by Set up or Repair.
    public static UpdateViewModel UpdateWithoutInstall(UpdateButtonRole instead) =>
        UpdateViewModel.For(UpdateStage.Available, new ReleaseVersion(1, 1, 0), new ReleaseVersion(1, 2, 0), null, null, "Set up Earshot first, then update.", updateOffered: false, instead);
}
