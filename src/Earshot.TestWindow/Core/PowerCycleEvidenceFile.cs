using System.Text.Json;

namespace Earshot.TestWindow.Core;

// All rows and the verdict go to gui-power-cycle.json before the second half starts. Written
// additively (gui- prefix), never overwriting a harness file. EvidenceStore.TryReadPowerCycleVerdict
// reads the top-level "verdict" member back with exactly these spellings.
internal static class PowerCycleEvidenceFile
{
    internal static void Write(string folder, string rawEvidenceJson, PowerCycleVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(rawEvidenceJson);

        string verdictText = verdict switch
        {
            PowerCycleVerdict.PowerDown => "power-down",
            PowerCycleVerdict.Restart => "restart",
            PowerCycleVerdict.NotYet => "not-yet",
            _ => "unknown",
        };

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("verdict", verdictText);
            writer.WritePropertyName("evidence");
            try
            {
                using JsonDocument document = JsonDocument.Parse(rawEvidenceJson);
                document.RootElement.WriteTo(writer);
            }
            catch (JsonException)
            {
                writer.WriteStartObject();
                writer.WriteString("unparseable", rawEvidenceJson);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        File.WriteAllBytes(Path.Combine(folder, "gui-power-cycle.json"), stream.ToArray());
    }
}
