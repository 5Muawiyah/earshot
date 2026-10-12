using Earshot.Repair;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// Removing the AirPods from Windows and pairing them again has not been run on real AirPods, and a hash only shows a download
// arrived whole. The person is asked to agree to the first and to rely on the second, so both are said where they decide.
[TestClass]
public sealed class FixHonestyCopyTests
{
    private const string Unproved = "has not yet been proved on real AirPods";

    private static string GuideFile()
    {
        for (DirectoryInfo? folder = new(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "docs", "ai-guide.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("docs\\ai-guide.md was not found above " + AppContext.BaseDirectory);
    }

    private static string Flat(string text) => string.Join(" ", text.Split((char[])['\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));

    [TestMethod]
    public void TheRemoveAndPairPromptSaysItIsUnprovedOnRealAirPods()
    {
        FixPrompt prompt = FixPromptText.RePair(["AirPods"], removeFirst: true);

        StringAssert.Contains(prompt.Body, Unproved);
    }

    [TestMethod]
    public void TheGuideSaysRemoveAndPairIsUnprovedAndWhatTheSetupHashCatches()
    {
        string guide = Flat(File.ReadAllText(GuideFile()));

        StringAssert.Contains(guide, "Removing the AirPods and pairing them again " + Unproved + ".");
        StringAssert.Contains(guide, "That check catches damage in the download, not a hostile release or a swapped file.");
    }
}
