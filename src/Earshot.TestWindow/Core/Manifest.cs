using System.Text.Json;

namespace Earshot.TestWindow.Core;

// The 16 tests, read from Data\tests.json (test-gui.md section 6.1). Read with JsonDocument, not
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
            WhatItProves = element.GetProperty("whatItProves").GetString()!,
            PowerCycleRequirement = ReadPowerCycleRequirement(element),
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
