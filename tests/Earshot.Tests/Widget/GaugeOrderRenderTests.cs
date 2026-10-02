using System.Drawing;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// All six orders drawn at 100%, 125% and 150%: the bitmap is the window's own fixed size, nothing is painted outside
// the padding, and the ring, the number and the bolt never run into one another.
[TestClass]
public sealed class GaugeOrderRenderTests
{
    private const string FontFamily = "Segoe UI";
    private static readonly int[] Dpis = [96, 120, 144];
    private static readonly Color Accent = Color.FromArgb(12, 160, 88);

    // The size the gauge window has at each scale, whatever the order.
    private static readonly Dictionary<int, Size> WindowSizes = new() { [96] = new Size(74, 40), [120] = new Size(93, 50), [144] = new Size(111, 60) };

    private static Bitmap Draw(GaugeContent content, int dpi, GaugeOrder order, bool light = true)
    {
        GaugePalette palette = GaugePalette.Create(light, Accent, highContrast: false, light ? Color.Black : Color.White);
        return GaugeRenderer.Render(content, palette, GaugeLayout.For(dpi, order), hover: false, FontFamily);
    }

    private static GaugeContent Full() => new(GaugeMode.Reading, 100, Low: false, Charging: true, "");

    private static bool Painted(Color pixel) => pixel.A >= 24;

    // The first and last columns with ink inside a rectangle, or null.
    private static (int First, int Last)? Columns(Bitmap bitmap, Rectangle area)
    {
        int first = -1;
        int last = -1;
        for (int x = Math.Max(0, area.Left); x < Math.Min(bitmap.Width, area.Right); x++)
        {
            for (int y = Math.Max(0, area.Top); y < Math.Min(bitmap.Height, area.Bottom); y++)
            {
                if (Painted(bitmap.GetPixel(x, y)))
                {
                    first = first < 0 ? x : first;
                    last = x;
                    break;
                }
            }
        }

        return first < 0 ? null : (first, last);
    }

    [TestMethod]
    public void EveryOrderIsTheWindowsFixedSizeAndPaintsNothingInItsPadding()
    {
        foreach (int dpi in Dpis)
        {
            foreach (GaugeOrder order in Enum.GetValues<GaugeOrder>())
            {
                GaugeLayout slots = GaugeLayout.For(dpi, order);
                int leftPad = Math.Min(slots.RingBox.Left, Math.Min(slots.NumberSlot.Left, slots.ChargingSlot.Left));
                int rightPad = slots.Width - Math.Max(slots.RingBox.Right, Math.Max(slots.NumberSlot.Right, slots.ChargingSlot.Right));
                foreach (GaugeContent content in new[]
                {
                    Full(),
                    new GaugeContent(GaugeMode.OnOtherDevice, null, false, false, ""),
                    new GaugeContent(GaugeMode.MarkOnly, null, false, false, ""),
                    new GaugeContent(GaugeMode.NotOnThisPc, null, false, false, ""),
                })
                {
                    using Bitmap bitmap = Draw(content, dpi, order);
                    string where = " (" + order + ", " + content.Mode + ", dpi " + dpi + ")";
                    Assert.AreEqual(WindowSizes[dpi], bitmap.Size, "The window is its fixed size" + where);
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            if (Painted(bitmap.GetPixel(x, y)))
                            {
                                Assert.IsTrue(x >= leftPad && x < bitmap.Width - rightPad, "Ink at column " + x + " is inside the padding" + where);
                            }
                        }
                    }
                }
            }
        }
    }

    [TestMethod]
    public void TheRingTheNumberAndTheBoltNeverTouchWhateverTheOrder()
    {
        foreach (int dpi in Dpis)
        {
            foreach (GaugeOrder order in Enum.GetValues<GaugeOrder>())
            {
                GaugeLayout layout = GaugeLayout.For(dpi, order);
                using Bitmap bitmap = Draw(Full(), dpi, order);
                string where = " (" + order + ", dpi " + dpi + ")";

                (int First, int Last)? ring = Columns(bitmap, layout.RingBox);
                (int First, int Last)? number = Columns(bitmap, layout.NumberSlot);
                (int First, int Last)? bolt = Columns(bitmap, layout.ChargingSlot);
                Assert.IsNotNull(ring, "The ring is drawn" + where);
                Assert.IsNotNull(number, "The number is drawn" + where);
                Assert.IsNotNull(bolt, "The bolt is drawn" + where);

                var ranges = new List<(string Name, int First, int Last)>
                {
                    ("ring", ring.Value.First, ring.Value.Last),
                    ("number", number.Value.First, number.Value.Last),
                    ("bolt", bolt.Value.First, bolt.Value.Last),
                };
                ranges.Sort((a, b) => a.First.CompareTo(b.First));
                for (int i = 0; i + 1 < ranges.Count; i++)
                {
                    Assert.IsGreaterThanOrEqualTo(
                        ranges[i].Last + 2, ranges[i + 1].First,
                        ranges[i].Name + " and " + ranges[i + 1].Name + " are at least one clear pixel apart" + where);
                }

                // And in the order the layout names.
                (GaugePiece first, GaugePiece second, GaugePiece third) = GaugeOrders.Sequence(order);
                string[] expected = [first.ToString().ToLowerInvariant(), second.ToString().ToLowerInvariant(), third.ToString().ToLowerInvariant()];
                CollectionAssert.AreEqual(expected, ranges.Select(r => r.Name).ToArray(), "Left to right" + where);
            }
        }
    }

    // The gauge's number is the type ramp's gauge style (Segoe UI Variable Small where installed): "100" must still fit
    // the 20 px slot at every scale, in any order.
    [TestMethod]
    public void ThreeDigitsInTheRampsGaugeFaceFitTheNumberSlotAtEveryScale()
    {
        string family = TypeRamp.FamilyFor(TypeRole.Gauge);
        foreach (int dpi in Dpis)
        {
            GaugeLayout layout = GaugeLayout.For(dpi);
            float cell = GaugeRenderer.DigitCell(family, layout.TypePixels);
            Assert.IsLessThanOrEqualTo(layout.NumberSlot.Width, (int)Math.Ceiling(cell * 3), family + " at " + dpi + " dpi: cell " + cell);
        }
    }

    // The phone and the digits are centred in the number slot, whatever the order: the ink's middle is within two pixels of the
    // slot's middle.
    [TestMethod]
    public void ThePhoneMarkAndTheDigitsAreCentredInTheNumberSlot()
    {
        foreach (int dpi in Dpis)
        {
            foreach (GaugeOrder order in Enum.GetValues<GaugeOrder>())
            {
                GaugeLayout layout = GaugeLayout.For(dpi, order);
                Rectangle slot = layout.NumberSlot;
                foreach (GaugeContent content in new[] { new GaugeContent(GaugeMode.OnOtherDevice, null, false, false, ""), Full() })
                {
                    using Bitmap bitmap = Draw(content, dpi, order);
                    (int First, int Last)? ink = Columns(bitmap, slot);
                    Assert.IsNotNull(ink, content.Mode + " is drawn in the number slot (" + order + ", dpi " + dpi + ")");
                    Assert.IsTrue(ink.Value.First >= slot.Left && ink.Value.Last < slot.Right, "Inside the slot (" + order + ", dpi " + dpi + ")");
                    double middle = (ink.Value.First + ink.Value.Last + 1) / 2.0;
                    Assert.AreEqual(slot.Left + (slot.Width / 2.0), middle, 2.0, content.Mode + " centred (" + order + ", dpi " + dpi + ")");
                }
            }
        }
    }
}
