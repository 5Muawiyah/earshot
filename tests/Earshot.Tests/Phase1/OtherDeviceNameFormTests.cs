using System.Windows.Forms;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase1;

// OtherDeviceNameForm, the picker-style modal for WidgetSettings.OtherDeviceLabel: built and inspected on
// its own STA thread (StaThread, from Phase1Fixtures), the same way TrayMenuTests builds a real
// ContextMenuStrip, and never shown (no Show(), no ShowDialog()), per the hard safety rule that no test
// draws a window on the real desktop.
[TestClass]
public sealed class OtherDeviceNameFormTests
{
    [TestMethod]
    public void TheCaptionIsExactlyTheSharedSentence()
    {
        StaThread.Run(() =>
        {
            using var form = new OtherDeviceNameForm("iPhone");

            Assert.AreEqual(WidgetCopy.OtherDeviceCaption, form.CaptionText);
        });
    }

    [TestMethod]
    public void TheFieldsCapMatchesTheSettingsConstant()
    {
        StaThread.Run(() =>
        {
            using var form = new OtherDeviceNameForm("iPhone");

            Assert.AreEqual(WidgetSettings.MaxOtherDeviceLabelLength, form.FieldMaxLength);
        });
    }

    // TextBox.MaxLength stops typing or pasting past the cap, but does not touch a value assigned to Text
    // directly, so Label() must cap again itself: without that, a value set programmatically (an IME commit,
    // or exactly what LabelTextForTest simulates here) would sail through to the setting uncapped.
    [TestMethod]
    public void LabelCapsTextThatWasSetPastTheLimit()
    {
        StaThread.Run(() =>
        {
            using var form = new OtherDeviceNameForm("iPhone");
            form.SetLabelTextForTest(new string('x', WidgetSettings.MaxOtherDeviceLabelLength + 10));

            string label = form.Label();

            Assert.AreEqual(WidgetSettings.MaxOtherDeviceLabelLength, label.Length);
            Assert.AreEqual(new string('x', WidgetSettings.MaxOtherDeviceLabelLength), label);
        });
    }

    // A paste can carry characters the settings store would remove, or end past the cap in the middle of a
    // pair. What leaves the form is what the store keeps, so the two never disagree about the label.
    [TestMethod]
    public void LabelIsCleanedAsTheSettingsStoreCleansIt()
    {
        StaThread.Run(() =>
        {
            char rightToLeftOverride = (char)0x202E;
            char zeroWidthSpace = (char)0x200B;
            char lineSeparator = (char)0x2028;
            string smile = char.ConvertFromUtf32(0x1F600);
            using var form = new OtherDeviceNameForm("iPhone");
            string pasted = rightToLeftOverride + "Sam" + zeroWidthSpace + "'s phone" + lineSeparator + new string('x', 36) + smile;
            form.SetLabelTextForTest(pasted);

            string label = form.Label();

            string keptByTheStore = (WidgetSettings.Default with { OtherDeviceLabel = pasted }).Clamped(out _).OtherDeviceLabel;
            Assert.AreEqual(keptByTheStore, label);
            Assert.IsFalse(label.Contains(rightToLeftOverride));
            Assert.IsFalse(label.Contains(zeroWidthSpace));
            Assert.IsFalse(label.Contains(lineSeparator));
            Assert.IsFalse(label.Any(char.IsSurrogate), "The emoji does not fit, so it is left out whole.");
        });
    }

    [TestMethod]
    public void LabelTrimsWhitespace()
    {
        StaThread.Run(() =>
        {
            using var form = new OtherDeviceNameForm("iPhone");
            form.SetLabelTextForTest("  Sam's iPad  ");

            Assert.AreEqual("Sam's iPad", form.Label());
        });
    }

    [TestMethod]
    public void LabelStartsAsTheCurrentSetting()
    {
        StaThread.Run(() =>
        {
            using var form = new OtherDeviceNameForm("Work laptop");

            Assert.AreEqual("Work laptop", form.Label());
        });
    }

    // An empty label is a valid choice (WidgetCopy.OnElsewhere falls back to "On another device" for it),
    // so there is nothing here for OK to refuse the way DevicePickerForm's OK refuses an unusable pick.
    [TestMethod]
    public void AnEmptyLabelIsAccepted()
    {
        StaThread.Run(() =>
        {
            using var form = new OtherDeviceNameForm("");
            form.SetLabelTextForTest("   ");

            Assert.AreEqual("", form.Label());
        });
    }

    [TestMethod]
    public void OkAndCancelAreWiredLikeDevicePickerForms()
    {
        StaThread.Run(() =>
        {
            using var form = new OtherDeviceNameForm("iPhone");

            Assert.IsInstanceOfType<Button>(form.AcceptButton);
            Assert.AreEqual(DialogResult.OK, ((Button)form.AcceptButton!).DialogResult);
            Assert.AreEqual(OtherDeviceNameForm.OkText, ((Button)form.AcceptButton!).Text);

            Assert.IsInstanceOfType<Button>(form.CancelButton);
            Assert.AreEqual(DialogResult.Cancel, ((Button)form.CancelButton!).DialogResult);
            Assert.AreEqual(OtherDeviceNameForm.CancelText, ((Button)form.CancelButton!).Text);
        });
    }

    [TestMethod]
    public void TheTitleIsTheSharedOtherDeviceNameTitle()
    {
        StaThread.Run(() =>
        {
            using var form = new OtherDeviceNameForm("iPhone");

            Assert.AreEqual(WidgetCopy.OtherDeviceNameTitle, form.Text);
        });
    }
}
