using System.Drawing;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Earshot.App;
using Earshot.Icons;
using Earshot.Tests.Widget;
using Earshot.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Icon = Earshot.Icons.Catalogue.Icon;

namespace Earshot.Tests.Icons;

// Earshot draws no icon from a font, and no icon font is kept for any icon: the built assembly names no icon font and
// holds no private-use code point as a string or character constant, the type that held the code points is gone, and a render matrix
// of the surfaces draws icons from the catalogue only and no text in an icon font or in the private use area.
[TestClass]
public sealed class SegoeFluentAbsenceTests
{
    private static readonly int[] CardDpis = [96, 144, 192];
    private static readonly int[] GaugeDpis = [96, 120, 144, 192];
    private static readonly string[] IconFonts = ["Segoe Fluent Icons", "Segoe MDL2 Assets"];

    // The text with every character outside printable ASCII as its code point, cut at 60 characters.
    private static string Escaped(string text) => string.Concat(text.Take(60).Select(c =>
        c is >= ' ' and <= '~' ? c.ToString() : "\\u" + ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));

    // The regular-expression source generator (GeneratedRegex, used by GateStore and DoctorReport) writes each ASCII character class it
    // tests as a 128 bit table, held in an eight character string whose characters are bits and can fall anywhere in the range. These
    // are not text and draw nothing.
    private static bool IsAsciiSetTable(string text) => text.Length == 8;

    private static bool HasPrivateUse(string text) => text.Any(c => c is >= '\uE000' and <= '\uF8FF');

    private static IEnumerable<string> StringsOf(MetadataReader md)
    {
        // Every string the code loads with ldstr.
        for (UserStringHandle h = md.GetNextHandle(MetadataTokens.UserStringHandle(0)); !h.IsNil; h = md.GetNextHandle(h))
        {
            yield return md.GetUserString(h);
        }

        // Every character and string constant: fields (const) and parameter defaults.
        var constants = new List<ConstantHandle>();
        foreach (FieldDefinitionHandle fh in md.FieldDefinitions)
        {
            constants.Add(md.GetFieldDefinition(fh).GetDefaultValue());
        }

        foreach (MethodDefinitionHandle mh in md.MethodDefinitions)
        {
            foreach (ParameterHandle ph in md.GetMethodDefinition(mh).GetParameters())
            {
                constants.Add(md.GetParameter(ph).GetDefaultValue());
            }
        }

        foreach (ConstantHandle ch in constants.Where(c => !c.IsNil))
        {
            Constant constant = md.GetConstant(ch);
            BlobReader blob = md.GetBlobReader(constant.Value);
            if (constant.TypeCode == ConstantTypeCode.Char)
            {
                yield return blob.ReadChar().ToString();
            }
            else if (constant.TypeCode == ConstantTypeCode.String)
            {
                yield return blob.ReadUTF16(blob.Length);
            }
        }
    }

    // (1) The built output.
    [TestMethod]
    public void TheBuiltAssemblyNamesNoIconFontAndHoldsNoPrivateUseCodePoint()
    {
        string path = typeof(TrayContext).Assembly.Location;
        using FileStream stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        MetadataReader md = pe.GetMetadataReader();

        var named = new List<string>();
        var privateUse = new List<string>();
        int read = 0;
        foreach (string text in StringsOf(md))
        {
            read++;
            if (IconFonts.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase)))
            {
                named.Add(text);
            }

            if (HasPrivateUse(text) && !IsAsciiSetTable(text))
            {
                privateUse.Add("\"" + Escaped(text) + "\"");
            }
        }

        Assert.IsGreaterThan(1000, read, "The scan read the assembly's strings: " + read);
        Assert.IsEmpty(named, "An icon font is named: " + string.Join("; ", named));
        Assert.IsEmpty(privateUse, "A private use code point is held: " + string.Join("; ", privateUse));
    }

    // (3) The type that held the code points is gone.
    [TestMethod]
    public void TheFluentGlyphTypeNoLongerExists()
    {
        Assert.IsNull(typeof(TrayContext).Assembly.GetType("Earshot.Widget.FluentGlyphs"));
        Assert.IsFalse(
            typeof(TrayContext).Assembly.GetTypes().Any(t => t.Name.Contains("FluentGlyph", StringComparison.Ordinal)),
            "No type is named for the icon font's glyphs.");
    }

    // (2) A render matrix: the surfaces this package owns (every gauge mode and story frame, the tray) and the card's main, settings
    // and updates pages draw their icons from the catalogue, and no text they draw is in an icon font or holds a private use code point.
    [TestMethod]
    public void TheGaugeMatrixDrawsCatalogueIconsOnly()
    {
        var seen = new HashSet<Icon>();
        IconPainter.DrawnIconLog = [];
        try
        {
            GaugePalette palette = GaugePalette.Create(lightTheme: true, Color.FromArgb(0x00, 0x5F, 0xB8), highContrast: false, Color.Black);
            foreach (int dpi in GaugeDpis)
            {
                GaugeLayout layout = GaugeLayout.For(dpi);
                foreach (GaugeMode mode in Enum.GetValues<GaugeMode>())
                {
                    foreach (GaugeStoryKind kind in Enum.GetValues<GaugeStoryKind>())
                    {
                        foreach (bool charging in new[] { false, true })
                        {
                            var content = new GaugeContent(mode, mode is GaugeMode.OnOtherDevice or GaugeMode.NotOnThisPc or GaugeMode.MarkOnly ? null : 70, false, charging, "")
                            {
                                Story = new GaugeStoryView(kind, true),
                                CaseMark = mode == GaugeMode.CaseAway,
                            };
                            using Bitmap bitmap = GaugeRenderer.Render(content, palette, layout, hover: false, "Segoe UI");
                            using Bitmap preview = GaugeRenderer.RenderPreview(content, palette, layout, "Segoe UI", Color.Gray);
                        }
                    }
                }
            }

            seen.UnionWith(IconPainter.DrawnIconLog.Select(d => d.Icon));
        }
        finally
        {
            IconPainter.DrawnIconLog = null;
        }

        CollectionAssert.AreEquivalent(
            new[] { Icon.AirPods, Icon.AirPodsCase, Icon.Lightning, Icon.Cellphone, Icon.Check, Icon.Close },
            seen.ToArray(),
            "The gauge's icons are the pair, the case, the bolt, the phone, the tick and the cross.");
    }

    [TestMethod]
    public void TheCardMatrixDrawsCatalogueIconsAndNoTextInAnIconFont()
    {
        var seen = new HashSet<Icon>();
        var fonts = new HashSet<string>();
        var privateUse = new List<string>();
        Phase5.CardSta.Run(() =>
        {
            DateTimeOffset now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
            WidgetSnapshot snapshot = CardKit.Snapshot(new PartReading(70, true, null) { ReadAt = now }, new PartReading(60, false, null) { ReadAt = now }, now) with
            {
                Case = new PartReading(90, false, null) { ReadAt = now },
                Where = AirPodsWhere.ThisPc,
            };
            IconPainter.DrawnIconLog = [];
            CardPaint.DrawnTextLog = [];
            try
            {
                foreach (DesignTheme theme in DesignPixels.Themes)
                {
                    foreach (int dpi in CardDpis)
                    {
                        using WidgetCard card = DesignPixels.NewCard(theme, 1.0);
                        card.Render(CardKit.MainModel(snapshot) with { Now = now }, dpi);
                        using (CardKit.Render(card))
                        {
                        }

                        CardKit.RenderSettings(card, CardKit.SettingsModel(FakeCardHost.Defaults() with { InstallExists = true }), dpi, more: true, order: true);
                        using (CardKit.Render(card))
                        {
                        }

                        card.Render(CardKit.UpdateModelWithRows(CardKit.Update(UpdateStage.Idle), autoCheck: true, showRepair: true, version: "1.7.1"), dpi);
                        using (CardKit.Render(card))
                        {
                        }
                    }
                }

                seen.UnionWith(IconPainter.DrawnIconLog.Select(d => d.Icon));
                foreach (DrawnText text in CardPaint.DrawnTextLog)
                {
                    fonts.Add(text.FontFamily);
                    if (HasPrivateUse(text.Text))
                    {
                        privateUse.Add(text.Text);
                    }
                }
            }
            finally
            {
                IconPainter.DrawnIconLog = null;
                CardPaint.DrawnTextLog = null;
            }
        });

        Assert.IsEmpty(fonts.Where(f => IconFonts.Any(i => f.Contains(i, StringComparison.OrdinalIgnoreCase))), "Text drawn in an icon font: " + string.Join(", ", fonts));
        Assert.IsEmpty(privateUse, "Text with a private use code point: " + string.Join(", ", privateUse));
        Assert.IsGreaterThan(0, fonts.Count, "The card drew text, so the check looked at something.");

        // What these pages must show, from the table of icons each page uses: the title row, the status row, the marks, the settings rows and
        // the updates rows.
        Icon[] required =
        [
            Icon.Settings, Icon.Refresh, Icon.Computer, Icon.Send, Icon.AirPods, Icon.AirPodsCase, Icon.Back,
            Icon.LayoutBottom, Icon.Monitor, Icon.Transfer, Icon.Cellphone, Icon.Notification, Icon.Keyboard, Icon.History,
            Icon.Copy, Icon.More, Icon.Pause, Icon.Mouse, Icon.Power, Icon.MicOff, Icon.Lightning, Icon.Sync, Icon.Information, Icon.Tool,
            Icon.Bluetooth, Icon.ExternalLink, Icon.ChevronRight,
        ];
        Icon[] missing = required.Where(i => !seen.Contains(i)).ToArray();
        Assert.IsEmpty(missing, "These icons were not drawn by the card matrix: " + string.Join(", ", missing));
    }
}
