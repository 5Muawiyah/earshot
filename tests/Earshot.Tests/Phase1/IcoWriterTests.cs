using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using Earshot.Icons;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase1;

[TestClass]
public sealed class IcoWriterTests
{
    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    [DataRow(256)]
    public void TheIcoHeaderDescribesOnePngFrame(int px)
    {
        byte[] png = EarbudGlyph.RenderPng(px, GlyphState.Connected, Color.White);

        byte[] ico = IcoWriter.SingleFramePng(png, px);
        ReadOnlySpan<byte> span = ico;

        Assert.AreEqual(22 + png.Length, ico.Length);
        Assert.AreEqual((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(span[0..]), "reserved");
        Assert.AreEqual((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(span[2..]), "type icon");
        Assert.AreEqual((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(span[4..]), "one image");
        Assert.AreEqual(px == 256 ? (byte)0 : (byte)px, ico[6], "width");
        Assert.AreEqual(px == 256 ? (byte)0 : (byte)px, ico[7], "height");
        Assert.AreEqual((byte)0, ico[8], "colour count");
        Assert.AreEqual((byte)0, ico[9], "reserved");
        Assert.AreEqual((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(span[10..]), "planes");
        Assert.AreEqual((ushort)32, BinaryPrimitives.ReadUInt16LittleEndian(span[12..]), "bits per pixel");
        Assert.AreEqual((uint)png.Length, BinaryPrimitives.ReadUInt32LittleEndian(span[14..]), "image size");
        Assert.AreEqual((uint)22, BinaryPrimitives.ReadUInt32LittleEndian(span[18..]), "image offset");
        CollectionAssert.AreEqual(png, ico[22..]);
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(32)]
    public void TheIcoLoadsAsAnIconOfThatSize(int px)
    {
        byte[] ico = IcoWriter.SingleFramePng(EarbudGlyph.RenderPng(px, GlyphState.Disconnected, Color.White), px);

        using var stream = new MemoryStream(ico);
        using var icon = new Icon(stream, px, px);

        Assert.AreEqual(px, icon.Width);
        Assert.AreEqual(px, icon.Height);
        Assert.AreNotEqual(0, icon.Handle);
    }

    [TestMethod]
    public void TheIconStaysUsableAfterItsStreamIsGone()
    {
        const int px = 24;
        using Icon icon = IcoWriter.Load(IcoWriter.SingleFramePng(EarbudGlyph.RenderPng(px, GlyphState.Connected, Color.White), px), px);
        using Bitmap bitmap = icon.ToBitmap();

        Assert.AreEqual(px, bitmap.Width);
    }

    [TestMethod]
    public void TheIconKeepsStraightAlpha()
    {
        const int px = 32;
        byte[] alpha = EarbudGlyph.Coverage(px, GlyphState.Disconnected);
        using Icon icon = IcoWriter.Load(IcoWriter.SingleFramePng(EarbudGlyph.RenderPng(px, GlyphState.Disconnected, Color.White), px), px);
        using Bitmap bitmap = icon.ToBitmap();

        int partial = 0;
        for (int y = 0; y < px; y++)
        {
            for (int x = 0; x < px; x++)
            {
                Color pixel = bitmap.GetPixel(x, y);
                Assert.AreEqual(alpha[(y * px) + x], pixel.A, "alpha at " + x + "," + y);
                if (pixel.A > 0)
                {
                    Assert.AreEqual(Color.White.ToArgb() & 0xFFFFFF, pixel.ToArgb() & 0xFFFFFF, "colour at " + x + "," + y);
                }

                if (pixel.A is > 0 and < 255)
                {
                    partial++;
                }
            }
        }

        Assert.IsGreaterThan(0, partial, "The glyph has antialiased edges to check.");
    }

    [TestMethod]
    public void AWhiteGlyphDrawnThroughItsIconHandleIsNotDarkenedAtItsEdges()
    {
        // Composite the icon onto opaque black through GDI, as the shell does with the HICON. With straight
        // alpha an edge pixel of coverage a comes out at about 255 * a; premultiplied data would give about
        // 255 * a * a, which is visibly darker.
        const int px = 32;
        byte[] alpha = EarbudGlyph.Coverage(px, GlyphState.Connected);
        using Icon icon = IcoWriter.Load(IcoWriter.SingleFramePng(EarbudGlyph.RenderPng(px, GlyphState.Connected, Color.White), px), px);
        using var target = new Bitmap(px, px, PixelFormat.Format24bppRgb);
        using (Graphics g = Graphics.FromImage(target))
        {
            g.Clear(Color.Black);
            g.DrawIcon(icon, new Rectangle(0, 0, px, px));
        }

        int checkedEdges = 0;
        for (int i = 0; i < alpha.Length; i++)
        {
            int a = alpha[i];
            if (a is < 64 or > 191)
            {
                continue;
            }

            Color drawn = target.GetPixel(i % px, i / px);
            int premultipliedLooksLike = a * a / 255;
            Assert.IsLessThan(4, Math.Abs(drawn.R - a), "pixel " + (i % px) + "," + (i / px) + " alpha " + a + " drew " + drawn.R);
            Assert.IsGreaterThan(premultipliedLooksLike + 8, drawn.R, "darkened edge at " + (i % px) + "," + (i / px));
            checkedEdges++;
        }

        Assert.IsGreaterThan(0, checkedEdges);
    }

    [TestMethod]
    public void OnlyPngDataAndIconSizesAreAccepted()
    {
        byte[] png = EarbudGlyph.RenderPng(16, GlyphState.Connected, Color.White);

        Assert.ThrowsExactly<ArgumentException>(() => IcoWriter.SingleFramePng([0x42, 0x4D, 0, 0, 0, 0, 0, 0], 16));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => IcoWriter.SingleFramePng(png, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => IcoWriter.SingleFramePng(png, 257));
    }
}
