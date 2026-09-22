using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Earshot.TestWindow.Core;

// Reads run evidence from disk, and only from disk: only result.json can raise a row to Passed.
// Every read is fail closed: a file that cannot be parsed, or that disagrees with itself, comes
// back as a reason string, never as a guess.
internal static partial class EvidenceStore
{
    [GeneratedRegex(@"^\d{8}T\d{6}Z$")]
    private static partial Regex StampPatternRegex();

    internal static bool IsRunStamp(string name) => StampPatternRegex().IsMatch(name);

    // Every run folder for one TestId under the live test root, newest first. Ordered by
    // RunSequence's own marker (gui-sequence.txt) where a folder has one, since a stamp
    // (yyyyMMdd'T'HHmmss'Z', UTC, otherwise sorting lexically the same as chronologically) is only
    // ever as trustworthy as the system clock was at the moment it was written: with the clock
    // stepped back between two runs, a later run's own stamp can read earlier than an older run's,
    // hiding a later kill or a later not-at-rest result behind it, or letting a later fail read as
    // the chosen, reported "Passed" run. A folder with no marker at all (an older run, from before
    // this existed) is still ordered by its stamp, exactly as before. Anything under the root
    // whose top folder is not a stamp, or that holds no folder named exactly TestId, is ignored:
    // it is not this test's evidence.
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

        found.Sort((a, b) => CompareNewestFirst(a, b));
        return found;
    }

    // Negative when a is newer than b (so a sorts first). Sequence decides it when both folders
    // have a marker; a folder without one always falls back to comparing by stamp for that one
    // comparison, never treated as though it were older or newer than a sequence could say.
    private static int CompareNewestFirst((string Stamp, string Folder) a, (string Stamp, string Folder) b)
    {
        long? sequenceA = RunSequence.TryReadMarker(a.Folder);
        long? sequenceB = RunSequence.TryReadMarker(b.Folder);
        if (sequenceA is long va && sequenceB is long vb)
        {
            return vb.CompareTo(va);
        }

        return string.CompareOrdinal(b.Stamp, a.Stamp);
    }

    // True when any two folders in this set carry a sequence marker whose order disagrees with
    // the order of what actually happened to each of them (EventTimeUtc: the latest sequence
    // marker was issued, a kill marker was written, or a trusted result's own finishedUtc,
    // ComputeEventTimeUtc below): the one situation neither signal alone can be fully trusted in,
    // since one of them was necessarily wrong about which of the two actually happened later.
    // Never compared against the folder's own bare stamp: a resumed half reuses its first half's
    // run folder, keeping that folder's original stamp forever however much later its own newest
    // event actually happened, so comparing against the stamp read every legitimate resume-after-
    // an-intervening-run as a disagreement, permanently, since the folder's stamp can never catch
    // up. Surfaced to the owner (StateDeriver's HistoryNote, Banner's own red) rather than
    // silently preferring sequence and saying nothing.
    //
    // A disagreeing pair is skipped, not surfaced, when some third entry in the same set is newer
    // than BOTH of them by sequence AND by event time (dominates them): whichever of the pair was
    // actually right or wrong about their own order can no longer change which run is genuinely
    // newest overall, since something unambiguous already outranks both, so the old disagreement
    // between them must never keep the banner locked for ever after a real Restore (or any other
    // run) that itself is unambiguously the newest thing that has happened. Two different run
    // folders carrying the very same sequence number is never forgiven this way: RunSequence.
    // TakeNext never legitimately hands the same value out twice, so two folders holding it is a
    // sign the count itself cannot be trusted, not a mere ordering ambiguity a later run can settle.
    internal static bool SequenceDisagreesWithStampOrder(IReadOnlyList<(long? Sequence, DateTimeOffset EventTimeUtc)> entries)
    {
        List<(long Sequence, DateTimeOffset EventTimeUtc)> withSequence = entries
            .Where(e => e.Sequence is not null)
            .Select(e => (e.Sequence!.Value, e.EventTimeUtc))
            .ToList();

        for (int i = 0; i < withSequence.Count; i++)
        {
            for (int j = i + 1; j < withSequence.Count; j++)
            {
                long si = withSequence[i].Sequence;
                long sj = withSequence[j].Sequence;
                if (si == sj)
                {
                    return true;
                }

                bool iNewerBySequence = si > sj;
                bool iNewerByEventTime = withSequence[i].EventTimeUtc > withSequence[j].EventTimeUtc;
                if (iNewerBySequence == iNewerByEventTime)
                {
                    continue;
                }

                bool dominated = withSequence.Any(k =>
                    k.Sequence > si && k.EventTimeUtc > withSequence[i].EventTimeUtc &&
                    k.Sequence > sj && k.EventTimeUtc > withSequence[j].EventTimeUtc);
                if (!dominated)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Every run folder for one TestId, read and validated, newest stamp first: exactly what
    // StateDeriver.Derive takes as its evidence. activeFolder (MainForm's own _activeResultFolder,
    // or null when nothing is running) is the one run folder gui-run-started.txt is never read as
    // stale for: it is this window's own currently active half, not one that died unnoticed.
    internal static IReadOnlyList<RunEvidence> LoadEvidence(string liveTestRoot, string testId, string? activeFolder = null)
    {
        var evidence = new List<RunEvidence>();
        foreach ((string stamp, string folder) in FindRunFolders(liveTestRoot, testId))
        {
            evidence.Add(ReadRunEvidence(stamp, folder, testId, activeFolder));
        }

        return evidence;
    }

    // Everything this run folder can tell the deriver about one test: its own result.json, the
    // first-half snapshot the window takes before shutting down or restarting at the power-cycle
    // boundary, whether resume.txt or gui-set-aside.txt are present, and the power-cycle verdict
    // (power-down, restart, not-yet or unknown) recorded before a second half ran.
    internal static RunEvidence ReadRunEvidence(string stamp, string folder, string testId, string? activeFolder = null)
    {
        (ParsedResult? result, string? failure) = TryReadResult(Path.Combine(folder, "result.json"), testId);

        string snapshotPath = Path.Combine(folder, "gui-first-half.result.json");
        bool hasSnapshotFile = File.Exists(snapshotPath);
        ParsedResult? snapshot = hasSnapshotFile ? TryReadResult(snapshotPath, testId).Result : null;

        bool hasRunStartedMarker = File.Exists(Path.Combine(folder, "gui-run-started.txt"));
        bool isActiveFolder = activeFolder is not null && string.Equals(folder, activeFolder, StringComparison.OrdinalIgnoreCase);
        bool hasKilledMarker = File.Exists(Path.Combine(folder, "gui-killed.txt"));
        bool hasStaleRunStartedMarker = hasRunStartedMarker && !isActiveFolder;

        // Trusted the same way Banner.cs's own scan trusts a run: a real result, never a killed or
        // stale-started folder whose result.json (if any) is that half's own victim, not evidence
        // of what actually happened last.
        bool trustworthy = result is not null && !hasKilledMarker && !hasStaleRunStartedMarker;
        DateTimeOffset stampUtc = ParseStampUtc(stamp);

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
            HasKilledMarker = hasKilledMarker,
            HasStaleRunStartedMarker = hasStaleRunStartedMarker,
            Sequence = RunSequence.TryReadMarker(folder),
            PowerCycleVerdict = TryReadPowerCycleVerdict(Path.Combine(folder, "gui-power-cycle.json")),
            EventTimeUtc = ComputeEventTimeUtc(folder, result, trustworthy, stampUtc),
        };
    }

    // The stamp format IsRunStamp already validated (^\d{8}T\d{6}Z$) for anything that reaches
    // here, so this always parses.
    internal static DateTimeOffset ParseStampUtc(string stamp) =>
        DateTimeOffset.ParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    // The newest last-write time of any file directly in this folder (result.json, resume.txt,
    // gui-killed.txt, gui-sequence.txt and the rest), falling back to the given time when the
    // folder holds nothing at all: an untrusted folder (a kill, a stale start, or a read failure)
    // is ordered by whatever actually happened last inside it, not by a stale record's own idea of
    // when it finished.
    internal static DateTimeOffset NewestWriteTimeUtc(string folder, DateTimeOffset fallback)
    {
        DateTimeOffset newest = fallback;
        if (!Directory.Exists(folder))
        {
            return newest;
        }

        foreach (string file in Directory.EnumerateFiles(folder))
        {
            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            if (written > newest)
            {
                newest = written;
            }
        }

        return newest;
    }

    // The single definition of "the newest moment anything is actually known to have happened in
    // this run folder", shared by Banner.cs's own cross-test scan and every per-row RunEvidence:
    // a trusted result's own finishedUtc (embedded by the script the moment it genuinely finished,
    // deliberately preferred here over any file's own real write time, which a fixture's own
    // narrative finishedUtc need not match), or, for a killed, stale or otherwise untrusted folder
    // (no finishedUtc worth trusting), the newest real write time of anything inside it, which a
    // reissued sequence marker or a kill marker are both ordinary files that already fall out of.
    // stampUtc is the last resort, when nothing else in the folder is known at all.
    internal static DateTimeOffset ComputeEventTimeUtc(string folder, ParsedResult? result, bool trustworthy, DateTimeOffset stampUtc) =>
        trustworthy ? result!.FinishedUtc ?? stampUtc : NewestWriteTimeUtc(folder, stampUtc);

    // A convenience over LoadEvidence's own already-read RunEvidence, for a caller (MainForm's
    // ComputeState) that needs to know whether this row's own evidence contains a sequence/order
    // disagreement, without reading every marker file from disk a second time.
    internal static bool SequenceDisagreesWithStampOrder(IReadOnlyList<RunEvidence> evidence) =>
        SequenceDisagreesWithStampOrder(evidence.Select(run => (run.Sequence, run.EventTimeUtc)).ToList());

    // The fail-closed read of result.json. Every early return below enforces one rule for
    // trusting that file, and the comment beside each one names which rule it is.
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
                Steps = ReadSteps(root),
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

    // Leniently read too, the same as findings and errors: steps are shown and reasoned about (a
    // declined elevated prompt), never used to decide pass or fail.
    private static List<StepRecord> ReadSteps(JsonElement root)
    {
        var list = new List<StepRecord>();
        if (!root.TryGetProperty("steps", out JsonElement element) || element.ValueKind == JsonValueKind.Null)
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

            bool elevated = item.TryGetProperty("elevated", out JsonElement elevatedElement) && elevatedElement.ValueKind == JsonValueKind.True;
            bool ran = item.TryGetProperty("ran", out JsonElement ranElement) && ranElement.ValueKind == JsonValueKind.True;
            list.Add(new StepRecord(elevated, ran, TryGetString(item, "error")));
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

    // gui-power-cycle.json holds the power-cycle verdict: { ..., "verdict": "power-down" | "restart" | "not-yet" | "unknown" }.
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
