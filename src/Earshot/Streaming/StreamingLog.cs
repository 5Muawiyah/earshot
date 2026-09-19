using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Earshot.Contracts;

namespace Earshot.Streaming;

// What this feature writes to disk about a device: a short key in the log, and nothing anywhere else. The id
// Windows gives is an interface path that embeds the device's Bluetooth address, so it is never written. A plain
// hash of it would not do either: the rest of the path can be guessed, so every address could be tried until one
// hashed to the key. The key is therefore an HMAC of the id under a secret made afresh each time Earshot starts,
// held in memory only and written nowhere. Without it a key cannot be worked back to a device, by anyone, from
// the log alone or from the log and the settings together.
//
// The price is that a key means something only within one run: the same phone has a different key tomorrow. That
// is all the log needs, which is to tell one device from another while reading one session. A secret kept in the
// settings file would have made keys last across runs, and would have sat beside the log in the same profile for
// whoever reads both, which protects less than it seems to. Nothing else wanted a lasting key: the one use was
// the order of a menu that usually holds one or two phones, and that is now remembered for the run only.
// https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.hmacsha256.hashdata
internal static class StreamingLog
{
    private const int KeyLength = 12;

    private static readonly byte[] SecretForThisRun = RandomNumberGenerator.GetBytes(32);

    public static string Key(string deviceId) => Key(deviceId, SecretForThisRun);

    // The same key for the same id and secret. The secret is a parameter so a test can show it matters.
    internal static string Key(string deviceId, ReadOnlySpan<byte> secret)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        return Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(deviceId)))[..KeyLength];
    }

    // One step, as a log line: the step, its code by name and number, and the fixed detail word.
    public static string Describe(StepOutcome step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Step + ": " + step.CodeName + " (0x" + step.Code.ToString("X8", CultureInfo.InvariantCulture) + ")" +
            (string.IsNullOrEmpty(step.Detail) ? "" : " " + step.Detail);
    }
}
