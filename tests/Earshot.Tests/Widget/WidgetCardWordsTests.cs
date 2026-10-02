using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Icons over words: a settings row is an icon and one to three words, with what used to be its description in the
// tooltip, and the card says less in the same places. Every icon has a name a screen reader can say.
[TestClass]
public sealed class WidgetCardWordsTests
{
    private static readonly int[] Dpis = [96, 120, 144];

    [TestMethod]
    public void EverySettingsRowIsAnIconAndOneToThreeWordsWithNoDescriptionUnderIt()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { InstallExists = true }), 96);

            foreach (SettingsItem row in card.CurrentSettingsLayout!.Items.Where(i => i.Kind == SettingsItemKind.Row))
            {
                int words = row.Label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                Assert.IsTrue(words is >= 1 and <= 3, row.Row + " says \"" + row.Label + "\", " + words + " words.");
                Assert.IsNull(row.Sub, row.Row + ": nothing under the label unless something is wrong.");
                Assert.AreNotEqual('\0', row.Glyph, row.Row + " has an icon.");
                Assert.IsFalse(row.IconRect.IsEmpty, row.Row + " has a place for it.");
                Assert.IsTrue(row.Bounds.Contains(row.IconRect), row.Row + ": the icon is inside its row.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(row.Tip), row.Row + " has a tooltip.");
                Assert.IsFalse(string.IsNullOrWhiteSpace(row.AccessibleName), row.Row + " has an accessible name.");
                Assert.IsLessThanOrEqualTo(row.LabelRect.Left, row.IconRect.Right, row.Row + ": the icon is to the left of the label.");
            }
        });
    }

    [TestMethod]
    public void ARowsIconIsDrawnBesideItsLabelWhenTheFontIsThere()
    {
        if (FluentGlyphs.Family is null)
        {
            Assert.Inconclusive("No icon font is installed on this machine.");
        }

        Phase5.CardSta.Run(() =>
        {
            foreach (int dpi in Dpis)
            {
                using WidgetCard card = CardKit.NewCard(dark: false);
                card.Render(CardKit.SettingsModel(FakeCardHost.Defaults() with { InstallExists = true }), dpi);
                using Bitmap bitmap = CardKit.Render(card);
                Color background = bitmap.GetPixel(0, 0);
                foreach (SettingsItem row in card.CurrentSettingsLayout!.Items.Where(i => i.Kind == SettingsItemKind.Row))
                {
                    Assert.IsTrue(CardKit.HasInk(bitmap, row.IconRect, background), row.Row + ": the icon is drawn at " + dpi + " dpi.");
                }
            }
        });
    }

    [TestMethod]
    public void TheMainViewDrawsItsWhereReadAndUpdateLinesWithIcons()
    {
        if (FluentGlyphs.Family is null)
        {
            Assert.Inconclusive("No icon font is installed on this machine.");
        }

        Phase5.CardSta.Run(() =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.Render(CardKit.MainModel(CardKit.Snapshot(readAt: now - TimeSpan.FromMinutes(2)), updateVersion: "1.3.0") with { Now = now }, 96);
            using Bitmap bitmap = CardKit.Render(card);
            Color background = bitmap.GetPixel(0, 0);
            WidgetCardLayout.Layout layout = card.CurrentMainLayout;
            int box = Earshot.Popup.CardPlacement.Scale(16, 96);

            foreach (Rectangle line in new[] { layout.WhereLine, layout.ReadLine, layout.UpdateCaption })
            {
                Assert.IsTrue(CardKit.HasInk(bitmap, new Rectangle(line.X, line.Y, box, line.Height), background), "An icon at the start of " + line);
                Assert.IsTrue(CardKit.HasInk(bitmap, new Rectangle(line.X + box + 8, line.Y, line.Width - box - 8, line.Height), background), "And words after it, " + line);
            }
        });
    }

    [TestMethod]
    public void TheReadLineIsJustTheAgeAndTheUpdateLineJustTheVersion()
    {
        DateTimeOffset now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.AreEqual("Not read yet", WidgetCopy.ReadAge(null, now));
        Assert.AreEqual("", WidgetCopy.ReadAge(now - TimeSpan.FromSeconds(30), now), "Fresh: no age is shown.");
        Assert.AreEqual("under 1 min ago", WidgetCopy.ReadAge(now - TimeSpan.FromSeconds(31), now));
        Assert.AreEqual("2 min ago", WidgetCopy.ReadAge(now - TimeSpan.FromMinutes(2), now));
        Assert.AreEqual("3 h ago", WidgetCopy.ReadAge(now - TimeSpan.FromHours(3), now));
        Assert.AreEqual("", WidgetCopy.ReadAge(now + TimeSpan.FromMinutes(5), now), "A reading from the future is fresh, never a negative age.");
        Assert.AreEqual("Battery read 2 min ago", WidgetCopy.BatteryReadLine(now - TimeSpan.FromMinutes(2), now), "The whole sentence stays, for the tooltip.");
        Assert.AreEqual("1.3.0 available", WidgetCopy.UpdateAvailableShort("1.3.0"));
        Assert.AreEqual("Version 1.3.0 is available", WidgetCopy.UpdateAvailable("1.3.0"));
    }

    [TestMethod]
    public void NoTextOnTheCardOrItsSettingsHasAnEmDashOrAnAmericanSpelling()
    {
        foreach (System.Reflection.FieldInfo field in typeof(WidgetCopy).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.IsLiteral && field.GetRawConstantValue() is string text)
            {
                Assert.IsFalse(text.Contains((char)0x2014), field.Name + " has an em dash.");
                Assert.IsFalse(text.Contains("color", StringComparison.OrdinalIgnoreCase), field.Name + " is spelt the American way.");
            }
        }
    }
}
