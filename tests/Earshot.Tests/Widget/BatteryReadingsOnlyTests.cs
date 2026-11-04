using Earshot.Tests.TestWindow;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// Every battery value is a reading: live, or the last one with its age. Nothing is grown from a charging reading, however
// long ago it was read and wherever the pair is. These go through the snapshot, the one way every surface reads the book.
[TestClass]
public sealed class BatteryReadingsOnlyTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    // 0x2027 is the model the seed for growing a charging bud was written for, so the old code would have grown these.
    private const ushort Model = 0x2027;

    private static PartReading Charging(int percent) => new(percent, true, null) { ReadAt = ReadAt };

    private static WidgetSnapshot Away(AirPodsWhere where)
    {
        LastReadingBook book = LastReadingBook.Empty.WithReading(
            new DecodedReading(Charging(40), Charging(45), Charging(50), null, null), Model, ReadAt);
        return WidgetSnapshot.Empty(WidgetWatcherState.Started) with { Where = where, LastReadings = book, PairedModel = Model };
    }

    [TestMethod]
    public void AChargingLastReadingHoursOldAwayIsShownAsItWasRead()
    {
        foreach (TimeSpan age in new[] { TimeSpan.FromMinutes(5), TimeSpan.FromHours(2), TimeSpan.FromHours(5), TimeSpan.FromDays(3) })
        {
            ShownBattery shown = BatteryFreshness.Shown(Away(AirPodsWhere.Unknown), ReadAt + age);

            Assert.AreEqual(40, shown.Left.Percent, "Left at " + age);
            Assert.AreEqual(ReadingKind.Last, shown.Left.Kind, "Left at " + age);
            Assert.AreEqual(45, shown.Right.Percent, "Right at " + age);
            Assert.AreEqual(ReadingKind.Last, shown.Right.Kind, "Right at " + age);
            Assert.AreEqual(50, shown.Case.Percent, "Case at " + age);
            Assert.AreEqual(ReadingKind.Last, shown.Case.Kind, "Case at " + age);
            Assert.AreEqual(ReadAt, shown.Left.ReadAt, "The read time stays the reading's own.");
        }
    }

    [TestMethod]
    public void TheSameHoldsOnThisPcAndWhereTheyAreOnAnotherDevice()
    {
        foreach (AirPodsWhere where in new[] { AirPodsWhere.ThisPc, AirPodsWhere.Elsewhere, AirPodsWhere.NotInUse })
        {
            ShownBattery shown = BatteryFreshness.Shown(Away(where), ReadAt + TimeSpan.FromHours(6));

            Assert.AreEqual(40, shown.Left.Percent, where.ToString());
            Assert.AreEqual(ReadingKind.Last, shown.Left.Kind, where.ToString());
            Assert.AreEqual(50, shown.Case.Percent, where.ToString());
            Assert.AreEqual(ReadingKind.Last, shown.Case.Kind, where.ToString());
        }
    }

    [TestMethod]
    public void TheGaugeAwayDrawsTheCaseReadingNotAGrownOne()
    {
        GaugeContent content = GaugeContent.From(Away(AirPodsWhere.Unknown), ReadAt + TimeSpan.FromHours(5), GaugeDisplaySettings.Default);

        Assert.AreEqual(GaugeMode.CaseAway, content.Mode);
        Assert.AreEqual(50, content.Percent);
        Assert.IsTrue(content.Tertiary);
    }

    [TestMethod]
    public void NoWordsOrMarksOfAnEstimateAreInAnySurfaceText()
    {
        DateTimeOffset now = ReadAt + TimeSpan.FromHours(5);
        foreach (AirPodsWhere where in new[] { AirPodsWhere.Unknown, AirPodsWhere.ThisPc, AirPodsWhere.Elsewhere })
        {
            WidgetSnapshot snapshot = Away(where);
            ShownBattery shown = BatteryFreshness.Shown(snapshot, now);
            var texts = new List<string>
            {
                GaugeContent.From(snapshot, now, GaugeDisplaySettings.Default).Tooltip,
                GaugeSpeech.Value(snapshot, now, GaugeDisplaySettings.Default),
                WidgetCopy.CaseOpenAnnouncement(shown, now),
            };
            foreach (ShownPart part in new[] { shown.Left, shown.Right, shown.Case })
            {
                texts.Add(WidgetCopy.PartLine(part, now));
                texts.Add(WidgetCopy.ColumnTip("Left", part, now) ?? "");
                texts.Add(WidgetCopy.PartTip(part, now) ?? "");
                texts.Add(WidgetCopy.SpokenReading("Left", part, now));
            }

            foreach (string text in texts)
            {
                StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex("≈|[Ee]stimat|about"), where + ": " + text);
            }
        }
    }

    // The approximate mark is gone from every string the copy holds and from every gauge look. Reflection over WidgetCopy
    // covers the constants and the fixed words; the surface texts above cover what is worked out.
    [TestMethod]
    public void NoApproximateMarkIsInWidgetCopyOrInAnyGaugeMode()
    {
        foreach (System.Reflection.FieldInfo field in typeof(WidgetCopy).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
        {
            if (field.FieldType == typeof(string) && field.GetValue(null) is string value)
            {
                Assert.IsFalse(value.Contains('≈', StringComparison.Ordinal), field.Name);
            }
        }

        DateTimeOffset now = ReadAt + TimeSpan.FromHours(5);
        foreach (GaugeContent content in new[]
        {
            GaugeContent.From(Away(AirPodsWhere.Unknown), now, GaugeDisplaySettings.Default),
            GaugeContent.From(Away(AirPodsWhere.ThisPc), now, GaugeDisplaySettings.Default),
            GaugeContent.From(Away(AirPodsWhere.Elsewhere), now, GaugeDisplaySettings.Default),
            GaugeContent.From(WidgetSnapshot.Empty(WidgetWatcherState.Started), now, GaugeDisplaySettings.Default),
        })
        {
            Assert.IsFalse(content.Tooltip.Contains('≈', StringComparison.Ordinal), content.Mode.ToString());
        }
    }

    // Done means none of it is left in the program: the estimator, the charge rates, the mark. EstimatedSize is a registry
    // value Windows names for the Installed apps entry, nothing to do with a battery.
    [TestMethod]
    public void NoEstimateRateOrApproximateMarkRemainsInSource()
    {
        string src = Path.Combine(RepositoryLocator.RepositoryRoot(), "src");
        var found = new List<string>();
        var pattern = new System.Text.RegularExpressions.Regex(@"≈|\bEstimat(e|ed|or|ion)\w*|ChargeRate\w*|HighWater|UseAppleBudSeed");
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (System.Text.RegularExpressions.Match match in pattern.Matches(File.ReadAllText(file)))
            {
                if (match.Value != "EstimatedSize")
                {
                    found.Add(Path.GetRelativePath(src, file) + ": " + match.Value);
                }
            }
        }

        Assert.IsEmpty(found, string.Join("; ", found.Distinct()));
    }
}
