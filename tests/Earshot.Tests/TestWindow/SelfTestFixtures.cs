using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Earshot.Tests.LiveTests;

namespace Earshot.Tests.TestWindow;

// One row of tools\live-tests\selftest\Invoke-SelfTest.ps1's own $tests table (read by
// Export-SelfTestPlan.ps1, never copied by hand): the script, the halves the self-test drives for
// it, and anything beyond -ExePath/-RunRoot/-Resume/-Variant/-OfferUninstall/-AllowPlanB it needs
// (Invoke-SelfTest.ps1's own Extra list, e.g. "WatchSeconds=30").
internal sealed record SelfTestPlanRow(string Number, string Id, string Script, IReadOnlyList<string> Halves, IReadOnlyList<string> Extra);

// One "<id>|<half>" entry of expectations.psd1's "one" case (read by Export-ExpectationsPlan.ps1):
// the overall outcome(s) it allows, the outcome pinned for every criterion it names ("any" left as
// the literal string, meaning not this machine's to decide), and the finding names it pins, either
// exhaustively (FindingNames) or only in part (FindingsIncludeNames).
internal sealed record ExpectationsPlanRow(
    string Key,
    IReadOnlyList<string> Overall,
    IReadOnlyDictionary<string, string> Criteria,
    IReadOnlyList<string>? FindingNames,
    IReadOnlyList<string>? FindingsIncludeNames);

// Fakes.psm1's own Answers and Notes tables (read by Export-FakeOwnerTables.ps1), in the order
// Get-FakeAnswer/Get-FakeNote themselves search them: first case-insensitive Contains match on the
// question text wins. CloseAtRestDisconnect and CloseAtRestBlockWhilePlaying are LiveTest.psm1's
// own $script:AtRestDisconnectConsequence and $script:AtRestBlockWhilePlayingConsequence, read by
// the same script, so a test asserting what PromptPresenter.cs maps either text to compares
// against what the module actually says today rather than a hand-kept copy that can drift from it
// unnoticed.
internal sealed class FakeOwnerTables
{
    public required IReadOnlyList<(string Key, string Value)> Answers { get; init; }
    public required IReadOnlyList<(string Key, string Value)> Notes { get; init; }
    public required string CloseAtRestDisconnect { get; init; }
    public required string CloseAtRestBlockWhilePlaying { get; init; }
}

// Runs the three exporter scripts under tools\live-tests\gui\selftest and parses what they print.
// Every one of them reads real source (Invoke-SelfTest.ps1's $tests, Fakes.psm1's tables and
// LiveTest.psm1's own closing-step consequence texts, expectations.psd1) rather than a hand-kept
// copy, so this fixture data cannot drift from what the self-test itself actually does.
internal static class SelfTestFixtures
{
    private static readonly TimeSpan ExporterTimeout = TimeSpan.FromSeconds(60);

    internal static IReadOnlyList<SelfTestPlanRow> LoadPlan(string repoRoot, string host)
    {
        string json = RunExporter(host, Path.Combine(repoRoot, "tools", "live-tests", "gui", "selftest", "Export-SelfTestPlan.ps1"));
        using JsonDocument document = JsonDocument.Parse(json);
        var rows = new List<SelfTestPlanRow>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            rows.Add(new SelfTestPlanRow(
                item.GetProperty("number").GetString()!,
                item.GetProperty("id").GetString()!,
                item.GetProperty("script").GetString()!,
                ReadStrings(item, "halves"),
                ReadStrings(item, "extra")));
        }

        return rows;
    }

    internal static FakeOwnerTables LoadOwnerTables(string repoRoot, string host)
    {
        string json = RunExporter(host, Path.Combine(repoRoot, "tools", "live-tests", "gui", "selftest", "Export-FakeOwnerTables.ps1"));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement consequences = root.GetProperty("closeAtRestConsequences");
        return new FakeOwnerTables
        {
            Answers = ReadPairs(root.GetProperty("answers")),
            Notes = ReadPairs(root.GetProperty("notes")),
            CloseAtRestDisconnect = consequences.GetProperty("disconnect").GetString()!,
            CloseAtRestBlockWhilePlaying = consequences.GetProperty("blockWhilePlaying").GetString()!,
        };
    }

    internal static IReadOnlyDictionary<string, ExpectationsPlanRow> LoadExpectations(string repoRoot, string host)
    {
        string json = RunExporter(host, Path.Combine(repoRoot, "tools", "live-tests", "gui", "selftest", "Export-ExpectationsPlan.ps1"));
        using JsonDocument document = JsonDocument.Parse(json);
        var rows = new Dictionary<string, ExpectationsPlanRow>(StringComparer.Ordinal);
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string key = item.GetProperty("key").GetString()!;
            var criteria = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonElement criterion in item.GetProperty("criteria").EnumerateArray())
            {
                criteria[criterion.GetProperty("id").GetString()!] = criterion.GetProperty("outcome").GetString()!;
            }

            rows[key] = new ExpectationsPlanRow(
                key,
                ReadStrings(item, "overall"),
                criteria,
                item.TryGetProperty("findingNames", out JsonElement findingNames) ? ReadStrings(findingNames) : null,
                item.TryGetProperty("findingsIncludeNames", out JsonElement includeNames) ? ReadStrings(includeNames) : null);
        }

        return rows;
    }

    private static List<string> ReadStrings(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out JsonElement element) ? ReadStrings(element) : new List<string>();

    private static List<string> ReadStrings(JsonElement element)
    {
        var list = new List<string>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    list.Add(item.GetString()!);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            list.Add(element.GetString()!);
        }

        return list;
    }

    private static List<(string Key, string Value)> ReadPairs(JsonElement array)
    {
        var list = new List<(string, string)>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            list.Add((item.GetProperty("key").GetString()!, item.GetProperty("value").GetString()!));
        }

        return list;
    }

    private static string RunExporter(string host, string script)
    {
        if (!File.Exists(script))
        {
            throw new FileNotFoundException("Exporter script was not found: " + script);
        }

        ProcessStartInfo info = WindowsPowerShellHost.CreateStartInfo(
            host, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script });

        var output = new StringBuilder();
        var errors = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (errors) { errors.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit((int)ExporterTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Timed out running " + script + ".");
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Exporter " + script + " exited " + process.ExitCode + ": " + errors);
        }

        lock (output)
        {
            return output.ToString();
        }
    }
}
