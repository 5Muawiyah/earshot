using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Earshot.Contracts;

namespace Earshot.Streaming;

// What this feature writes to disk about a device. The id Windows gives is an interface path that carries
// hardware identifiers, so neither the log nor the settings file ever holds it: both hold this short, stable
// hash instead, which is enough to tell one device from another and to find the same one again.
internal static class StreamingLog
{
    private const int KeyLength = 12;

    public static string Key(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)))[..KeyLength];
    }

    // One step, as a log line: the step, its code by name and number, and the fixed detail word.
    public static string Describe(StepOutcome step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Step + ": " + step.CodeName + " (0x" + step.Code.ToString("X8", CultureInfo.InvariantCulture) + ")" +
            (string.IsNullOrEmpty(step.Detail) ? "" : " " + step.Detail);
    }
}
