namespace Earshot.TestWindow.Core;

// The second half's own New-LiveTestRun truncates summary.txt and Complete-LiveTestRun overwrites
// result.json, so without this copy the first half's record would be lost. Copied additively,
// under the gui- prefix, never overwriting a harness file.
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
