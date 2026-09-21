using System.Windows.Forms;
using Earshot.TestWindow.Core;
using Earshot.TestWindow.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// When Windows ends the session while a half is
// running, the form cancels the close once; if the owner forces it, the child dies with the
// session. Windows sends WM_QUERYENDSESSION and needs a synchronous answer from FormClosing; a
// modal MessageBox does not answer it, it blocks the answer. OnFormClosing.ConfirmDialogForTests
// is a seam substituted here so this proves what the real caller decided (call the dialog, or
// not) without ever putting a real, undismissable dialog on screen: nothing in an automated run
// can safely click it, and MessageBox.Show's own modal loop does not pump this thread's queue,
// so a wrongly-shown one would hang the test rather than fail it cleanly.
[TestClass]
public sealed class WindowsShutdownCloseTests
{
    [TestMethod]
    public void AWindowsShutDownWithARunActiveCancelsOnceWithNoDialogAndRenamesTheWindow()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            int dialogCalls = 0;
            form.ConfirmDialogForTests = _ => { dialogCalls++; return DialogResult.Yes; };

            Assert.IsTrue(form.SelectRowForTests("01"));
            form.ClickStartForTests();
            Assert.IsNotNull(form.ActiveRunnerForTests, "no child started.");

            FormClosingEventArgs first = form.RaiseFormClosingForTests(CloseReason.WindowsShutDown);

            Assert.IsTrue(first.Cancel, "the first Windows shutdown attempt during a run must cancel the close.");
            Assert.AreEqual(0, dialogCalls, "no modal dialog may be shown on the Windows shutdown path: it blocks WM_QUERYENDSESSION rather than answering it.");
            StringAssert.Contains(form.Text, "a test is still running");

            // section 4.4: "if the owner forces it" - a second attempt must not cancel again,
            // or the window could make itself the one thing blocking a shutdown forever.
            FormClosingEventArgs second = form.RaiseFormClosingForTests(CloseReason.WindowsShutDown);
            Assert.IsFalse(second.Cancel, "a second Windows shutdown attempt must be let through.");
            Assert.AreEqual(0, dialogCalls, "still no dialog on the second attempt.");
        });
    }

    [TestMethod]
    public void AWindowsShutDownWithNoRunActiveIsLetThroughWithNoDialog()
    {
        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            int dialogCalls = 0;
            form.ConfirmDialogForTests = _ => { dialogCalls++; return DialogResult.Yes; };

            FormClosingEventArgs args = form.RaiseFormClosingForTests(CloseReason.WindowsShutDown);

            Assert.IsFalse(args.Cancel);
            Assert.AreEqual(0, dialogCalls);
        });
    }

    // An ordinary user-initiated close (CloseReason.UserClosing, an owner clicking the window's
    // own X) is unaffected by any of this: it must still ask, exactly as before.
    [TestMethod]
    public void AnOrdinaryUserCloseWithARunActiveStillAsksThroughTheDialog()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        using var sandbox = new TempFolder();
        MainFormTestHarness.Run(sandbox.Path, form =>
        {
            int dialogCalls = 0;
            form.ConfirmDialogForTests = _ => { dialogCalls++; return DialogResult.No; };

            Assert.IsTrue(form.SelectRowForTests("01"));
            form.ClickStartForTests();

            FormClosingEventArgs args = form.RaiseFormClosingForTests(CloseReason.UserClosing);

            Assert.AreEqual(1, dialogCalls, "an ordinary close must still ask before stopping a run.");
            Assert.IsTrue(args.Cancel, "answering No must cancel the close.");
        });
    }
}
