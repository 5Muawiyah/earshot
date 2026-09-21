using System.Drawing;
using System.Windows.Forms;
using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// the layout's Step view redesign: the checklist/instruction is a wrapping label that
// never clips (never a ListBox, which cannot wrap), the how-to picture is visible without
// scrolling at the default window size, the question sits directly above the answer buttons
// (fixing the named bug: it used to come first), the answer buttons are large, and no stray Enter
// or Space can answer a question nobody meant to answer. Real MainForm, real sandboxed scripts,
// real button clicks throughout: every scenario is driven the same way the rest of this suite
// already proves StepPanel's callers, not a presenter fact in isolation.
[TestClass]
public sealed class StepPanelLayoutTests
{
    // (a) The question sits below the checklist and directly above the answer buttons.
    //
    // Before this change: StepPanel laid every control out at a fixed Top/Left. Test 01's own
    // Show-Preconditions prompt put the question ("Are all of those true, and are you ready to
    // start?", PromptPresenter's own PlainLine for that prompt) at Top=32 and the checklist itself
    // (a ListBox) at Top=80: the question came before the list it asked about, the wrong way
    // round for a reader. Confirmed red against the unmodified file with a
    // throwaway probe before this test was written (not committed): body(ListBox).Bounds=
    // {Y=80,H=79}, question(Label).Bounds={Y=32,H=25} - the question's own bottom (57) sat above
    // the checklist's own top (80), the wrong way round.
    [TestMethod]
    public void TheQuestionSitsBelowTheChecklistAndDirectlyAboveTheAnswerButtons()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("01"));
            form.ClickStartForTests();
            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(120));
            Assert.IsNotNull(form.CurrentPromptSeqForTests, "Show-Preconditions never arrived.");

            StepPanel panel = form.StepPanelForTests;
            Application.DoEvents(); // let the just-built layout settle before reading Bounds.

            Control body = panel.BodyControlForTests;
            Control? question = panel.QuestionControlForTests;
            Control buttonRow = panel.ButtonRowForTests;

            Assert.IsNotNull(question, "test 01's own preconditions screen has both a checklist and a question about it.");
            StringAssert.Contains(panel.PlainLineTextForTests, "ready to start", "sanity: this must be test 01's Show-Preconditions prompt.");
            Assert.IsTrue(panel.ListItemsForTests.Count > 0, "sanity: this prompt must actually carry a checklist.");

            // All three controls are direct children of the same containing FlowLayoutPanel, so
            // their Bounds share one coordinate space and can be compared directly.
            Assert.IsLessThanOrEqualTo(question!.Top, body.Bottom,
                "the checklist (body, " + body.Bounds + ") must end at or above the question (" + question.Bounds + ").");
            Assert.IsLessThanOrEqualTo(buttonRow.Top, question.Bottom,
                "the question (" + question.Bounds + ") must end at or above the answer buttons (" + buttonRow.Bounds + ").");
        });
    }

    // (b) The how-to picture's bounds are inside the visible client area at the default window
    // size, for test 01's own first instruction (its Show-Preconditions physical action "Wear the
    // AirPods and play something from your phone..." names airpods-in-and-phone-playing).
    [TestMethod]
    public void HowToPictureFitsInsideTheClientAreaAtTheDefaultSizeForTest01()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            // The floor has to fit a small display, or Windows shrinks it and the window cannot be
            // used there: a hosted build machine with a 1024 by 768 screen showed exactly that.
            Assert.AreEqual(new Size(MainForm.MinimumWindowWidth, MainForm.MinimumWindowHeight), form.MinimumSize, "Windows changed the window's own floor, so it does not fit this display.");
            Assert.IsLessThanOrEqualTo(1024, MainForm.MinimumWindowWidth);
            Assert.IsLessThanOrEqualTo(720, MainForm.MinimumWindowHeight);

            Assert.IsTrue(form.SelectRowForTests("01"));
            form.ClickStartForTests();
            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(120));
            Assert.IsNotNull(form.CurrentPromptSeqForTests, "Show-Preconditions never arrived.");
            Application.DoEvents();

            StepPanel panel = form.StepPanelForTests;
            Assert.IsTrue(panel.HowToPictureVisibleForTests, "no picture was shown for test 01's own first physical action.");
            AssertControlFitsInsideForm(form, panel.HowToPictureControlForTests);
        });
    }

    // (c) ...and for the shut-down how-to. Investigation (PromptPresenter.cs, wording.json): the
    // "shut down, not restart" how-to (wording.json's own "shut-down-completely" block, picture
    // "start-power-shutdown") is named by test 08's SECOND physical action
    // ("Shut down, not restart. Wait about ten seconds with the machine off, then start it
    // again."), which is one of Show-Preconditions' own PhysicalActions
    // (08-AcceptancePowerCycle.ps1's own -PhysicalActions argument) - the very first StepPanel
    // screen, before the test starts, not the separate hand-off screen (_handOffBox), which carries
    // no how-to block of its own and is untouched by this commit. So this is the same StepPanel
    // scenario as (b), driven against row 08 instead of row 01, and needs no hand-off change.
    [TestMethod]
    public void HowToPictureFitsInsideTheClientAreaAtTheDefaultSizeForTest08sShutDownStep()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("08"));
            form.ClickStartForTests();
            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(120));
            Assert.IsNotNull(form.CurrentPromptSeqForTests, "Show-Preconditions never arrived.");
            Application.DoEvents();

            StepPanel panel = form.StepPanelForTests;
            StringAssert.Contains(panel.HowToStepsTextForTests, "Click Shut down. Do not click Restart.",
                "this must be test 08's own preconditions screen, carrying the shut-down-completely how-to.");
            Assert.IsTrue(panel.HowToPictureVisibleForTests, "no picture was shown for the shut-down-completely how-to.");
            AssertControlFitsInsideForm(form, panel.HowToPictureControlForTests);
        });
    }

    // (d) No label is clipped on Step for 01's preconditions, and the checklist is a real wrapping
    // label, never a scrolling box that clips.
    //
    // Before this change: the checklist was a fixed 560x80 ListBox (StepPanel.cs's own
    // _listItemsBox), which HomeViewNoClippedLabelsTests' own technique cannot even see (it only
    // ever scans Label controls), so the IsInstanceOfType assertion below is what turns genuinely
    // red on the unmodified file: BodyControlForTests was a ListBox, not a Label.
    [TestMethod]
    public void NoLabelIsClippedOnStepForTest01sPreconditionsAndTheChecklistNeverScrollsOrClips()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("01"));
            form.ClickStartForTests();
            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(120));
            Assert.IsNotNull(form.CurrentPromptSeqForTests, "Show-Preconditions never arrived.");
            Application.DoEvents();

            StepPanel panel = form.StepPanelForTests;
            Assert.IsInstanceOfType<Label>(panel.BodyControlForTests,
                "the checklist must be a wrapping label; a ListBox cannot wrap and clips a long line at its own fixed width.");
            Assert.IsFalse(panel.BodyControlForTests is ListBox, "never a scrolling box.");

            var offenders = new List<string>();
            CollectClippedLabels(form.StepPanelForTests, offenders);
            Assert.IsEmpty(offenders, "Clipped label(s) on Step:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        });
    }

    // (e) Font sizes: the plain line at least 14 pt, a representative body control at least 11 pt
    // (the layout: "Plain line font at least 14 pt, body at least 11 pt").
    [TestMethod]
    public void ThePlainLineIsAtLeast14PointAndTheBodyIsAtLeast11Point()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            StepPanel panel = form.StepPanelForTests;
            Assert.IsGreaterThanOrEqualTo(14f, panel.PlainLineFontSizeForTests, "the plain line must be at least 14 pt.");
            Assert.IsGreaterThanOrEqualTo(11f, panel.ChecklistFontSizeForTests, "the checklist body must be at least 11 pt.");
        });
    }

    // (f) Answer buttons: at least 44 px high, at least 13 pt.
    [TestMethod]
    public void AnswerButtonsAreAtLeast44PxHighAndAtLeast13Point()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("01"));
            form.ClickStartForTests();
            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(120));
            Assert.IsNotNull(form.CurrentPromptSeqForTests);

            IReadOnlyList<Button> buttons = form.StepPanelForTests.AnswerButtonsForTests;
            Assert.IsGreaterThan(0, buttons.Count, "Show-Preconditions must offer at least one answer button.");
            foreach (Button button in buttons)
            {
                Assert.IsGreaterThanOrEqualTo(44, button.Height, "button \"" + button.Text + "\" is only " + button.Height + " px high.");
                Assert.IsGreaterThanOrEqualTo(13f, button.Font.SizeInPoints, "button \"" + button.Text + "\" is only " + button.Font.SizeInPoints + " pt.");
            }
        });
    }

    // (g) No default button: Enter and Space never answer by accident.
    //
    // Investigation (a throwaway probe against the real, unmodified StepPanel/MainForm, not
    // committed): Form.AcceptButton is never set anywhere in MainForm.cs, so the one unconditional
    // WinForms mechanism that turns a stray Enter into a click, regardless of focus, never applies
    // here - confirmed by the AcceptButton assertion below, which was already green before this
    // change and stays green after it, recorded as evidence the risk is not that mechanism.
    // The same probe found real Control.Focused already false on every answer button once a new
    // prompt's buttons replace the old ones (WinForms' own Controls.Clear() drops it, with nothing
    // in this codebase's own code asking for that), so a plain "is a button focused" assertion
    // would already have been green on the unmodified file - not a genuine red-before-green test of
    // anything this change does. What the same probe DID find still true on the unmodified file,
    // even after the 800 ms click-safety delay had re-enabled the new prompt's own buttons with no
    // deliberate Tab or click in between: Form.ActiveControl (ContainerControl's own "which branch
    // of the tree last had focus" bookkeeping) stayed pointed at the button row itself
    // (activeControlAfterDelay=FlowLayoutPanel), left there by the previous prompt's real button
    // click and never cleared for the new one. That stale pointer is the real, current, named risk
    // ("focus is still sitting somewhere generic" after a prompt refreshes): this is what
    // StepPanel.ClearStaleActiveControl now clears every time Show() rebuilds the row, which is
    // what turns the assertion below genuinely red on the unmodified file and green after the fix.
    [TestMethod]
    public void NoStaleFocusIsLeftAimedAtTheAnswerButtonsWhenANewPromptAppears()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsNull(form.AcceptButton, "no default button: Form.AcceptButton must never be set.");

            // Row 13 (the same row WaitOwnerNoTakesYourTimeTests already drives): its
            // Show-Preconditions prompt leads to a real Wait-Owner step, a second, genuinely
            // different prompt with its own, freshly built button row.
            Assert.IsTrue(form.SelectRowForTests("13"));
            form.ClickStartForTests();
            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(120));
            int? firstSeq = form.CurrentPromptSeqForTests;
            Assert.IsNotNull(firstSeq, "Show-Preconditions never arrived.");

            // Wait out the click-safety delay so the buttons are genuinely reachable, then
            // deliberately focus the first one (the same state a real Tab keypress, which must
            // stay allowed, would leave behind) and click it for real, the deliberate, allowed way
            // to answer.
            MainFormTestHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(900));
            StepPanel panel = form.StepPanelForTests;
            panel.FocusFirstAnswerButtonForTests();
            Assert.IsTrue(panel.AnyAnswerButtonFocusedForTests, "sanity: the deliberate focus must have actually landed on the button.");

            form.ClickCurrentPromptButtonForTests();
            MainFormTestHarness.PumpUntil(
                () => form.CurrentPromptSeqForTests is not null && form.CurrentPromptSeqForTests != firstSeq,
                TimeSpan.FromSeconds(30));
            Assert.IsNotNull(form.CurrentPromptSeqForTests, "the Wait-Owner step never arrived.");
            Assert.AreNotEqual(firstSeq, form.CurrentPromptSeqForTests);

            // Wait out the new prompt's own click-safety delay too: the stale pointer this test
            // proves was cleared survived past it on the unmodified file (activeControlAfterDelay
            // was still the button row after this same wait), so the assertion below is not merely
            // catching a timing coincidence.
            MainFormTestHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(1000));

            Assert.IsFalse(panel.AnyAnswerButtonFocusedForTests, "no answer button should hold real focus with no deliberate Tab or click.");
            Assert.IsFalse(
                ReferenceEquals(form.ActiveControl, panel.ButtonRowForTests),
                "Form.ActiveControl must not be left pointing at the answer button row after a new prompt appears; " +
                "a stray Enter or Space could resume answering from state nobody chose.");
        });
    }

    // The Step view's own progress line (point 1): shown for an ordinary single-test start, not
    // only for Run all, using the exact position RunAllOrder.Items itself gives this row.
    [TestMethod]
    public void TheProgressLineShowsThisRowsOwnPositionForAnOrdinarySingleTestStart()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsFalse(form.RunAllActiveForTests, "sanity: this must be an ordinary single-test start, not Run all.");
            Assert.IsTrue(form.SelectRowForTests("03"));
            form.ClickStartForTests();
            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(120));
            Assert.IsNotNull(form.CurrentPromptSeqForTests, "Show-Preconditions never arrived.");

            int expectedIndex = -1;
            for (int i = 0; i < RunAllOrder.Items.Count; i++)
            {
                if (RunAllOrder.Items[i].RowNumber == "03")
                {
                    expectedIndex = i;
                    break;
                }
            }

            Assert.AreNotEqual(-1, expectedIndex, "sanity: row 03 must be in Run all's own order.");

            string repoRoot = RepositoryLocator.RepositoryRoot();
            var rows = Manifest.Load(Path.Combine(repoRoot, "src", "Earshot.TestWindow", "Data", "tests.json"));
            DisplayRow row03 = DisplayRow.Flatten(rows).First(r => r.Number == "03");

            StepPanel panel = form.StepPanelForTests;
            Assert.IsTrue(panel.ProgressVisibleForTests, "the progress line must show for an ordinary single-test start too, not only Run all.");
            Assert.AreEqual("Test " + (expectedIndex + 1) + " of " + RunAllOrder.Items.Count + " · " + row03.Name,
                panel.ProgressTextForTests);
        });
    }

    // Row 00 Restore and the administrator prompt check are not in RunAllOrder.Items at all
    // (RunAllOrder.cs's own remark: 00 is the manual escape hatch, never part of the sequence);
    // the progress line must never fabricate a position for either.
    [TestMethod]
    public void TheProgressLineIsHiddenForARowOutsideRunAllsOwnOrder()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            Assert.IsTrue(form.SelectRowForTests("00"));
            form.ClickStartForTests();
            MainFormTestHarness.PumpUntil(() => form.CurrentPromptSeqForTests is not null, TimeSpan.FromSeconds(120));
            Assert.IsNotNull(form.CurrentPromptSeqForTests, "row 00's first prompt never arrived.");

            Assert.IsFalse(form.StepPanelForTests.ProgressVisibleForTests, "row 00 has no position in Run all's own order and must show no progress line.");
        });
    }

    private static void AssertControlFitsInsideForm(MainForm form, Control control)
    {
        Point formRelative = form.PointToClient(control.PointToScreen(Point.Empty));
        var bounds = new Rectangle(formRelative, control.Size);
        var client = new Rectangle(Point.Empty, form.ClientSize);
        Assert.IsTrue(client.Contains(bounds),
            "control bounds " + bounds + " are not entirely inside the form's own client area " + client + ".");
    }

    // Same technique as HomeViewNoClippedLabelsTests: Label.GetPreferredSize is the exact
    // calculation WinForms itself uses to size an AutoSize label, so asking it for the size needed
    // at this label's own current width is the direct definition of not clipped.
    //
    // Unlike Home's own collapsed More section (whose controls sit inside a panel that is itself
    // still a direct, always-materialised child of the visible Home view), StepPanel's own
    // technical-details controls are invisible-and-never-laid-out while the toggle is off: nested
    // two FlowLayoutPanels deep inside an invisible branch, WinForms never runs their AutoSize pass
    // at all, so their Bounds stay at the designer's own default (100x23) rather than anything a
    // real clip check could read honestly. Invisible controls are skipped here for that reason;
    // this test's own job is "nothing currently on screen is clipped", and technical details off
    // means that whole panel is not on screen to begin with.
    private static void CollectClippedLabels(Control root, List<string> offenders)
    {
        if (!root.Visible)
        {
            return;
        }

        foreach (Control child in root.Controls)
        {
            if (!child.Visible)
            {
                continue;
            }

            if (child is Label label && !string.IsNullOrEmpty(label.Text) && label.Width > 0 && label.Height > 0)
            {
                Size needed = label.GetPreferredSize(new Size(label.Width, 0));
                if (needed.Width > label.Width + 4 || needed.Height > label.Height + 4)
                {
                    string shortText = label.Text.Length > 40 ? string.Concat(label.Text.AsSpan(0, 40), "...") : label.Text;
                    offenders.Add(
                        (string.IsNullOrEmpty(label.Name) ? label.GetType().Name : label.Name) + " (\"" + shortText + "\"): needs " +
                        needed + " but has " + label.Size);
                }
            }

            CollectClippedLabels(child, offenders);
        }
    }
}
