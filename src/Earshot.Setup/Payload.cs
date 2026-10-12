using System.Reflection;
using System.Security.Cryptography;

namespace Earshot.Setup;

// The release zip Setup carries. Behind an interface so the check is tested against a stream that is not the real one.
internal interface IPayload
{
    // False when this build of Setup was made with no zip in it (an ordinary build, not a release).
    bool Embedded { get; }

    // The SHA-256 of the zip as the build recorded it: 64 lower-case hex characters, or empty when nothing is embedded.
    string Sha256 { get; }

    // The release version the zip holds, such as 1.5.0.
    string Version { get; }

    Stream Open();
}

internal sealed class EmbeddedPayload : IPayload
{
    public const string ResourceName = "payload.zip";

    public bool Embedded => PayloadConstants.Embedded;

    public string Sha256 => PayloadConstants.Sha256;

    public string Version => PayloadConstants.Version;

    public Stream Open() =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException("This build of Setup carries no release.");
}

internal enum PayloadVerdict
{
    Intact,
    NoPayload,
    Damaged,
}

internal sealed record PayloadCheckResult(PayloadVerdict Verdict, string ActualSha256);

// Hashes the embedded zip and compares it with the hash recorded at build. A mismatch means the file was damaged on its way
// here (or changed afterwards). The hash lives inside the same file, so it does not show that the file is the one its author
// published, or that nobody rebuilt it with another hash: it catches damage, not a hostile release or a swapped file.
internal static class PayloadCheck
{
    public static PayloadCheckResult Verify(IPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.Embedded || !IsSha256(payload.Sha256))
        {
            return new PayloadCheckResult(PayloadVerdict.NoPayload, "");
        }

        using Stream stream = payload.Open();
        string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new PayloadCheckResult(
            string.Equals(actual, payload.Sha256, StringComparison.OrdinalIgnoreCase) ? PayloadVerdict.Intact : PayloadVerdict.Damaged,
            actual);
    }

    public static bool IsSha256(string? text) =>
        text is { Length: 64 } && text.All(char.IsAsciiHexDigit);

    // A release version of the form major.minor.patch, or null.
    public static Version? ParseVersion(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Version.TryParse(text, out Version? v) && v.Major >= 0 && v.Minor >= 0 && v.Build >= 0 && v.Revision < 0
            ? v
            : null;
}
