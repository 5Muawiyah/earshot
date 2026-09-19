using System.Globalization;
using System.Text;

namespace Earshot.Streaming;

// The words on the menu items the owner can click. They name a command, so none ends with a full stop.
// Anything the owner reads as a statement is a sentence and lives in StreamingCopy.
internal static class StreamingLabels
{
    public const string Parent = "Play from a phone";
    public const string Refresh = "Refresh the list";
    public const string StopFormat = "Stop playing from {0}";

    private static readonly CompositeFormat StopComposite = CompositeFormat.Parse(StopFormat);

    public static string Stop(string deviceName) =>
        string.Format(CultureInfo.InvariantCulture, StopComposite, StreamingCopy.SafeName(deviceName));
}
