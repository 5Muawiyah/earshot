using System.Security.Cryptography;
using Earshot.Tracing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Tracing;

// The recording profile Earshot bundles. It is Microsoft's file, taken from github.com/microsoft/busiotools
// (bluetooth/tracing/BluetoothStack.wprp, branch master, commit 7f68a48cd21306c6b0519728ec311a0f66479aff), so a changed
// copy has to be a decision, not an accident.
[TestClass]
public sealed class TraceProfileTests
{
    // The file as Microsoft publishes it (CRLF). Line endings are read as CRLF before hashing, so a checkout that holds LF matches too.
    private const string PinnedSha256 = "d69c983384b970e8c0380c48ebebba547f94c4a84c9f4912a10798b6b08cf704";

    [TestMethod]
    public void TheBundledProfileIsTheFileThatWasReviewed()
    {
        byte[] embedded = TraceProfile.ReadEmbedded();

        Assert.AreEqual(PinnedSha256, TraceProfile.HashOf(embedded));
        Assert.IsTrue(TraceProfile.IsExpected(embedded));
    }

    [TestMethod]
    public void AChangedProfileFailsTheCheck()
    {
        byte[] changed = TraceProfile.ReadEmbedded();
        changed[changed.Length / 2] ^= 0x01;

        Assert.IsFalse(TraceProfile.IsExpected(changed));
    }

    [TestMethod]
    public void TheCheckIgnoresLineEndings()
    {
        string text = System.Text.Encoding.UTF8.GetString(TraceProfile.ReadEmbedded()).Replace("\r\n", "\n");
        byte[] lf = System.Text.Encoding.UTF8.GetBytes(text);

        Assert.IsFalse(lf.Contains((byte)0x0D));
        Assert.IsTrue(TraceProfile.IsExpected(lf));
    }

    [TestMethod]
    public void TheFileOnDiskAndTheEmbeddedCopyAreTheSameFile()
    {
        string onDisk = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Earshot", "Trace", "BluetoothStack.wprp"));
        if (!File.Exists(onDisk))
        {
            Assert.Inconclusive("The source tree is not beside the test assembly.");
        }

        Assert.AreEqual(PinnedSha256, TraceProfile.HashOf(File.ReadAllBytes(onDisk)));
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(onDisk))),
            Convert.ToHexString(SHA256.HashData(TraceProfile.ReadEmbedded())));
    }

    [TestMethod]
    public void TheProfileNamesTheVerboseRecordingTheCommandStarts()
    {
        string text = System.Text.Encoding.UTF8.GetString(TraceProfile.ReadEmbedded());

        // wpr.exe names a profile "<Name>.<DetailLevel>"; the file declares a Verbose profile called BluetoothStack that
        // logs to a file.
        string[] parts = TraceProfile.ProfileId.Split('.');
        Assert.AreEqual(2, parts.Length);
        Assert.IsTrue(text.Contains("Name=\"" + parts[0] + "\" DetailLevel=\"" + parts[1] + "\"", StringComparison.Ordinal));
        Assert.IsTrue(text.Contains("Id=\"" + TraceProfile.ProfileId + ".File\" LoggingMode=\"File\"", StringComparison.Ordinal));
    }
}
