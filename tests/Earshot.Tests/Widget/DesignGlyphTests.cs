using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The Segoe Fluent Icons code points the design lists, each by name. Pure: the constants, not the font.
[TestClass]
public sealed class DesignGlyphTests
{
    // The design's list, by the name the code gives each: name, then the code point the design states.
    private static readonly (string Name, int CodePoint)[] Listed =
    [
        ("Refresh", 0xE72C), ("Settings", 0xE713), ("Back", 0xE72B), ("Bolt", 0xE945), ("ReadTime", 0xE823), ("OnThisPc", 0xE977),
        ("Warning", 0xE7BA), ("BluetoothOff", 0xE702), ("CellPhone", 0xE8EA), ("TvMonitor", 0xE7F4), ("Sort", 0xE8CB), ("Preview", 0xE8A0),
        ("MicOff", 0xEC54), ("Volume", 0xE767), ("Keyboard", 0xE765), ("Sync", 0xE895), ("WhatsNew", 0xE946), ("OpenExternal", 0xE8A7),
        ("ChevronRight", 0xE76C), ("ChevronDown", 0xE70D), ("ChevronUp", 0xE70E),
    ];

    [TestMethod]
    public void EveryCodePointTheDesignListsIsTheOneTheCodeUses()
    {
        foreach ((string name, int expected) in Listed)
        {
            System.Reflection.FieldInfo? field = typeof(FluentGlyphs).GetField(name);
            Assert.IsNotNull(field, name + " is a named code point.");
            Assert.AreEqual(expected, (char)field.GetRawConstantValue()!, name);
        }
    }

    [TestMethod]
    public void TheSettingsRowsUseTheDesignsIcons()
    {
        Assert.AreEqual(FluentGlyphs.TvMonitor, SettingsRows.For(SettingsRowId.GaugeDisplay).Glyph);
        Assert.AreEqual(FluentGlyphs.Sort, SettingsRows.For(SettingsRowId.GaugeOrder).Glyph);
        Assert.AreEqual(FluentGlyphs.Preview, SettingsRows.For(SettingsRowId.CaseCard).Glyph);
        Assert.AreEqual(FluentGlyphs.MicOff, SettingsRows.For(SettingsRowId.MicrophoneOff).Glyph);
        Assert.AreEqual(FluentGlyphs.Volume, SettingsRows.For(SettingsRowId.SoundSettings).Glyph);
        Assert.AreEqual(FluentGlyphs.Keyboard, SettingsRows.For(SettingsRowId.Connect).Glyph);
        Assert.AreEqual(FluentGlyphs.Keyboard, SettingsRows.For(SettingsRowId.Disconnect).Glyph);
        Assert.AreEqual(FluentGlyphs.Keyboard, SettingsRows.For(SettingsRowId.OpenCard).Glyph);
        Assert.AreEqual(FluentGlyphs.Sync, SettingsRows.For(SettingsRowId.About).Glyph);
        Assert.AreEqual(FluentGlyphs.Sync, SettingsRows.For(SettingsRowId.CheckAutomatically).Glyph);
    }
}
