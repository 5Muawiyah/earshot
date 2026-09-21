using System.Globalization;
using System.Text.RegularExpressions;

namespace Earshot.TestWindow.Core;

internal sealed record ResumeInstruction
{
    public required string ScriptPath { get; init; }
    public required string ExePath { get; init; }
    public required string RunRoot { get; init; }
    public int? Variant { get; init; }
}

// A strict parser for resume.txt. Parsed, never executed: this file is
// never handed to a shell, only matched against the one literal line
// Write-ResumeInstruction writes, then checked against the filesystem. Anything else, the row is
// Unknown and nothing is started.
internal static class ResumeFile
{
    // powershell -NoProfile -ExecutionPolicy Bypass -File "<script>" -ExePath "<exe>"
    // -RunRoot "<root>" -Resume[ -Variant <1-5>]
    private static readonly Regex Pattern = new(
        "^powershell -NoProfile -ExecutionPolicy Bypass -File \"([^\"]+)\" -ExePath \"([^\"]+)\" -RunRoot \"([^\"]+)\" -Resume(?: -Variant ([1-5]))?$",
        RegexOptions.Compiled);

    internal static bool TryParse(
        string resumeTxtPath,
        string repositoryRoot,
        string liveTestRoot,
        IReadOnlyCollection<string> knownScriptFileNames,
        out ResumeInstruction? instruction,
        out string? reason)
    {
        instruction = null;

        if (!File.Exists(resumeTxtPath))
        {
            reason = "resume.txt does not exist";
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(resumeTxtPath);
        }
        catch (IOException ex)
        {
            reason = "resume.txt could not be read: " + ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            reason = "resume.txt could not be read: " + ex.Message;
            return false;
        }

        // Write-ResumeInstruction writes exactly one line (Set-Content with one -Value). A
        // second line, of anything, is not that shape.
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string[] nonEmpty = lines.Where(l => l.Length > 0).ToArray();
        if (nonEmpty.Length != 1)
        {
            reason = "resume.txt must hold exactly one line";
            return false;
        }

        Match match = Pattern.Match(nonEmpty[0]);
        if (!match.Success)
        {
            reason = "resume.txt's line does not match Write-ResumeInstruction's own format: " + nonEmpty[0];
            return false;
        }

        string scriptPath = match.Groups[1].Value;
        string exePath = match.Groups[2].Value;
        string runRoot = match.Groups[3].Value;
        int? variant = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : null;

        string expectedScriptFolder = Path.GetFullPath(Path.Combine(repositoryRoot, "tools", "live-tests"));
        string actualScriptFolder;
        try
        {
            actualScriptFolder = Path.GetFullPath(Path.GetDirectoryName(scriptPath) ?? string.Empty);
        }
        catch (ArgumentException)
        {
            reason = "resume.txt's script path is not a valid path: " + scriptPath;
            return false;
        }

        if (!string.Equals(actualScriptFolder, expectedScriptFolder, StringComparison.OrdinalIgnoreCase) ||
            !knownScriptFileNames.Contains(Path.GetFileName(scriptPath), StringComparer.OrdinalIgnoreCase))
        {
            reason = "resume.txt's script is not one of the 16 shipped scripts inside this repository's tools\\live-tests: " + scriptPath;
            return false;
        }

        // The root must be the folder two above resume.txt: <root>\<TestId>\resume.txt.
        string resumeTxtFullPath = Path.GetFullPath(resumeTxtPath);
        string? testIdFolder = Path.GetDirectoryName(resumeTxtFullPath);
        string? expectedRoot = testIdFolder is null ? null : Path.GetDirectoryName(testIdFolder);
        if (expectedRoot is null || !string.Equals(Path.GetFullPath(runRoot), Path.GetFullPath(expectedRoot), StringComparison.OrdinalIgnoreCase))
        {
            reason = "resume.txt's -RunRoot is not the folder two above it: " + runRoot;
            return false;
        }

        string fullLiveTestRoot = Path.GetFullPath(liveTestRoot);
        if (!Path.GetFullPath(runRoot).StartsWith(fullLiveTestRoot, StringComparison.OrdinalIgnoreCase))
        {
            reason = "resume.txt's -RunRoot does not lie under " + liveTestRoot + ": " + runRoot;
            return false;
        }

        if (!File.Exists(exePath))
        {
            reason = "resume.txt's -ExePath does not exist: " + exePath;
            return false;
        }

        instruction = new ResumeInstruction { ScriptPath = scriptPath, ExePath = exePath, RunRoot = runRoot, Variant = variant };
        reason = null;
        return true;
    }
}
