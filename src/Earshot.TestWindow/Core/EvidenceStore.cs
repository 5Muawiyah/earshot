using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Earshot.TestWindow.Core;

// Reads run evidence from disk, and only from disk (design.md D4: "only result.json can raise a
// row to Passed"). Every read is fail closed: a file that cannot be parsed, or that disagrees
// with itself, comes back as a reason string, never as a guess. test-gui.md section 6.2.
internal static partial class EvidenceStore
{
    [GeneratedRegex(@"^\d{8}T\d{6}Z$")]
    private static partial Regex StampPatternRegex();

    internal static bool IsRunStamp(string name) => StampPatternRegex().IsMatch(name);

    // Every run folder for one TestId under the live test root, newest stamp first. A stamp
    // format sorts lexically the same as chronologically (yyyyMMdd'T'HHmmss'Z', UTC), so an
    // ordinal sort needs no date parsing. Anything under the root whose top folder is not a
    // stamp, or that holds no folder named exactly TestId, is ignored, as section 6.2 says.
    internal static IReadOnlyList<(string Stamp, string Folder)> FindRunFolders(string liveTestRoot, string testId)
    {
        var found = new List<(string Stamp, string Folder)>();
        if (!Directory.Exists(liveTestRoot))
        {
            return found;
        }

        foreach (string stampFolder in Directory.EnumerateDirectories(liveTestRoot))
        {
            string stamp = Path.GetFileName(stampFolder);
            if (!IsRunStamp(stamp))
            {
                continue;
            }

            string testFolder = Path.Combine(stampFolder, testId);
            if (Directory.Exists(testFolder))
            {
                found.Add((stamp, testFolder));
            }
        }

        found.Sort((a, b) => string.CompareOrdinal(b.Stamp, a.Stamp));
        return found;
    }

    // Every run folder for one TestId, read and validated, newest stamp first: exactly what
    // StateDeriver.Derive takes as its evidence.
    internal static IReadOnlyList<RunEvidence> LoadEvidence(string liveTestRoot, string testId)
    {
        var evidence = new List<RunEvidence>();
        foreach ((string stamp, string folder) in FindRunFolders(liveTestRoot, testId))
        {
            evidence.Add(ReadRunEvidence(stamp, folder, testId));
        }

        return evidence;
    }

    // Everything this run folder can tell the deriver about one test: its own result.json, the
    // first-half snapshot the window takes at the power-cycle boundary (section 9.1), whether
    // resume.txt or gui-set-aside.txt are present, and the power-cycle verdict (section 9.3).
    internal static RunEvidence ReadRunEvidence(string stamp, string folder, string testId)
    {
        (ParsedResult? result, string? failure) = TryReadResult(Path.Combine(folder, "result.json"), testId);

        string snapshotPath = Path.Combine(folder, "gui-first-half.result.json");
        bool hasSnapshotFile = File.Exists(snapshotPath);
        ParsedResult? snapshot = hasSnapshotFile ? TryReadResult(snapshotPath, testId).Result : null;

        return new RunEvidence
        {
            Stamp = stamp,
            Folder = folder,
            Result = result,
            ReadFailureReason = failure,
            FirstHalfSnapshot = snapshot,
            HasFirstHalfSnapshotFile = hasSnapshotFile,
            HasResumeFile = File.Exists(Path.Combine(folder, "resume.txt")),
            HasSetAsideFile = File.Exists(Path.Combine(folder, "gui-set-aside.txt")),
            HasKilledMarker = File.Exists(Path.Combine(folder, "gui-killed.txt")),
            PowerCycleVerdict = TryReadPowerCycleVerdict(Path.Combine(folder, "gui-power-cycle.json")),
        };
    }

    // The fail-closed read, section 6.2 rules 1 to 5. Every early return is a rule from that
    // section; the comment beside each one names it.
    internal static (ParsedResult? Result, string? FailureReason) TryReadResult(string path, string expectedTestId)
    {
        if (!File.Exists(path))
        {
            return (null, "no result.json");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            return (null, "result.json could not be read: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (null, "result.json could not be read: " + ex.Message);
        }

        if (bytes.Length == 0)
        {
            return (null, "result.json is empty");
        }

        // Rule 1: UTF-8, with or without a byte order mark (PowerShell 5.1's Set-Content -Encoding
        // UTF8 writes one).
        ReadOnlyMemory<byte> content = bytes;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            content = content[3..];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            return (null, "result.json is not valid JSON: " + ex.Message);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "result.json is not a JSON object");
            }

            // Rule 2, part one: test equals the folder's TestId.
            string? test = TryGetString(root, "test");
            if (test is null)
            {
                return (null, "result.json has no 'test' member");
            }

            if (!string.Equals(test, expectedTestId, StringComparison.Ordinal))
            {
                return (null, "result.json is for test '" + test + "', not '" + expectedTestId + "'");
            }

            // Rule 2, part two: overall is exactly pass, fail or inconclusive, lower case.
            string? overall = TryGetString(root, "overall");
            if (overall is not ("pass" or "fail" or "inconclusive"))
            {
                return (null, "overall is '" + (overall ?? "(missing)") + "', not pass, fail or inconclusive in lower case");
            }

            // Rule 3: criteria may be an array, a single object, or absent.
            if (!TryReadCriteria(root, out IReadOnlyList<CriterionRecord>? criteria, out string? criteriaFailure))
            {
                return (null, criteriaFailure);
            }

            // Rule 4: recompute overall from the criteria by Complete-LiveTestRun's own rule. A
            // pass therefore needs at least one criterion and every criterion passing.
            string recomputed = RecomputeOverall(criteria!);
            if (!string.Equals(recomputed, overall, StringComparison.Ordinal))
            {
                return (null, "result.json disagrees with itself: its criteria recompute to '" + recomputed +
                    "' but overall says '" + overall + "'");
            }

            var parsed = new ParsedResult
            {
                Test = test,
                Overall = overall,
                Criteria = criteria!,
                Findings = ReadFindings(root),
                Errors = ReadErrors(root),
                StepCount = CountArrayOrObjectMember(root, "steps"),
                Exe = TryGetString(root, "exe"),
                StartedUtc = TryGetDate(root, "startedUtc"),
                FinishedUtc = TryGetDate(root, "finishedUtc"),
            };
            return (parsed, null);
        }
    }

    private static bool TryReadCriteria(JsonElement root, out IReadOnlyList<CriterionRecord>? criteria, out string? failureReason)
    {
        var list = new List<CriterionRecord>();
        if (root.TryGetProperty("criteria", out JsonElement element) && element.ValueKind != JsonValueKind.Null)
        {
            if (element.ValueKind != JsonValueKind.Array && element.ValueKind != JsonValueKind.Object)
            {
                criteria = null;
                failureReason = "'criteria' is neither an array, an object nor absent";
                return false;
            }

            IEnumerable<JsonElement> items = element.ValueKind == JsonValueKind.Array
                ? element.EnumerateArray()
                : new[] { element };

            foreach (JsonElement item in items)
            {
                string? id = item.ValueKind == JsonValueKind.Object ? TryGetString(item, "id") : null;
                string? text = item.ValueKind == JsonValueKind.Object ? TryGetString(item, "criterion") : null;
                string? outcome = item.ValueKind == JsonValueKind.Object ? TryGetString(item, "outcome") : null;

                if (id is null || text is null || outcome is not ("pass" or "fail" or "inconclusive"))
                {
                    criteria = null;
                    failureReason = "a criterion entry is missing id, criterion or a valid outcome";
                    return false;
                }

                list.Add(new CriterionRecord(id, text, outcome, TryGetString(item, "detail") ?? string.Empty));
            }
        }

        criteria = list;
        failureReason = null;
        return true;
    }

    private static List<FindingRecord> ReadFindings(JsonElement root)
    {
        var list = new List<FindingRecord>();
        if (!root.TryGetProperty("findings", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return list;
        }

        IEnumerable<JsonElement> items = element.ValueKind switch
        {
            JsonValueKind.Array => element.EnumerateArray(),
            JsonValueKind.Object => new[] { element },
            _ => Array.Empty<JsonElement>(),
        };

        foreach (JsonElement item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? name = TryGetString(item, "name");
            if (name is null)
            {
                continue;
            }

            string? value = null;
            if (item.TryGetProperty("value", out JsonElement valueElement))
            {
                value = valueElement.ValueKind switch
                {
                    JsonValueKind.String => valueElement.GetString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => null,
                    JsonValueKind.Number => valueElement.GetRawText(),
                    _ => valueElement.GetRawText(),
                };
            }

            list.Add(new FindingRecord(name, value, TryGetString(item, "detail") ?? string.Empty));
        }

        return list;
    }

    // Write-Failure's own shape: { utc, message }. Read leniently, the same as findings: a
    // malformed entry is skipped rather than failing the whole read, because errors are shown
    // alongside a result, never used to decide pass or fail.
    private static List<string> ReadErrors(JsonElement root)
    {
        var list = new List<string>();
        if (!root.TryGetProperty("errors", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return list;
        }

        IEnumerable<JsonElement> items = element.ValueKind switch
        {
            JsonValueKind.Array => element.EnumerateArray(),
            JsonValueKind.Object => new[] { element },
            _ => Array.Empty<JsonElement>(),
        };

        foreach (JsonElement item in items)
        {
            string? message = item.ValueKind == JsonValueKind.Object ? TryGetString(item, "message") : null;
            if (message is not null)
            {
                list.Add(message);
            }
        }

        return list;
    }

    private static int CountArrayOrObjectMember(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            return 0;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Array => element.GetArrayLength(),
            JsonValueKind.Object => 1,
            _ => 0,
        };
    }

    private static string RecomputeOverall(IReadOnlyList<CriterionRecord> criteria)
    {
        if (criteria.Count == 0)
        {
            return "inconclusive";
        }

        if (criteria.Any(c => c.Outcome == "fail"))
        {
            return "fail";
        }

        if (criteria.Any(c => c.Outcome == "inconclusive"))
        {
            return "inconclusive";
        }

        return "pass";
    }

    private static string? TryGetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? TryGetDate(JsonElement root, string name)
    {
        string? text = TryGetString(root, name);
        if (text is not null && DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value))
        {
            return value;
        }

        return null;
    }

    // gui-power-cycle.json (section 9.3): { ..., "verdict": "power-down" | "restart" | "not-yet" | "unknown" }.
    // Written by PowerCycle.cs in a later slice; read here as plain data, decided by nothing else.
    private static string? TryReadPowerCycleVerdict(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? TryGetString(document.RootElement, "verdict")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
