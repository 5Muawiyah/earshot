using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Forms;
using Earshot.Contracts;
using Earshot.Streaming;
using Earshot.Tests.Phase1;
using Earshot.Tests.Phase5;
using Earshot.Tests.TestWindow;
using Earshot.Tests.Widget;
using Earshot.Tray;
using Earshot.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Icons;

// Nothing about size, position or words changes while the icons are replaced. This pins what the 1.7.0 code lays out and draws
// before any drawing change: every rectangle of the card's layouts, the gauge's layout for every order, the picker's and the
// sub-page frame's, every rectangle and text the real card records as it renders each of its pages (the same pages as
// CardTextFitTests, plus the compact rows), the controls a screen reader and a tooltip see on each, and the tray menu's items
// in order with their Text, Checked and Enabled. The glyph a part draws (a char today, an icon later) is not part of it.
//
// The file is Baseline\layout-1.7.0.json, written once on the unchanged code with EARSHOT_REGENERATE_BASELINE=1 and committed
// first. Every package after must leave the test green. The tray menu's check margin, image size and width are properties of the
// strip, which the document does not read, so no difference is allowed.
//
// The text sections depend on the fonts of the PC (the type ramp's Segoe UI Variable instances and the icon fonts the 1.7.0 card
// asked for): they are compared only where the same fonts are installed, and the test is inconclusive, after the pure layout
// sections have been compared, where they are not.
[TestClass]
public sealed class LayoutBaselineTests
{
    private const string Regenerate = "EARSHOT_REGENERATE_BASELINE";

    private static readonly int[] Dpis = [96, 120, 144, 192];
    private static readonly double[] TextScales = [1.0, 1.5];

    // A fixed instant: ages ("5 seconds ago") and the history days must read the same on every run.
    private static readonly DateTimeOffset Fixed = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static string BaselinePath() =>
        Path.Combine(RepositoryLocator.RepositoryRoot(), "tests", "Earshot.Tests", "Icons", "Baseline", "layout-1.7.0.json");

    [TestMethod]
    public void NoSizePositionOrWordHasChangedSince170()
    {
        SortedDictionary<string, List<string>> fresh = Build();
        if (Environment.GetEnvironmentVariable(Regenerate) == "1")
        {
            File.WriteAllText(BaselinePath(), Serialise(fresh), new UTF8Encoding(false));
        }

        SortedDictionary<string, List<string>> baseline = Parse(File.ReadAllText(BaselinePath()));

        // The sections that do not depend on a font.
        AssertSame(baseline, fresh, key => !key.StartsWith("render/", StringComparison.Ordinal));

        string wanted = baseline["environment"].Single();
        string present = fresh["environment"].Single();
        if (wanted != present)
        {
            Assert.Inconclusive("The rendered text is pinned for the fonts the baseline was taken with (" + wanted + "); this PC has " + present + ".");
        }

        AssertSame(baseline, fresh, key => key.StartsWith("render/", StringComparison.Ordinal));
    }

    private static void AssertSame(
        SortedDictionary<string, List<string>> baseline, SortedDictionary<string, List<string>> fresh, Func<string, bool> include)
    {
        string[] keys = baseline.Keys.Concat(fresh.Keys).Where(include).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.IsNotEmpty(keys);
        foreach (string key in keys)
        {
            Assert.IsTrue(baseline.ContainsKey(key), "The baseline has no section " + key + ".");
            Assert.IsTrue(fresh.ContainsKey(key), "The code no longer produces the section " + key + ".");
            List<string> was = baseline[key];
            List<string> now = fresh[key];
            for (int i = 0; i < Math.Max(was.Count, now.Count); i++)
            {
                string a = i < was.Count ? was[i] : "(no line)";
                string b = i < now.Count ? now[i] : "(no line)";
                if (a != b)
                {
                    Assert.Fail("Section " + key + ", line " + i + " differs.\nbaseline: " + a + "\nnow:      " + b);
                }
            }
        }
    }

    // ---- The file

    private static string Serialise(SortedDictionary<string, List<string>> sections)
    {
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var sb = new StringBuilder();
        sb.Append("{\n");
        int n = 0;
        foreach ((string key, List<string> lines) in sections)
        {
            sb.Append("  ").Append(JsonSerializer.Serialize(key, options)).Append(": [\n");
            for (int i = 0; i < lines.Count; i++)
            {
                sb.Append("    ").Append(JsonSerializer.Serialize(lines[i], options)).Append(i + 1 < lines.Count ? ",\n" : "\n");
            }

            sb.Append("  ]").Append(++n < sections.Count ? ",\n" : "\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    private static SortedDictionary<string, List<string>> Parse(string json)
    {
        var result = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonProperty property in doc.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
        }

        return result;
    }

    // ---- The document

    private static SortedDictionary<string, List<string>> Build()
    {
        var sections = new SortedDictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["environment"] = [FontsPresent()],
            ["layout/card"] = CardLayouts(),
            ["layout/compact"] = CompactLayouts(),
            ["layout/gauge"] = GaugeLayouts(),
            ["layout/send-to"] = SendToLayouts(),
            ["layout/frame"] = FrameLayouts(),
            ["menu"] = MenuLines(),
        };
        foreach (KeyValuePair<string, List<string>> page in RenderedPages())
        {
            sections["render/" + page.Key] = page.Value;
        }

        return sections;
    }

    private static string FontsPresent()
    {
        using var fonts = new InstalledFontCollection();
        string[] present = fonts.Families
            .Select(f => f.Name)
            .Where(n => n.StartsWith("Segoe UI Variable", StringComparison.Ordinal) || n is "Segoe Fluent Icons" or "Segoe MDL2 Assets" or "Segoe UI")
            .Order(StringComparer.Ordinal)
            .ToArray();
        return string.Join(", ", present);
    }

    private static List<string> CardLayouts()
    {
        var lines = new List<string>();
        (string Name, bool Switch, bool Gear, bool Update, int UpdateWidth, bool Refresh, bool SendTo, int StatusLines)[] shapes =
        [
            ("plain", false, true, false, 0, true, false, 1),
            ("everything", true, true, true, 0, true, true, 1),
            ("case-open", false, false, false, 0, false, false, 1),
            ("switch, three lines", true, true, false, 0, false, true, 3),
            ("update, wide button, two lines", false, true, true, 90, true, false, 2),
        ];
        foreach (int dpi in Dpis)
        {
            foreach (double t in TextScales)
            {
                foreach (var s in shapes)
                {
                    WidgetCardLayout.Layout layout = WidgetCardLayout.Compute(
                        dpi, s.Switch, s.Gear, s.Update, s.UpdateWidth, s.Refresh, t, s.SendTo, s.StatusLines);
                    lines.Add(Key(dpi, t) + " " + s.Name + ": " + Dump(layout));
                }
            }
        }

        return lines;
    }

    private static List<string> CompactLayouts()
    {
        var lines = new List<string>();
        foreach (int dpi in Dpis)
        {
            foreach (double t in TextScales)
            {
                foreach (bool glyph in new[] { true, false })
                {
                    foreach (WidgetCardLayout.CompactButton button in Enum.GetValues<WidgetCardLayout.CompactButton>())
                    {
                        int actionWidth = button == WidgetCardLayout.CompactButton.Text ? 90 : 0;
                        int width = WidgetCardLayout.CompactTextWidth(dpi, t, glyph, button, actionWidth);
                        for (int n = 1; n <= WidgetCardLayout.CompactMaxLines; n++)
                        {
                            WidgetCardLayout.CompactLayout layout = WidgetCardLayout.ForCompact(dpi, t, n, glyph, button, actionWidth);
                            lines.Add(Key(dpi, t) + " glyph " + glyph + " " + button + " lines " + n + " wrap " + width + ": " + Dump(layout));
                        }
                    }
                }
            }
        }

        return lines;
    }

    private static List<string> GaugeLayouts()
    {
        var lines = new List<string>();
        foreach (int dpi in Dpis)
        {
            foreach (GaugeOrder order in Enum.GetValues<GaugeOrder>())
            {
                lines.Add("dpi " + dpi + " " + order + ": " + Dump(GaugeLayout.For(dpi, order)));
            }
        }

        return lines;
    }

    private static List<string> SendToLayouts()
    {
        var lines = new List<string>();
        foreach (int dpi in Dpis)
        {
            foreach (double t in TextScales)
            {
                foreach (int rows in new[] { 1, 3, 5 })
                {
                    lines.Add(Key(dpi, t) + " rows " + rows + ": " + Dump(SendToLayout.Compute(dpi, rows, t)));
                }
            }
        }

        return lines;
    }

    private static List<string> FrameLayouts()
    {
        var lines = new List<string>();
        foreach (int dpi in Dpis)
        {
            foreach (double t in TextScales)
            {
                for (int buttons = 0; buttons <= 2; buttons++)
                {
                    lines.Add(Key(dpi, t) + " buttons " + buttons + ": " + Dump(SubPageFrame.Compute(dpi, 300, buttons, t)));
                }
            }
        }

        return lines;
    }

    // ---- The tray menu

    private static readonly StreamingMenuModel PlayingFromAPhone = new(
        StreamingLabels.Parent,
        ParentEnabled: true,
        [
            new StreamingMenuItem("Test Phone", Enabled: true, Checked: true, "phone-id", StreamingMenuCommand.Play),
            StreamingMenuModel.Sentence("Looking for devices."),
            new StreamingMenuItem("Stop playing from Test Phone", Enabled: true, Checked: false, "phone-id", StreamingMenuCommand.Stop),
            new StreamingMenuItem(StreamingLabels.Refresh, Enabled: true, Checked: false, null, StreamingMenuCommand.Refresh),
        ]);

    private static List<string> MenuLines()
    {
        var lines = new List<string>();
        StaThread.Run(() =>
        {
            BootBlockStatus block = Phase1Fixtures.Block(BlockState.NotSetUp);
            (string Name, MenuState State)[] states =
            [
                ("not set up", MenuModel.Build(Phase1Fixtures.NoDevice(), block, null, Phase1Fixtures.Settings(), false, StartupState.Off)),
                ("play from a phone", MenuModel.Build(Phase1Fixtures.NoDevice(), block, null, Phase1Fixtures.Settings(), false, StartupState.Off, streaming: PlayingFromAPhone)),
                ("safe mode, voice missing", MenuModel.Build(Phase1Fixtures.NoDevice(), block, null, Phase1Fixtures.Settings(), false, StartupState.Off, safeMode: true, voiceKnownMissing: true)),
                ("busy", MenuModel.Build(Phase1Fixtures.NoDevice(), block, null, Phase1Fixtures.Settings(), true, StartupState.Off)),
                ("connected", MenuModel.Build(Phase1Fixtures.Target(ConnectionState.Connected), block, null, Phase1Fixtures.Settings(), false, StartupState.Off)),
            ];
            foreach ((string name, MenuState state) in states)
            {
                MenuState captured = state;
                using var menu = new TrayMenu(() => captured);
                int index = 0;
                foreach (ToolStripItem item in menu.Items)
                {
                    string kind = item is ToolStripSeparator ? "separator" : "item";
                    string checkState = item is ToolStripMenuItem m ? m.CheckState.ToString() : "-";
                    lines.Add(name + " #" + index + " " + kind + " available " + item.Available + " enabled " + item.Enabled + " check " + checkState + " text \"" + item.Text + "\"");
                    if (item is ToolStripMenuItem parent)
                    {
                        int child = 0;
                        foreach (ToolStripMenuItem sub in parent.DropDownItems.OfType<ToolStripMenuItem>())
                        {
                            lines.Add(name + " #" + index + "." + child++ + " enabled " + sub.Enabled + " check " + sub.CheckState + " text \"" + sub.Text + "\"");
                        }
                    }

                    index++;
                }
            }
        });
        return lines;
    }

    // ---- The rendered pages

    private static WidgetSnapshot Heard(TimeSpan age)
    {
        DateTimeOffset at = Fixed - age;
        return CardKit.Snapshot(new PartReading(70, true, null) { ReadAt = at }, new PartReading(60, false, null) { ReadAt = at }, at) with
        {
            Case = new PartReading(90, false, null) { ReadAt = at },
        };
    }

    private static IEnumerable<(string Name, bool Notice, Action<WidgetCard, int> Show)> Pages()
    {
        foreach (TimeSpan age in new[] { TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(4), TimeSpan.FromHours(30) })
        {
            TimeSpan captured = age;
            yield return ("main, reading " + age, false, (card, dpi) => card.Render(CardKit.MainModel(Heard(captured), updateVersion: "1.4.0") with { ShowSwitch = true, AutoPauseOn = true, Now = Fixed }, dpi));
            yield return ("case-open, reading " + age, true, (card, dpi) => card.Render(CardKit.MainModel(Heard(captured)) with { Now = Fixed }, dpi));
        }

        foreach (BatteryRefreshOutcome outcome in new[] { BatteryRefreshOutcome.NothingHeard, BatteryRefreshOutcome.BluetoothOff })
        {
            BatteryRefreshOutcome captured = outcome;
            yield return ("main, " + outcome, false, (card, dpi) => card.Render(CardKit.MainModel(Heard(TimeSpan.FromSeconds(5))) with { Refresh = new BatteryRefreshView(false, 0, captured), Now = Fixed }, dpi));
        }

        yield return ("main, nothing heard", false, (card, dpi) => card.Render(CardKit.MainModel(CardKit.Snapshot()) with { Now = Fixed }, dpi));
        yield return ("main, on another device", false, (card, dpi) => card.Render(CardKit.MainModel(Heard(TimeSpan.FromSeconds(5)) with { Where = AirPodsWhere.Elsewhere }) with { Now = Fixed }, dpi));
        yield return ("settings, everything open", false, (card, dpi) =>
            CardKit.RenderSettings(
                card,
                CardKit.SettingsModel(FakeCardHost.Defaults() with { InstallExists = true, InstalledVersion = "1.4.0", ConnectFailure = "Ctrl+Alt+Shift+A is already in use by another program, so it was not set. Windows reported error 1409." }),
                dpi, more: true, order: true));
        yield return ("settings, closed", false, (card, dpi) =>
            CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults()), dpi, more: false, order: false));

        foreach (UpdateStage stage in Enum.GetValues<UpdateStage>())
        {
            UpdateStage captured = stage;
            int? percent = stage == UpdateStage.Downloading ? 60 : null;
            string? reason = stage is UpdateStage.CheckFailed or UpdateStage.DownloadFailed or UpdateStage.HandoverFailed ? "The server did not answer in time." : null;
            yield return ("update flow, " + stage, false, (card, dpi) => card.Render(CardKit.UpdateModel(CardKit.Update(captured, percent, reason)), dpi));
            yield return ("updates page, " + stage, false, (card, dpi) => card.Render(CardKit.UpdateModelWithRows(CardKit.Update(captured, percent, reason), autoCheck: true, showRepair: true, version: "1.4.0"), dpi));
        }

        yield return ("history, today", false, (card, dpi) => card.Render(CardKit.HistoryModel(HistoryDays.View(0, Fixed, TimeZoneInfo.Utc, null)), dpi));
        yield return ("history, two days back", false, (card, dpi) => card.Render(CardKit.HistoryModel(HistoryDays.View(2, Fixed, TimeZoneInfo.Utc, null)), dpi));

        foreach ((string name, CompactContent content) in new (string, CompactContent)[]
        {
            ("compact, moved to the iPhone", CompactContents.MovedTo("iPhone")),
            ("compact, moved to another device", CompactContents.MovedTo(string.Empty)),
            ("compact, low battery", CompactContents.LowBattery("Left", 20)),
            ("compact, fully charged", CompactContents.FullyCharged()),
        })
        {
            CompactContent captured = content;
            yield return (name, true, (card, dpi) => card.Render(CardKit.MainModel() with { Form = CardForm.Compact, Compact = captured, Now = Fixed }, dpi));
        }
    }

    private static SortedDictionary<string, List<string>> RenderedPages()
    {
        var result = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach ((string name, bool notice, Action<WidgetCard, int> show) in Pages())
        {
            foreach (int dpi in Dpis)
            {
                foreach (double t in TextScales)
                {
                    var lines = new List<string>();
                    CardSta.Run(() =>
                    {
                        using WidgetCard card = DesignPixels.NewCard(DesignTheme.Light, t, notice);
                        CardPaint.DrawnTextLog = [];
                        CardPaint.DrawnTabularLog = [];
                        try
                        {
                            show(card, dpi);
                            using Bitmap bitmap = CardKit.Render(card);
                            lines.Add("client " + Dump(card.ClientSize) + " bitmap " + bitmap.Width + "x" + bitmap.Height);
                            lines.Add("main " + Dump(card.CurrentMainLayout));
                            if (card.CurrentSetupLayout is { } setup)
                            {
                                lines.Add("setup " + Dump(setup));
                            }

                            if (card.CurrentSettingsLayout is { } settings)
                            {
                                lines.Add("settings frame " + Dump(settings.Frame));
                                lines.AddRange(settings.Items.Select((item, i) => "settings row " + i + " " + Dump(item)));
                                lines.AddRange(settings.Targets.Select((target, i) => "settings target " + i + " " + Dump(target)));
                                lines.AddRange(settings.Surfaces.Select((surface, i) => "settings surface " + i + " " + Dump(surface)));
                            }

                            if (card.CurrentCompactLayout is { } compact)
                            {
                                lines.Add("compact " + Dump(compact));
                            }

                            if (card.CurrentStripLayout is { } strip)
                            {
                                lines.Add("strip " + Dump(strip));
                            }

                            lines.AddRange(card.CurrentControls().Select((c, i) => "control " + i + " " + Dump(c)));
                            lines.AddRange(CardPaint.DrawnTextLog.Select((d, i) => "text " + i + " " + Dump(d)));
                            lines.AddRange(CardPaint.DrawnTabularLog.Select((d, i) => "figure " + i + " " + Dump(d)));
                        }
                        finally
                        {
                            CardPaint.DrawnTextLog = null;
                            CardPaint.DrawnTabularLog = null;
                        }
                    });
                    result[name + "/" + Key(dpi, t)] = lines;
                }
            }
        }

        return result;
    }

    // ---- The dump

    private static string Key(int dpi, double textScale) => "dpi " + dpi + " text " + textScale.ToString("0.00", CultureInfo.InvariantCulture);

    // A value as one line of text: numbers, rectangles, enums, strings, lists and the public properties of a record, in
    // declaration order. A property of type char (the glyph a part draws) and a delegate (what a press does) are left out.
    private static string Dump(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object? value, int depth)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                sb.Append('"').Append(s).Append('"');
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case float f:
                sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                return;
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            case Rectangle r:
                sb.Append('[').Append(r.X).Append(',').Append(r.Y).Append(',').Append(r.Width).Append('x').Append(r.Height).Append(']');
                return;
            case RectangleF rf:
                sb.Append('[').Append(rf.X.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(rf.Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(rf.Width.ToString("R", CultureInfo.InvariantCulture)).Append('x').Append(rf.Height.ToString("R", CultureInfo.InvariantCulture)).Append(']');
                return;
            case Size sz:
                sb.Append(sz.Width).Append('x').Append(sz.Height);
                return;
            case Point p:
                sb.Append(p.X).Append(',').Append(p.Y);
                return;
            case Enum e:
                sb.Append(e);
                return;
            case IFormattable formattable when value.GetType().IsPrimitive:
                sb.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            case System.Collections.IEnumerable list:
                sb.Append('[');
                bool first = true;
                foreach (object? item in list)
                {
                    sb.Append(first ? string.Empty : "; ");
                    first = false;
                    Write(sb, item, depth + 1);
                }

                sb.Append(']');
                return;
        }

        if (depth > 8)
        {
            sb.Append("...");
            return;
        }

        sb.Append('{');
        bool firstProperty = true;
        foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || property.PropertyType == typeof(char) || typeof(Delegate).IsAssignableFrom(property.PropertyType))
            {
                continue;
            }

            sb.Append(firstProperty ? string.Empty : ", ").Append(property.Name).Append('=');
            firstProperty = false;
            Write(sb, property.GetValue(value), depth + 1);
        }

        sb.Append('}');
    }
}
