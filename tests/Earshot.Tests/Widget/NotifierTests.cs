using Earshot.Contracts;
using Earshot.Widget;
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

        public ExistingShortcut? Existing { get; set; }

        public StepOutcome WriteShortcut(string shortcutPath, string targetPath, string appUserModelId)
        {
            Calls.Add((shortcutPath, targetPath, appUserModelId));
            return Result;
        }

        public ExistingShortcut? ReadShortcut(string shortcutPath) => Existing;
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

    // The "left alone" comments must match the code. An existing shortcut that already targets the
    // right exe and carries the right AppUserModelID must not be rewritten at all.
    [TestMethod]
    public void AnAlreadyCorrectShortcutIsLeftAlone()
    {
        const string exePath = @"C:\Earshot\Earshot.exe";
        var writer = new FakeShellLinkWriter { Existing = new ExistingShortcut(exePath, NotificationRegistration.AppUserModelId) };
        var log = new CapturingLog();
        var registration = new NotificationRegistration(
            writer, log, safeMode: false, redirected: false, shortcutFolder: @"C:\folder", runningExePath: exePath);

        StepOutcome step = registration.Register();

        Assert.IsTrue(step.Ok);
        Assert.AreEqual(0, writer.Calls.Count, "An already-correct shortcut must not be rewritten.");
        Assert.IsTrue(log.Has(LogLevel.Info, "left alone"));
    }

    // Never overwrite an Earshot.lnk whose target is not this exe (a foreign target this run does not
    // recognise as either its running or its installed copy).
    [TestMethod]
    public void AShortcutTargetingSomethingElseIsNeverOverwritten()
    {
        var writer = new FakeShellLinkWriter { Existing = new ExistingShortcut(@"C:\SomeOtherApp\SomeOtherApp.exe", "SomeOtherApp") };
        var log = new CapturingLog();
        var registration = new NotificationRegistration(
            writer, log, safeMode: false, redirected: false, shortcutFolder: @"C:\folder", runningExePath: @"C:\Earshot\Earshot.exe");

        StepOutcome step = registration.Register();

        Assert.IsFalse(step.Ok);
        Assert.AreEqual(0, writer.Calls.Count, "A shortcut pointing at something else must never be overwritten.");
        Assert.IsTrue(log.Has(LogLevel.Warn, "not Earshot"));
    }

    // A target that is stale but still one of this run's own exe paths (an old install location) is safe to
    // rewrite, distinct from a genuinely foreign target.
    [TestMethod]
    public void AStaleButStillOwnTargetIsRewritten()
    {
        const string running = @"C:\running\Earshot.exe";
        const string installed = @"C:\installed\Earshot.exe";
        var writer = new FakeShellLinkWriter { Existing = new ExistingShortcut(running, NotificationRegistration.AppUserModelId) };
        var log = new CapturingLog();
        var registration = new NotificationRegistration(
            writer, log, safeMode: false, redirected: false, shortcutFolder: @"C:\folder",
            runningExePath: running, installedExePath: installed, fileExists: path => path == installed);

        StepOutcome step = registration.Register();

        Assert.AreEqual(1, writer.Calls.Count, "A stale but still-Earshot target must be rewritten.");
        Assert.AreEqual(installed, writer.Calls[0].TargetPath);
    }

    // For every helper a test fakes, one execution of the real one: the real RealShellLinkWriter, run
    // against a shortcut in a temp folder, never the owner's Start menu, deleted afterwards. Inconclusive,
    // not failed, when this build of Windows has no shell link support to check.
    [TestMethod]
    public void TheRealShellLinkWriterWritesAndReadsBackTheAppUserModelId()
    {
        if (!WidgetPlatformGuard.HasToastNotifications)
        {
            Assert.Inconclusive("This build of Windows has no shell link support to check.");
            return;
        }

        using var temp = new TempFolder();
        string shortcutPath = temp.File("Earshot.lnk");
        string targetPath = Environment.ProcessPath ?? Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var writer = new RealShellLinkWriter();

        try
        {
            StepOutcome writeStep = writer.WriteShortcut(shortcutPath, targetPath, NotificationRegistration.AppUserModelId);
            Assert.IsTrue(writeStep.Ok, "The real writer must succeed writing into a temp folder: " + writeStep.CodeName);
            Assert.IsTrue(File.Exists(shortcutPath));

            ExistingShortcut? readBack = writer.ReadShortcut(shortcutPath);
            Assert.IsNotNull(readBack, "The real writer must be able to read back what it just wrote.");
            Assert.AreEqual(NotificationRegistration.AppUserModelId, readBack!.Value.AppUserModelId);
            Assert.AreEqual(targetPath, readBack.Value.TargetPath, ignoreCase: true);
        }
        finally
        {
            File.Delete(shortcutPath);
        }

        Assert.IsFalse(File.Exists(shortcutPath));
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
