namespace Earshot.TestWindow.Core;

// test-gui.md section 9.1: "the second half's New-LiveTestRun truncates summary.txt and
// Complete-LiveTestRun overwrites result.json, so without it the first half's record ... is
// lost." Copied additively, under the gui- prefix (section 8.4), never overwriting a harness
// file.
internal static class FirstHalfSnapshot
{
    internal const string ResultFileName = "gui-first-half.result.json";
    internal const string SummaryFileName = "gui-first-half.summary.txt";

    internal static void Take(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        string result = Path.Combine(folder, "result.json");
        if (File.Exists(result))
        {
            File.Copy(result, Path.Combine(folder, ResultFileName), overwrite: true);
        }

        string summary = Path.Combine(folder, "summary.txt");
        if (File.Exists(summary))
        {
            File.Copy(summary, Path.Combine(folder, SummaryFileName), overwrite: true);
        }
    }
}
