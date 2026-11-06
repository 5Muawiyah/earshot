using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Earshot.Tests.TestWindow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Icons;

// The icon files in third_party\MingCute are the publisher's, byte for byte: their SHA-256 and git object id are those the manifest
// records, the folder holds those files and the licence and nothing else, and git is told not to touch their line ends (with
// text=auto a checkout on Windows would turn the LF of an SVG into CRLF and every hash would fail).
[TestClass]
public sealed class VendoredFilesTests
{
    internal const string LicenceSha256 = "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30";
    internal const string LicenceBlobId = "d645695673349e3947e8e5ae42332d0ac3164cd7";
    internal const string Commit = "9dc7d3ad074fb16477096a6a8109a6467d0e2fe3";
    private const string UpstreamPrefix = "assets/svg/core/";

    internal static string Folder() => Path.Combine(RepositoryLocator.RepositoryRoot(), "third_party", "MingCute");

    internal static JsonDocument Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder(), "manifest.json")));

    // Upstream path "assets/svg/core/regular/system/close.svg" is kept at svg\core\regular\system\close.svg.
    internal static string LocalPath(string upstream)
    {
        Assert.IsTrue(upstream.StartsWith(UpstreamPrefix, StringComparison.Ordinal), upstream);
        return Path.Combine(Folder(), "svg", "core", upstream[UpstreamPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // The git object id of a file: SHA-1 of "blob <length>", a zero byte, then the bytes. It names the file in git's store and
    // protects nothing, so it is computed here (FIPS 180-4) rather than with the platform's SHA1, which the analyser flags as weak.
    internal static string BlobId(byte[] bytes)
    {
        byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
        return Sha1Hex([.. header, .. bytes]);
    }

    internal static string Sha1Hex(byte[] message)
    {
        uint h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476, h4 = 0xC3D2E1F0;
        long bitLength = (long)message.Length * 8;
        int paddedLength = (((message.Length + 8) / 64) + 1) * 64;
        byte[] padded = new byte[paddedLength];
        message.CopyTo(padded, 0);
        padded[message.Length] = 0x80;
        for (int i = 0; i < 8; i++)
        {
            padded[paddedLength - 1 - i] = (byte)(bitLength >> (8 * i));
        }

        uint[] w = new uint[80];
        for (int block = 0; block < paddedLength; block += 64)
        {
            for (int t = 0; t < 16; t++)
            {
                int o = block + (t * 4);
                w[t] = ((uint)padded[o] << 24) | ((uint)padded[o + 1] << 16) | ((uint)padded[o + 2] << 8) | padded[o + 3];
            }

            for (int t = 16; t < 80; t++)
            {
                w[t] = uint.RotateLeft(w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16], 1);
            }

            uint a = h0, b = h1, c = h2, d = h3, e = h4;
            for (int t = 0; t < 80; t++)
            {
                uint f, k;
                if (t < 20)
                {
                    f = (b & c) | (~b & d);
                    k = 0x5A827999;
                }
                else if (t < 40)
                {
                    f = b ^ c ^ d;
                    k = 0x6ED9EBA1;
                }
                else if (t < 60)
                {
                    f = (b & c) | (b & d) | (c & d);
                    k = 0x8F1BBCDC;
                }
                else
                {
                    f = b ^ c ^ d;
                    k = 0xCA62C1D6;
                }

                uint temp = uint.RotateLeft(a, 5) + f + e + k + w[t];
                e = d;
                d = c;
                c = uint.RotateLeft(b, 30);
                b = a;
                a = temp;
            }

            h0 += a;
            h1 += b;
            h2 += c;
            h3 += d;
            h4 += e;
        }

        return string.Concat(new[] { h0, h1, h2, h3, h4 }.Select(h => h.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)));
    }

    // FIPS 180-4's own example: SHA-1("abc"). The blob id of the empty file is a fact every git repository shares.
    [TestMethod]
    public void TheBlobIdIsGitsOwn()
    {
        Assert.AreEqual("a9993e364706816aba3e25717850c26c9cd0d89d", Sha1Hex(Encoding.ASCII.GetBytes("abc")));
        Assert.AreEqual("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", BlobId([]));
    }

    [TestMethod]
    public void TheManifestNamesTheSourceTheCommitAndTheLicence()
    {
        using JsonDocument manifest = Manifest();
        JsonElement root = manifest.RootElement;
        Assert.AreEqual("https://github.com/mingcute-design/mingcute-icons", root.GetProperty("source").GetString());
        Assert.AreEqual(Commit, root.GetProperty("commit").GetString());
        Assert.AreEqual("3.1.0", root.GetProperty("version").GetString());
        Assert.AreEqual("Apache-2.0", root.GetProperty("licence").GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("noticeFile").ValueKind, "The repository has no NOTICE file.");
        Assert.AreEqual(LicenceSha256, root.GetProperty("licenceFile").GetProperty("sha256").GetString());
        Assert.AreEqual(LicenceBlobId, root.GetProperty("licenceFile").GetProperty("gitObjectId").GetString());
        Assert.AreEqual(69, root.GetProperty("files").GetArrayLength());
    }

    [TestMethod]
    public void EveryListedFileIsTheDownloadedOneByteForByte()
    {
        using JsonDocument manifest = Manifest();
        foreach (JsonElement file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            string upstream = file.GetProperty("path").GetString()!;
            string path = LocalPath(upstream);
            Assert.IsTrue(File.Exists(path), upstream + " is not vendored.");
            byte[] bytes = File.ReadAllBytes(path);
            Assert.AreEqual(file.GetProperty("sha256").GetString(), Sha256(bytes), upstream + ": SHA-256");
            Assert.AreEqual(file.GetProperty("gitObjectId").GetString(), BlobId(bytes), upstream + ": git object id");
            Assert.DoesNotContain((byte)'\r', bytes, upstream + " holds a carriage return: git changed its line ends.");
        }
    }

    [TestMethod]
    public void TheLicenceIsTheApacheTextByteForByte()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Folder(), "LICENSE"));
        Assert.AreEqual(LicenceSha256, Sha256(bytes));
        Assert.AreEqual(LicenceBlobId, BlobId(bytes));
        Assert.DoesNotContain((byte)'\r', bytes, "The licence holds a carriage return: git changed its line ends.");
    }

    [TestMethod]
    public void TheFolderHoldsTheListedFilesTheLicenceAndTheManifestAndNothingElse()
    {
        using JsonDocument manifest = Manifest();
        string folder = Folder();
        string[] expected = manifest.RootElement.GetProperty("files").EnumerateArray()
            .Select(f => LocalPath(f.GetProperty("path").GetString()!))
            .Append(Path.Combine(folder, "LICENSE"))
            .Append(Path.Combine(folder, "manifest.json"))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] actual = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void GitIsToldToLeaveTheLineEndsOfTheVendoredFilesAlone()
    {
        string attributes = File.ReadAllText(Path.Combine(RepositoryLocator.RepositoryRoot(), ".gitattributes"));
        string[] lines = attributes.Split('\n').Select(l => l.Trim()).ToArray();
        CollectionAssert.Contains(lines, "third_party/MingCute/LICENSE -text");
        CollectionAssert.Contains(lines, "third_party/MingCute/svg/** -text");
    }
}
