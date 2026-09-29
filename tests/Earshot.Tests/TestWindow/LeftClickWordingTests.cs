using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A left click on the tray icon opens a card, and connects or disconnects only when "Left click connects
// straight away" is on, which it is not by default. Connecting and disconnecting are the buttons on the card. The
// live tests and the window's words once said that one left click connects, and asked about the battery claim item
// that the card's "Set up battery" replaced. This reads every place those words live and fails on the old phrasing,
// so it cannot come back unnoticed.
[TestClass]
public sealed class LeftClickWordingTests
{
    private static readonly string[] OutOfDatePhrases =
    {
        "Make these my AirPods",
        "with one click",
        "one click on its icon",
        "the same way a left click",
        "does on a left click",
        "a left click in the tray",
        "toggle only once",
    };

    [TestMethod]
    public void NoLiveTestOrWindowTextSaysOneLeftClickConnectsOrNamesTheOldClaimItem()
    {
        string root = RepositoryLocator.RepositoryRoot();
        var files = new List<string>
        {
            Path.Combine(root, "src", "Earshot.TestWindow", "Data", "tests.json"),
            Path.Combine(root, "src", "Earshot.TestWindow", "Data", "wording.json"),
            Path.Combine(root, "src", "Earshot.TestWindow", "Ui", "Copy.cs"),
        };
        files.AddRange(Directory.GetFiles(Path.Combine(root, "tools", "live-tests"), "*.ps1", SearchOption.TopDirectoryOnly));

        var found = new List<string>();
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            foreach (string phrase in OutOfDatePhrases)
            {
                if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(Path.GetFileName(file) + ": \"" + phrase + "\"");
                }
            }
        }

        Assert.IsEmpty(found, "These words describe the old left click or the old claim item:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }
}
