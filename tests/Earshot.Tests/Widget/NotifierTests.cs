using Earshot.Contracts;
using Earshot.Widget.Alert;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

[TestClass]
public sealed class NotifierTests
{
    // No real shortcut is ever written by a test: this fake stands in for RealShellLinkWriter.
    private sealed class FakeShellLinkWriter : IShellLinkWriter
    {
        public List<(string ShortcutPath, string TargetPath, string AppUserModelId)> Calls { get; } = new();

        public StepOutcome Result { get; set; } = StepOutcomes.FromHResult("fake-shortcut", 0);

        public StepOutcome WriteShortcut(string shortcutPath, string targetPath, string appUserModelId)
        {
            Calls.Add((shortcutPath, targetPath, appUserModelId));
            return Result;
        }
    }

    [TestMethod]
    public void TheShortcutPathAndIdAreFixed()
    {
        // Read through reflection, not as two literals, so the comparison is not folded away at compile
        // time and genuinely reads what the type declares.
        object? appUserModelId = typeof(NotificationRegistration).GetField(nameof(NotificationRegistration.AppUserModelId))!.GetRawConstantValue();
        object? shortcutFileName = typeof(NotificationRegistration).GetField(nameof(NotificationRegistration.ShortcutFileName))!.GetRawConstantValue();
        Assert.AreEqual("5Muawiyah.Earshot", appUserModelId);
        Assert.AreEqual("Earshot.lnk", shortcutFileName);

        var registration = new NotificationRegistration(
            new FakeShellLinkWriter(), new CapturingLog(), safeMode: false, redirected: false,
            shortcutFolder: @"C:\some\folder", runningExePath: @"C:\some\Earshot.exe");

        Assert.AreEqual(System.IO.Path.Combine(@"C:\some\folder", "Earshot.lnk"), registration.ShortcutPath);
    }

    [TestMethod]
    public void RegistrationIsSkippedInSafeMode()
    {
        var writer = new FakeShellLinkWriter();
        var log = new CapturingLog();
        var registration = new NotificationRegistration(
            writer, log, safeMode: true, redirected: false, shortcutFolder: @"C:\folder", runningExePath: @"C:\Earshot.exe");

        StepOutcome step = registration.Register();

        Assert.IsFalse(step.Ok);
        Assert.AreEqual(0, writer.Calls.Count);
        Assert.IsTrue(log.Has(LogLevel.Warn, "Safe mode"));
    }

    [TestMethod]
    public void RegistrationIsSkippedWithARedirectedDataRoot()
    {
        var writer = new FakeShellLinkWriter();
        var log = new CapturingLog();
        var registration = new NotificationRegistration(
            writer, log, safeMode: false, redirected: true, shortcutFolder: @"C:\folder", runningExePath: @"C:\Earshot.exe");

        StepOutcome step = registration.Register();

        Assert.IsFalse(step.Ok);
        Assert.AreEqual(0, writer.Calls.Count);
        Assert.IsTrue(log.Has(LogLevel.Warn, "Test data folder"));
    }

    [TestMethod]
    public async Task AShowFailureIsLoggedWithItsHResultAndFallsBackToTheCard()
    {
        var fallback = new FakeNotifier();
        var log = new CapturingLog();
        var failing = StepOutcomes.FromHResult("toast-show", unchecked((int)0x80004005), detail: "InvalidOperationException", ok: false);
        var toast = new ToastNotifier("5Muawiyah.Earshot", fallback, log, (title, text) => failing);

        StepOutcome step = await toast.NotifyAsync("Earshot", "Left AirPod at 20%");

        Assert.IsFalse(step.Ok);
        Assert.AreEqual(1, fallback.Calls.Count);
        Assert.AreEqual(("Earshot", "Left AirPod at 20%"), fallback.Calls[0]);
        Assert.AreEqual(1, toast.AlertsFallenBackToCard);

        // The real Show() path's own failure message shape, proved directly: no test may trigger the real
        // WinRT call failing, since that depends on this machine's own notifier state.
        var ex = new InvalidOperationException("no notifier registered");
        string message = ToastNotifier.DescribeFailure(ex);
        StringAssert.Contains(message, nameof(InvalidOperationException));
        StringAssert.Contains(message, "0x");
    }

    [TestMethod]
    public void TheTargetPrefersTheInstalledExe()
    {
        const string installed = @"C:\installed\Earshot.exe";
        const string running = @"C:\running\Earshot.exe";
        var writer = new FakeShellLinkWriter();

        var whenInstalledExists = new NotificationRegistration(
            writer, new CapturingLog(), safeMode: false, redirected: false, shortcutFolder: @"C:\folder",
            runningExePath: running, installedExePath: installed, fileExists: path => path == installed);
        Assert.AreEqual(installed, whenInstalledExists.TargetExePath);

        var whenInstalledMissing = new NotificationRegistration(
            writer, new CapturingLog(), safeMode: false, redirected: false, shortcutFolder: @"C:\folder",
            runningExePath: running, installedExePath: installed, fileExists: _ => false);
        Assert.AreEqual(running, whenInstalledMissing.TargetExePath);
    }
}
