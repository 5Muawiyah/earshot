using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The physical-action lines Show-Preconditions prints for 04, 08, 09 and 10 (wording.json's own
// Action entries) told the reader, in plain words, to "ask whoever set Earshot up to run the
// command this half shows you": true only outside the window (a genuine console route), but never
// true through it, since the window reads resume.txt itself and offers "Carry on with the second
// half" the moment the row is selected again. Nobody running through the window ever needs to
// type anything; the plain line must not invent a step the window does not actually ask of them.
[TestClass]
public sealed class ResumeActionWordingTests
{
    private static IReadOnlyList<WordingEntry> LoadWording() =>
        Wording.Load(Path.Combine(RepositoryLocator.RepositoryRoot(), "src", "Earshot.TestWindow", "Data", "wording.json"));

    [TestMethod]
    public void EveryResumeActionTellsTheReaderToCarryOnRatherThanRunAPrintedCommand()
    {
        IReadOnlyList<WordingEntry> wording = LoadWording();

        List<WordingEntry> resumeActions = wording
            .Where(e => e.Kind == WordingKind.Action && e.ScriptText.Contains("run the command", StringComparison.Ordinal))
            .ToList();

        // Sanity: this must find the four entries the fix touches (04, 08, 09, 10), not zero
        // (which would mean the fixture query itself stopped matching anything).
        Assert.AreEqual(4, resumeActions.Count, "expected one resume-action entry each for 04, 08, 09 and 10.");

        foreach (WordingEntry entry in resumeActions)
        {
            Assert.IsFalse(entry.Plain.Contains("ask whoever set Earshot up", StringComparison.Ordinal),
                "test " + entry.Test + ": the window carries on by itself; nobody needs to be asked to run a command for it.");
            Assert.IsFalse(entry.Plain.Contains("run the command", StringComparison.Ordinal),
                "test " + entry.Test + ": the plain line must not invent a console step the window never asks of the reader.");
            StringAssert.Contains(entry.Plain, Copy.CarryOnSecondHalfButtonLabel,
                "test " + entry.Test + ": the plain line must say what the window actually needs (select the row, choose Carry on with the second half).");
            StringAssert.Contains(entry.Plain, "You do not need to type anything");
        }
    }
}
