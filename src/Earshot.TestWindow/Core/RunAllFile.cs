using System.Text.Json;

namespace Earshot.TestWindow.Core;

// run-all.json holds the order, the item it stopped at, and for each item a pointer to its run
// folder. No outcome is stored. One file, at the live test root (never inside a stamp folder,
// since it spans many runs).
internal sealed record RunAllRecord
{
    public required IReadOnlyList<string> Order { get; init; }

    // -1: nothing is stopped (a fresh record, or a completed sequence).
    public int StoppedAtIndex { get; init; } = -1;

    // item key ("08", "10v3") -> the stamp folder its evidence is under.
    public IReadOnlyDictionary<string, string> Pointers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

// Read and write only: RunAllFile decides nothing about whether to halt or advance (RunAllHalt
// does), and reads fail closed. A lost or unreadable run-all.json means Run all is simply not
// active; the list is unaffected, and a corrupt run-all.json changes no row: a read failure here
// must never touch StateDeriver's own evidence, only this file.
internal static class RunAllFile
{
    internal const string FileName = "run-all.json";

    internal static string Key(RunAllItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Key;
    }

    internal static RunAllRecord? TryRead(string liveTestRoot)
    {
        string path = Path.Combine(liveTestRoot, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("order", out JsonElement orderElement) || orderElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var order = new List<string>();
            foreach (JsonElement item in orderElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                order.Add(item.GetString()!);
            }

            int stoppedAtIndex = root.TryGetProperty("stoppedAtIndex", out JsonElement stoppedElement) &&
                stoppedElement.ValueKind == JsonValueKind.Number
                ? stoppedElement.GetInt32()
                : -1;

            var pointers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("pointers", out JsonElement pointersElement) && pointersElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in pointersElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        pointers[property.Name] = property.Value.GetString()!;
                    }
                }
            }

            return new RunAllRecord { Order = order, StoppedAtIndex = stoppedAtIndex, Pointers = pointers };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void Write(string liveTestRoot, RunAllRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Directory.CreateDirectory(liveTestRoot);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("order");
            writer.WriteStartArray();
            foreach (string key in record.Order)
            {
                writer.WriteStringValue(key);
            }

            writer.WriteEndArray();
            writer.WriteNumber("stoppedAtIndex", record.StoppedAtIndex);
            writer.WritePropertyName("pointers");
            writer.WriteStartObject();
            foreach ((string key, string stamp) in record.Pointers)
            {
                writer.WriteString(key, stamp);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        File.WriteAllBytes(Path.Combine(liveTestRoot, FileName), stream.ToArray());
    }

    internal static void Delete(string liveTestRoot)
    {
        string path = Path.Combine(liveTestRoot, FileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
