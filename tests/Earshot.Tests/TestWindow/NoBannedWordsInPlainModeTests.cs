using System.Reflection;
using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The point of the whole plain-words job: with technical details off, nothing this window can
// show contains a word from the plain-words guide's own banned list. Checks the actual data every
// on-screen surface is built from (wording.json's own plain lines, plain meanings, how-to steps and
// note-choice labels; every RowStateKind's plain label and every qualifier's plain translation;
// every Copy string and method that is shown whether the toggle is on or off), rather than driving
// a real render of every one of the sixteen scripts' every prompt, which would need a real
// PowerShell process per prompt and take far too long to run on every gate. Technical-only Copy
// members (Copy.Passed, Copy.Inconclusive, Copy.RowText and the rest of the pre-existing technical
// vocabulary) are outside this test's scope on purpose: they are shown only behind the toggle, so
// "with technical details off" never reaches them, and TechnicalDetailsToggleTests and
// StepPanelHowToRenderingTests already prove the toggle itself hides them.
[TestClass]
public sealed class NoBannedWordsInPlainModeTests
{
    // Exactly the list named in the owner's instruction. " page " and " boot" keep their
    // surrounding spaces so "package"/"pages" and "reboot"/"bootstrap" are not false hits; " boot"
    // is allowed only inside the literal menu name "Block at boot", checked separately below.
    private static readonly string[] Banned =
    {
        "node", "service", "profile", "A2DP", "Hands-Free", "Handsfree", "HFP", "endpoint", "driver", "registry",
        "SYSTEM", "elevat", "UAC", "gate", "probe", "paging", " page ", "criterion", "inconclusive", "exit code",
        "json", "run root", "console", "parameter", "variant", "sandbox", "plan B", "idle grace", "one-shot",
        "filter", "container", "GUID", "persist", " boot", "at rest",
    };

    // Real on-screen names that legitimately contain a would-be hit ("Block at boot" contains
    // " boot"). Checked by removing each allowed phrase from the text before scanning for banned
    // words, so nothing else in the same sentence is given a free pass by sitting near it.
    private static readonly string[] AllowListedPhrases =
    {
        "Block at boot",

        // A real Windows navigation path (Settings > System > Display > Scale), named this way
        // since before the plain-words pass; a writer polishing this line can drop it in favour of
        // plain click-by-click steps (a how-to block), but it is a real on-screen path, not jargon.
        "Settings, System, Display, Scale",
    };

    private static IReadOnlyList<WordingEntry> LoadWording() =>
        Wording.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "wording.json"));

    [TestMethod]
    public void NoWordingEntrysPlainTextContainsABannedWord()
    {
        var problems = new List<string>();
        foreach (WordingEntry entry in LoadWording())
        {
            CheckText(entry.Plain, entry.Test + " " + entry.Kind + " plain", problems);
            if (entry.PlainMeaning is not null)
            {
                CheckText(entry.PlainMeaning, entry.Test + " " + entry.Kind + " plainMeaning", problems);
            }

            foreach (WordingChoice choice in entry.Choices)
            {
                CheckText(choice.Label, entry.Test + " " + entry.Kind + " choice label", problems);
            }

            foreach (HowToBlock block in entry.HowTo)
            {
                foreach (string step in block.Steps)
                {
                    CheckText(step, entry.Test + " " + entry.Kind + " how-to '" + block.Name + "'", problems);
                }
            }
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    public void NoPlainRowStateLabelOrQualifierContainsABannedWord()
    {
        var problems = new List<string>();
        foreach (RowStateKind kind in Enum.GetValues<RowStateKind>())
        {
            CheckText(Copy.PlainBaseText(kind), "row state " + kind, problems);
        }

        foreach (string qualifier in new[] { "on an earlier build", "second half only on record", "shut down not confirmed", "run order not confirmed" })
        {
            CheckText(Copy.PlainQualifier(qualifier), "qualifier '" + qualifier + "'", problems);
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    public void NoAlwaysVisibleCopyStringContainsABannedWord()
    {
        var problems = new List<string>();
        foreach ((string name, string value) in AlwaysVisibleCopyStrings())
        {
            CheckText(value, "Copy." + name, problems);
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    // Every Copy member that is shown whether the toggle is on or off: this window's own words,
    // never the script's, so none of it is gated by the technical-details toggle. Constants read
    // by reflection (so a new one is covered automatically); the handful of methods are called
    // with representative arguments, since a banned word could just as easily be baked into a
    // format string as into a constant.
    private static IEnumerable<(string Name, string Value)> AlwaysVisibleCopyStrings()
    {
        var technicalOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Copy.NotRun), nameof(Copy.WaitingForShutDown), nameof(Copy.WaitingForRestart), nameof(Copy.Passed),
            nameof(Copy.Failed), nameof(Copy.Inconclusive), nameof(Copy.Unknown), nameof(Copy.StoppedBeforeAnyStep),
            nameof(Copy.Locked),
        };

        foreach (FieldInfo field in typeof(Copy).GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(string) || technicalOnly.Contains(field.Name))
            {
                continue;
            }

            if (field.GetValue(null) is string value)
            {
                yield return (field.Name, value);
            }
        }

        yield return (nameof(Copy.AtRestOnPurpose), Copy.AtRestOnPurpose("Because a live test needed it enabled."));
        yield return (nameof(Copy.HandOffText), Copy.HandOffText(PowerCycleRequirement.FullShutDown));
        yield return (nameof(Copy.HandOffText), Copy.HandOffText(PowerCycleRequirement.Restart));
        yield return (nameof(Copy.HandOffText), Copy.HandOffText(PowerCycleRequirement.AnyStart));
        yield return (nameof(Copy.FastStartupSentence), Copy.FastStartupSentence("on"));
        yield return (nameof(Copy.FastStartupSentence), Copy.FastStartupSentence("off"));
        yield return (nameof(Copy.FastStartupSentence), Copy.FastStartupSentence("unknown"));
        yield return (nameof(Copy.RunAllStoppedForPowerCycle), Copy.RunAllStoppedForPowerCycle("08"));
        yield return (nameof(Copy.RunAllStoppedForFailure), Copy.RunAllStoppedForFailure("08"));
        yield return (nameof(Copy.RunAllProgressLine), Copy.RunAllProgressLine(1, 19, "Connect with one click"));
        yield return (nameof(Copy.RunAllSummarySentence), Copy.RunAllSummarySentence(3, 1, 1, 1));
        yield return (nameof(Copy.PlainCheckSummarySentence), Copy.PlainCheckSummarySentence(4, 0, 0));
        yield return (nameof(Copy.PlainCheckSummarySentence), Copy.PlainCheckSummarySentence(5, 2, 1));
        yield return (nameof(Copy.LeftAtRestText), Copy.LeftAtRestText("yes", null));
        yield return (nameof(Copy.LeftAtRestText), Copy.LeftAtRestText("no", null));
        yield return (nameof(Copy.LeftAtRestText), Copy.LeftAtRestText("no-on-purpose", "Because a live test needed it enabled."));
        yield return (nameof(Copy.LeftAtRestText), Copy.LeftAtRestText("not-applicable", null));
        yield return (nameof(Copy.LeftAtRestText), Copy.LeftAtRestText("unknown", null));
        yield return (nameof(Copy.LeftAtRestText), Copy.LeftAtRestText(null, null));
    }

    private static void CheckText(string text, string source, List<string> problems)
    {
        string scanned = text;
        foreach (string allowed in AllowListedPhrases)
        {
            scanned = scanned.Replace(allowed, string.Empty, StringComparison.Ordinal);
        }

        foreach (string banned in Banned)
        {
            if (scanned.Contains(banned, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(source + " contains banned word '" + banned.Trim() + "': \"" + text + "\"");
            }
        }
    }
}
