using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// What the card draws goes to the window as one copy of pixels already drawn, never as a clear followed by drawing on the window
// itself, which shows the backdrop alone for as long as the drawing takes (a flash), and the copy is exactly what the direct draw
// made. The surface here is the kind the window is painted on: a 32-bit device context, drawn on through Graphics.FromHdc.
[TestClass]
public sealed class WidgetCardBufferedPaintTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPels;
        public int YPels;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    // The bytes of a 32-bit top-down surface of size after paint drew on it through Graphics.FromHdc, from a surface filled with
    // 0xFF first so that a pixel nothing wrote shows.
    private static byte[] PaintOnSurface(Size size, Action<Graphics> paint)
    {
        var info = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
        nint dc = CreateCompatibleDC(0);
        nint dib = CreateDIBSection(dc, ref info, 0, out nint bits, 0, 0);
        nint old = SelectObject(dc, dib);
        try
        {
            var bytes = new byte[size.Width * size.Height * 4];
            Array.Fill(bytes, (byte)0xFF);
            Marshal.Copy(bytes, 0, bits, bytes.Length);
            using (Graphics g = Graphics.FromHdc(dc))
            {
                paint(g);
            }

            Marshal.Copy(bits, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(dib);
            DeleteDC(dc);
        }
    }

    // What the card draws goes to the window as one copy of pixels already drawn, and those pixels are exactly what drawing on
    // the window itself made. Both are painted here onto the same kind of surface, a 32-bit device context, over a transparent
    // clear (the backdrop's case), and compared byte for byte.
    [TestMethod]
    public void WhatIsPaintedIsOneCopyOfFinishedPixelsIdenticalToTheDirectDraw()
    {
        Phase5.CardSta.Run(() =>
        {
            foreach (bool dark in CardKit.Themes)
            {
                using WidgetCard card = CardKit.NewCard(dark);
                card.OverrideBackgroundForCaptureOnly = Color.FromArgb(0, 0, 0, 0);
                WidgetSnapshot snapshot = CardKit.Snapshot(new PartReading(70, true, null), new PartReading(55, false, null), DateTimeOffset.UtcNow);
                card.Render(CardKit.MainModel(snapshot), 96);
                Size size = card.ClientSize;

                byte[] direct = PaintOnSurface(size, g => card.RenderContent(g));
                byte[] buffered = PaintOnSurface(size, g => card.PaintBuffered(g, new Rectangle(Point.Empty, size)));

                CollectionAssert.AreEqual(direct, buffered, (dark ? "Dark" : "Light") + ": the copied pixels differ from the drawn ones.");
                Assert.AreEqual(1, card.PaintCounts.Blits);
            }
        });
    }

    // A part of the window copied is only that part: the rest of the surface is untouched.
    [TestMethod]
    public void ACopyOfPartOfTheCardLeavesTheRestOfTheSurfaceAlone()
    {
        Phase5.CardSta.Run(() =>
        {
            using WidgetCard card = CardKit.NewCard(dark: false);
            card.OverrideBackgroundForCaptureOnly = Color.FromArgb(0, 0, 0, 0);
            card.Render(CardKit.MainModel(), 96);
            Size size = card.ClientSize;
            var part = new Rectangle(10, 10, 80, 30);

            byte[] surface = PaintOnSurface(size, g => card.PaintBuffered(g, part));

            for (int y = 0; y < size.Height; y++)
            {
                for (int x = 0; x < size.Width; x++)
                {
                    if (!part.Contains(x, y))
                    {
                        int o = ((y * size.Width) + x) * 4;
                        Assert.AreEqual(0xFF, surface[o], "Pixel " + x + "," + y + " outside the part was written.");
                        Assert.AreEqual(0xFF, surface[o + 3]);
                    }
                }
            }
        });
    }

    // A real WM_PAINT of a card on screen goes through the copy and never draws on the window's own surface: the one place the
    // window's pixels are written is PaintBuffered, and it counts each.
    [TestMethod]
    public void APaintOfACardOnScreenIsOneCopyOfAFrameDrawnApart()
    {
        Phase5.CardDesktop.Run(() =>
        {
            var log = new CapturingLog();
            using var card = new WidgetCard(log);
            card.SetTheme(Color.Black, highContrast: false);
            card.Render(CardKit.MainModel(), 96);
            card.Show();
            Application.DoEvents();
            PaintStatistics before = card.PaintCounts;

            card.Invalidate();
            card.Update();

            PaintStatistics after = card.PaintCounts;
            Assert.AreEqual(before.Blits + 1, after.Blits, "The paint was one copy.");
            Assert.AreEqual(before.Frames + 1, after.Frames, "Of one frame drawn into a bitmap.");
        });
    }
}
