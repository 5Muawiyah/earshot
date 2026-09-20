using System.Text.Json;

namespace Earshot.TestWindow.Core;

// test-gui.md section 9.3's four verdicts. "unknown" covers a read error, an unreadable log, and
// a 109 value this table does not name: it is never accepted as a shut down.
internal enum PowerCycleVerdict
{
    Unknown,
    NotYet,
    Restart,
    PowerDown,
}

// Decides a verdict from the raw rows Get-PowerCycleEvidence.ps1 returns; it reads nothing
// itself. test-gui.md section 9.3:
//   1. read error: unknown;
//   2. no start after since: not-yet;
//   3. the newest 109 before the newest start and after since: none gives unknown; 5 gives
//      restart; 4 or 6 gives power-down; any other value gives unknown.
internal static class PowerCycle
{
    internal static PowerCycleVerdict Decide(string evidenceJson)
    {
        ArgumentNullException.ThrowIfNull(evidenceJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(evidenceJson);
        }
        catch (JsonException)
        {
            return PowerCycleVerdict.Unknown;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return PowerCycleVerdict.Unknown;
            }

            // Rule 1: a read error the script itself recorded, rather than a shape it read fine.
            if (root.TryGetProperty("error", out _))
            {
                return PowerCycleVerdict.Unknown;
            }

            if (!TryReadUtcArray(root, "kernelGeneral12", out List<DateTimeOffset>? starts) ||
                !TryReadPower109(root, out List<(DateTimeOffset Utc, int? ShutdownActionType)>? power109))
            {
                return PowerCycleVerdict.Unknown;
            }

            // Rule 2: no start (Kernel-General 12) at all since the snapshot: not-yet.
            if (starts!.Count == 0)
            {
                return PowerCycleVerdict.NotYet;
            }

            DateTimeOffset newestStart = starts.Max();

            // Rule 3: the newest 109 strictly before the newest start.
            (DateTimeOffset Utc, int? ShutdownActionType)? newest109BeforeStart = power109!
                .Where(row => row.Utc < newestStart)
                .OrderByDescending(row => row.Utc)
                .Select(row => (row.Utc, row.ShutdownActionType) as (DateTimeOffset, int?)?)
                .FirstOrDefault();

            if (newest109BeforeStart is null)
            {
                return PowerCycleVerdict.Unknown;
            }

            return newest109BeforeStart.Value.ShutdownActionType switch
            {
                5 => PowerCycleVerdict.Restart,
                4 or 6 => PowerCycleVerdict.PowerDown,
                _ => PowerCycleVerdict.Unknown,
            };
        }
    }

    private static bool TryReadUtcArray(JsonElement root, string propertyName, out List<DateTimeOffset>? values)
    {
        values = null;
        if (!root.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<DateTimeOffset>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("utc", out JsonElement utcElement) ||
                utcElement.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(utcElement.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset utc))
            {
                return false;
            }

            list.Add(utc);
        }

        values = list;
        return true;
    }

    private static bool TryReadPower109(JsonElement root, out List<(DateTimeOffset Utc, int? ShutdownActionType)>? rows)
    {
        rows = null;
        if (!root.TryGetProperty("kernelPower109", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<(DateTimeOffset, int?)>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("utc", out JsonElement utcElement) ||
                utcElement.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(utcElement.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out DateTimeOffset utc))
            {
                return false;
            }

            int? type = item.TryGetProperty("shutdownActionType", out JsonElement typeElement) && typeElement.ValueKind == JsonValueKind.Number
                ? typeElement.GetInt32()
                : null;
            list.Add((utc, type));
        }

        rows = list;
        return true;
    }

    // "shut down" means the required-transition table's "full shut down" row: power-down only.
    internal static bool CountsAsFullShutDown(PowerCycleVerdict verdict) => verdict == PowerCycleVerdict.PowerDown;
}
