using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The row list used to allow multi-select (ListView's own default), so a ctrl or shift click
// left two rows selected at once and Start acted on whichever happened to be
// SelectedIndices[0], the lowest index, never necessarily the one the owner meant. Real MainForm,
// a real ListView with a real handle: a single-select control clears an earlier selection on its
// own the moment a second item is marked Selected, which is the actual mechanism this proves,
// not a copy of it.
[TestClass]
public sealed class SingleSelectTests
{
    [TestMethod]
    public void SelectingASecondRowClearsTheFirst()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("01"));
            Assert.AreEqual(1, form.SelectedIndexCountForTests);

            form.SelectAdditionalRowForTests("02");

            Assert.AreEqual(1, form.SelectedIndexCountForTests,
                "the row list allowed two rows to stay selected at once; Start would act on the lowest, not necessarily the one clicked.");
        });
    }
}
