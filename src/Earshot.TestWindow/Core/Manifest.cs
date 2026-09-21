using System.Text.Json;

namespace Earshot.TestWindow.Core;

// The 16 tests, read from Data\tests.json. Read with JsonDocument, not
// JsonSerializer: this solution turns reflection-based (de)serialisation off
// (JsonSerializerIsReflectionEnabledByDefault is false), and a hand-written source-generated
// context is not worth it for a file this shape reads once at start-up.
internal static class Manifest
{
    internal const int ExpectedRowCount = 16;
    internal const int ExpectedHalfSum = 22;

    internal static string DefaultPath() => Path.Combine(AppContext.BaseDirectory, "Data", "tests.json");

    internal static IReadOnlyList<ManifestRow> Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using JsonDocument document = JsonDocument.Parse(bytes);

        var rows = new List<ManifestRow>();
        foreach (JsonElement rowElement in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            rows.Add(ReadRow(rowElement));
        }

        return rows;
    }

    private static ManifestRow ReadRow(JsonElement element)
    {
        List<ManifestVariant>? variants = null;
        if (element.TryGetProperty("variants", out JsonElement variantsElement) && variantsElement.ValueKind == JsonValueKind.Array)
        {
            variants = new List<ManifestVariant>();
            foreach (JsonElement variantElement in variantsElement.EnumerateArray())
            {
                variants.Add(new ManifestVariant(
                    variantElement.GetProperty("variant").GetInt32(),
                    variantElement.GetProperty("testId").GetString()!,
                    variantElement.GetProperty("name").GetString()!,
                    variantElement.GetProperty("title").GetString()!,
                    ReadPowerCycleRequirement(variantElement)));
            }
        }

        return new ManifestRow
        {
            Number = element.GetProperty("number").GetString()!,
            Script = element.GetProperty("script").GetString()!,
            TestId = element.GetProperty("testId").GetString()!,
            Kind = element.GetProperty("kind").GetString()!,
            ElevatedVariant = element.TryGetProperty("elevatedVariant", out JsonElement elevated) && elevated.GetBoolean(),
            Halves = element.GetProperty("halves").GetInt32(),
            Name = element.GetProperty("name").GetString()!,
            Title = element.GetProperty("title").GetString()!,
            Proves = element.GetProperty("proves").GetString()!,
            Settles = element.GetProperty("settles").GetString()!,
            PowerCycleRequirement = ReadPowerCycleRequirement(element),
            // The default, 900 s ("This test has been silent for 15 minutes"), is not an invented
            // figure: it is the silence watchdog's own literal wording, unchanged, and is
            // comfortably longer than any legitimate gap the shipped scripts actually produce.
            // Where a script names its own watch parameter, tests.json's own value is derived
            // from it instead: 03 (03-AllowPages.ps1's $WatchSeconds, default 120) uses 150 (the
            // default plus a 30 s margin over its 10 s-interval watch loop); 13
            // (13-GraceWindow.ps1's $WatchMinutes, default 10, i.e. 600 s) uses 650 (a 50 s margin
            // over its own 15 s-interval loop). Every other row keeps the 900 s default: none of
            // the shipped scripts' other Wait-Seconds calls exceed a few seconds between the
            // Write-Line each one starts with.
            MaxSilenceSeconds = element.TryGetProperty("maxSilenceSeconds", out JsonElement silence) ? silence.GetInt32() : 900,
            FirstHalfOnlyCriteriaIds = ReadStringArray(element, "firstHalfOnlyCriteriaIds"),
            SecondHalfOnlyCriteriaIds = ReadStringArray(element, "secondHalfOnlyCriteriaIds"),
            FirstHalfOnlyFindingNames = ReadStringArray(element, "firstHalfOnlyFindingNames"),
            FirstHalfAloneIsCompleteWhenDeclined =
                element.TryGetProperty("firstHalfAloneIsCompleteWhenDeclined", out JsonElement declined) && declined.GetBoolean(),
            Variants = variants,
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString()!);
            }
        }

        return list;
    }

    private static PowerCycleRequirement ReadPowerCycleRequirement(JsonElement element)
    {
        string? value = element.TryGetProperty("powerCycleRequirement", out JsonElement prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

        return value switch
        {
            "any-start" => PowerCycleRequirement.AnyStart,
            "restart" => PowerCycleRequirement.Restart,
            "full-shut-down" => PowerCycleRequirement.FullShutDown,
            _ => PowerCycleRequirement.None,
        };
    }
}
