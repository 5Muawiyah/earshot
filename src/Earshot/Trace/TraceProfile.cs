using System.Reflection;
using System.Security.Cryptography;

namespace Earshot.Tracing;

// The Windows Performance Recorder profile Earshot bundles, as an embedded resource.
//
// It is Microsoft's file, unmodified: github.com/microsoft/busiotools, bluetooth/tracing/BluetoothStack.wprp, branch
// master, last changed by commit 7f68a48cd21306c6b0519728ec311a0f66479aff (committed 2026-07-24). The repository's licence is
// MIT, "Copyright (c) Microsoft Corporation"; its notice is in THIRD-PARTY-NOTICES.txt. The SHA-256 below is pinned by a
// test and checked again before every recording, so a changed copy is a decision and not an accident.
// https://github.com/microsoft/busiotools/blob/master/bluetooth/tracing/BluetoothStack.wprp
// https://github.com/microsoft/busiotools/blob/master/LICENSE
internal static class TraceProfile
{
    public const string ResourceName = "Earshot.Trace.BluetoothStack.wprp";

    // The recording to start. "BluetoothStack.Verbose.File" is the id the file's own comments use, and wpr.exe refuses it
    // with 0xc5600611 on the owner's PC; the profile id without the suffix, with -filemode, started (25 October 2026, by hand,
    // as recorded in verification.md).
    public const string ProfileId = "BluetoothStack.Verbose";

    public const string SourceCommit = "7f68a48cd21306c6b0519728ec311a0f66479aff";

    // SHA-256 of the file as Microsoft publishes it, with CRLF line endings. HashOf reads every line ending as CRLF first, so a
    // checkout that turned them into LF (the repository's text=auto rule can) still matches.
    public const string Sha256 = "d69c983384b970e8c0380c48ebebba547f94c4a84c9f4912a10798b6b08cf704";

    public static byte[] ReadEmbedded()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The Bluetooth recording profile is not embedded in this build.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public static string HashOf(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var crlf = new List<byte>(bytes.Length + 2048);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0D && i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
            {
                continue;
            }

            if (bytes[i] == 0x0A)
            {
                crlf.Add(0x0D);
            }

            crlf.Add(bytes[i]);
        }

        return Convert.ToHexString(SHA256.HashData(crlf.ToArray())).ToLowerInvariant();
    }

    public static bool IsExpected(byte[] bytes) => string.Equals(HashOf(bytes), Sha256, StringComparison.Ordinal);
}
