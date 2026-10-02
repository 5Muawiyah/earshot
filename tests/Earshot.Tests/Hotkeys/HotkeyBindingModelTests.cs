using Earshot.Hotkeys;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Hotkeys;

// The model a settings page binds a shortcut box to: read, set, clear and "did it fail to register". The
// registration outcomes come from a real HotkeyManager over the fake window and native calls.
[TestClass]
public sealed class HotkeyBindingModelTests
{
    private static (HotkeyBindingModel Model, HotkeySettings Settings, HotkeyManager Manager, FakeNativeHotkeys Native) Build()
    {
        var settings = new HotkeySettings();
        var native = new FakeNativeHotkeys();
        var manager = new HotkeyManager(new FakeMessageWindow(), native, new CapturingLog());
        return (new HotkeyBindingModel(settings, () => manager.CurrentOutcomes), settings, manager, native);
    }

    [TestMethod]
    public void TheChordIsTheStoredTextAndSetStoresAValidOneAsTyped()
    {
        (HotkeyBindingModel model, HotkeySettings settings, _, _) = Build();
        Assert.AreEqual("Ctrl+Alt+Shift+A", model.Chord(HotkeyAction.SwitchToPc));

        string? refusal = model.Set(HotkeyAction.SwitchToPc, "ctrl+alt+p");

        Assert.IsNull(refusal);
        Assert.AreEqual("ctrl+alt+p", settings.SwitchToPc, "The text is kept as typed.");
        Assert.AreEqual("ctrl+alt+p", model.Chord(HotkeyAction.SwitchToPc));
    }

    [TestMethod]
    public void ARefusedSetStoresNothingAndSaysWhy()
    {
        (HotkeyBindingModel model, HotkeySettings settings, _, _) = Build();

        Assert.AreEqual(HotkeyText.NoKeyMessage, model.Set(HotkeyAction.SwitchToPc, "Ctrl"));
        Assert.AreEqual(HotkeyManager.F12RefusedMessage, model.Set(HotkeyAction.SwitchToPc, "Ctrl+F12"));
        Assert.AreEqual("Ctrl+Alt+Shift+D is already set for another command here.", model.Set(HotkeyAction.SwitchToPc, "Ctrl+Alt+Shift+D"));

        Assert.AreEqual(HotkeySettings.DefaultSwitchToPc, settings.SwitchToPc, "A refusal must leave the working shortcut as it was.");
    }

    [TestMethod]
    public void ClearLeavesTheCommandWithNoShortcutAndRegistersNothingForIt()
    {
        (HotkeyBindingModel model, HotkeySettings settings, HotkeyManager manager, FakeNativeHotkeys native) = Build();

        model.Clear(HotkeyAction.SwitchToPhone);
        manager.Apply(settings);

        Assert.AreEqual(string.Empty, model.Chord(HotkeyAction.SwitchToPhone));
        Assert.IsFalse(model.RegistrationFailed(HotkeyAction.SwitchToPhone), "No chord is not a failure.");
        Assert.AreEqual(2, native.Calls.Count(c => c.Method == "RegisterHotKey"), "Only the connect and card shortcuts should be registered.");
    }

    [TestMethod]
    public void RegistrationFailedReportsAChordAnotherProgramHolds()
    {
        (HotkeyBindingModel model, HotkeySettings settings, HotkeyManager manager, FakeNativeHotkeys native) = Build();
        Assert.IsFalse(model.RegistrationFailed(HotkeyAction.SwitchToPc), "Nothing has been applied yet.");
        native.SetResult(HotkeyManager.HotkeyIdBase + (int)HotkeyAction.SwitchToPc, NativeCallResult.Failure(1409));

        manager.Apply(settings);

        Assert.IsTrue(model.RegistrationFailed(HotkeyAction.SwitchToPc));
        StringAssert.Contains(model.FailureMessage(HotkeyAction.SwitchToPc)!, "1409");
        Assert.IsFalse(model.RegistrationFailed(HotkeyAction.SwitchToPhone));
        Assert.IsNull(model.FailureMessage(HotkeyAction.SwitchToPhone));
    }

    [TestMethod]
    public void ShortcutsSwitchedOffIsNotAFailure()
    {
        (HotkeyBindingModel model, HotkeySettings settings, HotkeyManager manager, _) = Build();
        settings.Enabled = false;

        manager.Apply(settings);

        Assert.IsFalse(model.RegistrationFailed(HotkeyAction.SwitchToPc));
    }
}
