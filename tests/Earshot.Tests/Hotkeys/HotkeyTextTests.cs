using Earshot.Hotkeys;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Hotkeys;

[TestClass]
public sealed class HotkeyTextTests
{
    [TestMethod]
    public void ParseCtrlAltPGivesControlAltAndVirtualKey0x50()
    {
        Assert.IsTrue(HotkeyText.TryParse("Ctrl+Alt+P", out HotkeyCombination combination, out string error));
        Assert.AreEqual(new HotkeyCombination(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x50), combination);
        Assert.AreEqual(HotkeyModifiers.Control | HotkeyModifiers.Alt, combination.Modifiers);
        Assert.AreEqual((ushort)0x50, combination.VirtualKey);
        Assert.AreEqual(string.Empty, error);
    }

    [TestMethod]
    public void ParseIgnoresCaseAndSpaces()
    {
        var expected = new HotkeyCombination(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x50);
        foreach (string text in new[] { "ctrl + ALT + p", "CTRL+alt+P", " Ctrl+Alt+P " })
        {
            Assert.IsTrue(HotkeyText.TryParse(text, out HotkeyCombination combination, out _), text);
            Assert.AreEqual(expected, combination, text);
        }
    }

    [TestMethod]
    public void ParseAcceptsModifierAndKeyAliases()
    {
        Assert.IsTrue(HotkeyText.TryParse("Control+Alt+Esc", out HotkeyCombination esc, out _));
        Assert.AreEqual(HotkeyModifiers.Control | HotkeyModifiers.Alt, esc.Modifiers);
        Assert.AreEqual((ushort)0x1B, esc.VirtualKey);

        Assert.IsTrue(HotkeyText.TryParse("Windows+Shift+Del", out HotkeyCombination del, out _));
        Assert.AreEqual(HotkeyModifiers.Windows | HotkeyModifiers.Shift, del.Modifiers);
        Assert.AreEqual((ushort)0x2E, del.VirtualKey);

        Assert.IsTrue(HotkeyText.TryParse("Win+PgDn", out HotkeyCombination pgdn, out _));
        Assert.AreEqual(HotkeyModifiers.Windows, pgdn.Modifiers);
        Assert.AreEqual((ushort)0x22, pgdn.VirtualKey);
    }

    [TestMethod]
    public void ParseRejectsKeyWithoutModifier()
    {
        foreach (string text in new[] { "P", "F5" })
        {
            Assert.IsFalse(HotkeyText.TryParse(text, out HotkeyCombination combination, out string error), text);
            Assert.AreEqual(HotkeyText.NoModifierMessage, error, text);
            Assert.AreEqual(default, combination, text);
        }
    }

    [TestMethod]
    public void ParseRejectsModifiersOnly()
    {
        foreach (string text in new[] { "Ctrl", "Ctrl+Alt" })
        {
            Assert.IsFalse(HotkeyText.TryParse(text, out _, out string error), text);
            Assert.AreEqual(HotkeyText.NoKeyMessage, error, text);
        }
    }

    [TestMethod]
    public void ParseRejectsEmptyInput()
    {
        foreach (string? text in new[] { null, "", "   " })
        {
            Assert.IsFalse(HotkeyText.TryParse(text, out HotkeyCombination combination, out string error));
            Assert.AreEqual(HotkeyText.EmptyInputMessage, error);
            Assert.AreEqual(default, combination);
        }
    }

    [TestMethod]
    public void ParseRejectsUnknownKeyName()
    {
        Assert.IsFalse(HotkeyText.TryParse("Ctrl+Banana", out _, out string error));
        Assert.AreEqual("There is no key called \"Banana\".", error);
    }

    [TestMethod]
    public void ParseRejectsEmptySegments()
    {
        foreach (string text in new[] { "Ctrl++P", "Ctrl+P+", "+P" })
        {
            Assert.IsFalse(HotkeyText.TryParse(text, out _, out string error), text);
            Assert.AreEqual(HotkeyText.EmptySegmentMessage, error, text);
        }
    }

    [TestMethod]
    public void ParseRejectsRepeatedModifier()
    {
        Assert.IsFalse(HotkeyText.TryParse("Ctrl+Ctrl+P", out _, out string error));
        Assert.AreEqual("\"Ctrl\" is listed twice.", error);

        Assert.IsFalse(HotkeyText.TryParse("ctrl+CTRL+P", out _, out string secondError));
        Assert.IsFalse(string.IsNullOrEmpty(secondError));
    }

    [TestMethod]
    public void ParseRejectsTwoKeys()
    {
        Assert.IsFalse(HotkeyText.TryParse("Ctrl+P+Q", out _, out string error));
        Assert.AreEqual(HotkeyText.TwoKeysMessage, error);
    }

    [TestMethod]
    public void ParseRejectsKeyBeforeModifier()
    {
        Assert.IsFalse(HotkeyText.TryParse("Ctrl+P+Alt", out _, out string error));
        Assert.AreEqual(HotkeyText.KeyNotLastMessage, error);
    }

    [TestMethod]
    public void FormatUsesFixedModifierOrder()
    {
        var combination = new HotkeyCombination(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows, 0x50);
        Assert.AreEqual("Ctrl+Alt+Shift+Win+P", HotkeyText.Format(combination));

        Assert.IsTrue(HotkeyText.TryParse("Shift+Ctrl+P", out HotkeyCombination parsed, out _));
        Assert.AreEqual("Ctrl+Shift+P", HotkeyText.Format(parsed));
    }

    [TestMethod]
    public void RoundTripEveryCanonicalKeyNameSurvives()
    {
        Assert.IsTrue(VirtualKeyTable.KeyNames.Count >= 80, "The key table must not be empty for this test to mean anything.");

        foreach (string name in VirtualKeyTable.KeyNames)
        {
            string text = "Ctrl+" + name;
            Assert.IsTrue(HotkeyText.TryParse(text, out HotkeyCombination combination, out string error), name + ": " + error);
            Assert.AreEqual(text, HotkeyText.Format(combination), name);
        }
    }

    [TestMethod]
    public void FormatThrowsForUnnamedKey()
    {
        var combination = new HotkeyCombination(HotkeyModifiers.Control, 0x07);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => HotkeyText.Format(combination));

        Assert.IsFalse(HotkeyText.TryFormat(combination, out string text));
        Assert.AreEqual(string.Empty, text);
    }
}
