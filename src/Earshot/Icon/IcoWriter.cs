using System.Buffers.Binary;

namespace Earshot.Icons;

// Wraps one PNG image in a single-frame ICO file held in memory.
//
// Layout, little-endian (ICONDIR, then one ICONDIRENTRY, then the image):
//   0  u16 reserved = 0
//   2  u16 type     = 1 (icon)
//   4  u16 count    = 1
//   6  u8  width    (0 means 256)
//   7  u8  height   (0 means 256)
//   8  u8  colours  = 0
//   9  u8  reserved = 0
//  10  u16 planes   = 1
//  12  u16 bits per pixel = 32
//  14  u32 image size in bytes
//  18  u32 image offset = 22
//  22  PNG bytes
// https://learn.microsoft.com/en-us/previous-versions/ms997538(v=msdn.10)
//
// Loading the result with new Icon(stream, px, px) keeps the PNG's straight alpha, so antialiased
// edges are composited correctly. The icon stays valid after the stream is disposed.
internal static class IcoWriter
{
    public const int DirectorySize = 6;
    public const int EntrySize = 16;
    public const int ImageOffset = DirectorySize + EntrySize;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] SingleFramePng(ReadOnlySpan<byte> png, int px)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(px, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(px, 256);
        if (!png.StartsWith(PngSignature))
        {
            throw new ArgumentException("The image is not a PNG.", nameof(png));
        }

        var ico = new byte[ImageOffset + png.Length];
        Span<byte> span = ico;
        BinaryPrimitives.WriteUInt16LittleEndian(span[0..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);
        span[6] = (byte)(px == 256 ? 0 : px);
        span[7] = (byte)(px == 256 ? 0 : px);
        span[8] = 0;
        span[9] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], ImageOffset);
        png.CopyTo(span[ImageOffset..]);
        return ico;
    }

    // Loads a single-frame ICO. The caller disposes the icon.
    public static Icon Load(byte[] ico, int px)
    {
        ArgumentNullException.ThrowIfNull(ico);
        using var stream = new MemoryStream(ico, writable: false);
        return new Icon(stream, px, px);
    }
}
