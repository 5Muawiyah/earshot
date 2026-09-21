using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The checked-in pictures under src\Earshot.TestWindow\Data\pictures are drawn, not captured
// (this repository is public; a real screenshot would show the owner's other apps, his AirPods'
// own name, and Windows will not let its permission box be captured at all). This class is the
// one place that checks the files on disk still match what HowToPictureGenerator draws right now,
// so a hand-edited or stale PNG is caught rather than silently drifting from the code that is
// meant to be its only source. To refresh the checked-in files after changing the generator, run
// this same test class with EARSHOT_REGENERATE_PICTURES=1 set first: it writes the fresh PNGs
// before asserting, so the assertion passes against what it just wrote, and the files are then
// committed like any other source change.
[TestClass]
public sealed class HowToPictureGeneratorTests
{
    private static string PicturesFolder() =>
        Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "pictures");

    [TestMethod]
    public void EveryGeneratedPictureMatchesTheCheckedInFileOnDisk()
    {
        if (Environment.GetEnvironmentVariable("EARSHOT_REGENERATE_PICTURES") == "1")
        {
            Directory.CreateDirectory(PicturesFolder());
            foreach ((string name, Func<byte[]> generate) in HowToPictureGenerator.Generators)
            {
                File.WriteAllBytes(Path.Combine(PicturesFolder(), name + ".png"), generate());
            }
        }

        var mismatched = new List<string>();
        foreach ((string name, Func<byte[]> generate) in HowToPictureGenerator.Generators)
        {
            string path = Path.Combine(PicturesFolder(), name + ".png");
            byte[] fresh = generate();
            if (!File.Exists(path))
            {
                mismatched.Add(name + ": no checked-in file at " + path);
                continue;
            }

            byte[] onDisk = File.ReadAllBytes(path);

            // GDI+'s own PNG encoder is not byte-stable run to run (text anti-aliasing hinting
            // varies slightly even for identical DrawString calls), so this compares decoded
            // pixels, which is what actually reaches the screen, rather than the encoded bytes.
            if (!DecodedPixelsMatch(onDisk, fresh))
            {
                mismatched.Add(name + ": the checked-in PNG no longer matches HowToPictureGenerator's own output.");
            }
        }

        Assert.IsEmpty(mismatched, string.Join(Environment.NewLine, mismatched) +
            Environment.NewLine + "Run again with EARSHOT_REGENERATE_PICTURES=1 to refresh the checked-in files.");
    }

    private static bool DecodedPixelsMatch(byte[] a, byte[] b)
    {
        using var streamA = new MemoryStream(a);
        using var streamB = new MemoryStream(b);
        using var bitmapA = new Bitmap(streamA);
        using var bitmapB = new Bitmap(streamB);
        if (bitmapA.Width != bitmapB.Width || bitmapA.Height != bitmapB.Height)
        {
            return false;
        }

        BitmapData dataA = bitmapA.LockBits(new Rectangle(0, 0, bitmapA.Width, bitmapA.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData dataB = bitmapB.LockBits(new Rectangle(0, 0, bitmapB.Width, bitmapB.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(dataA.Stride) * dataA.Height;
            var bufferA = new byte[bytes];
            var bufferB = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(dataA.Scan0, bufferA, 0, bytes);
            System.Runtime.InteropServices.Marshal.Copy(dataB.Scan0, bufferB, 0, bytes);

            // Allow a tiny per-channel difference: sub-pixel text hinting can shift a handful of
            // edge pixels' antialiasing by a shade between otherwise identical runs, never the
            // drawing itself.
            int differing = 0;
            for (int i = 0; i < bytes; i++)
            {
                if (Math.Abs(bufferA[i] - bufferB[i]) > 24)
                {
                    differing++;
                }
            }

            return differing < bytes / 200;
        }
        finally
        {
            bitmapA.UnlockBits(dataA);
            bitmapB.UnlockBits(dataB);
        }
    }

    [TestMethod]
    public void EveryGeneratedPictureStaysUnderSixtyKilobytes()
    {
        var tooLarge = new List<string>();
        foreach ((string name, Func<byte[]> generate) in HowToPictureGenerator.Generators)
        {
            byte[] bytes = generate();
            if (bytes.Length > 60 * 1024)
            {
                tooLarge.Add(name + ": " + bytes.Length + " bytes.");
            }
        }

        Assert.IsEmpty(tooLarge, string.Join(Environment.NewLine, tooLarge));
    }
}
