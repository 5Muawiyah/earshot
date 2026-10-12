using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Install;
using Earshot.Tray;
using Earshot.Widget.Alert;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Install;

// uninstall-ui and repair-ui against a fake launcher, a fake Run value and a fake shortcut: no administrator prompt is raised
// and nothing outside the test is touched.
[TestClass]
public sealed class UiVerbsTests
{
    private const string Exe = @"C:\Program Files\Earshot\Earshot.exe";
    private const string Shortcut = @"C:\Users\x\Start\Earshot.lnk";
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private sealed class FakeRegistry : IStartupRegistry
    {
        public string? Run { get; set; }

        public bool Fail { get; set; }

        public string? ReadRunValue(string name) => Run;

        public byte[]? ReadStartupApproved(string name) => null;

        public void WriteRunValue(string name, string command) => Run = command;

        public void DeleteRunValue(string name)
        {
            if (Fail)
            {
                throw new UnauthorizedAccessException("denied");
            }

            Run = null;
        }
    }

    private sealed class FakeLinks : IShellLinkWriter
    {
        public ShortcutRead Read { get; set; } = new(ShortcutReadStatus.NotFound, null, null);

        public StepOutcome WriteShortcut(string shortcutPath, string targetPath, string appUserModelId) => throw new AssertFailedException("Never written.");

        public ShortcutRead ReadShortcut(string shortcutPath) => Read;
    }

    private sealed class FakeLauncher : IElevatedLauncher
    {
        public List<(string Exe, string Args)> Runs { get; } = [];

        public ElevatedRun Result { get; set; } = new(0, StepOutcomes.FromWin32("runas", 0));

        public Task<ElevatedRun> RunAsync(string executable, string arguments, CancellationToken ct)
        {
            Runs.Add((executable, arguments));
            return Task.FromResult(Result);
        }
    }

    private sealed class Messages : IUiVerbMessages
    {
        public List<string> Shown { get; } = [];

        public void Show(string text) => Shown.Add(text);
    }

    private sealed class Rig
    {
        public FakeRegistry Registry { get; } = new() { Run = StartupRegistration.CommandFor(Exe) };

        public FakeLinks Links { get; } = new() { Read = new(ShortcutReadStatus.Ok, new ExistingShortcut(Exe, "x"), null) };

        public FakeLauncher Launcher { get; } = new();

        public Messages Messages { get; } = new();

        public List<string> Deleted { get; } = [];

        public UiVerbs Verbs => new(Launcher, Cleanup, Exe, Messages, new CapturingLog());

        public PerUserCleanup Cleanup => new(Registry, Links, Shortcut, Deleted.Add);
    }

    [TestMethod]
    public void UninstallRaisesThePromptForTheInstalledProgramThenRemovesTheOwnPerUserEntries()
    {
        var rig = new Rig();

        int code = rig.Verbs.Uninstall();

        Assert.AreEqual(0, code);
        Assert.HasCount(1, rig.Launcher.Runs);
        Assert.AreEqual((Exe, "uninstall"), rig.Launcher.Runs[0]);
        Assert.IsNull(rig.Registry.Run, "Earshot's own Open on startup value is gone.");
        Assert.AreEqual(Shortcut, string.Join("|", rig.Deleted));
        Assert.IsEmpty(rig.Messages.Shown);
    }

    [TestMethod]
    public void ADeclinedPromptChangesNothingAndSaysSo()
    {
        var rig = new Rig();
        rig.Launcher.Result = new ElevatedRun(null, StepOutcomes.FromWin32("runas", 1223, "cancelled"));

        int code = rig.Verbs.Uninstall();

        Assert.AreEqual(1223, code);
        Assert.IsNotNull(rig.Registry.Run, "The value stays until an uninstall has actually finished.");
        Assert.IsEmpty(rig.Deleted);
        Assert.AreEqual("Nothing was changed.", string.Join("|", rig.Messages.Shown));
    }

    [TestMethod]
    public void AnUninstallThatDidNotFinishLeavesThePerUserEntriesAndReportsTheCode()
    {
        var rig = new Rig();
        rig.Launcher.Result = new ElevatedRun(3, StepOutcomes.FromWin32("runas", 0));

        int code = rig.Verbs.Uninstall();

        Assert.AreEqual(3, code);
        Assert.IsNotNull(rig.Registry.Run);
        Assert.IsEmpty(rig.Deleted);
        Assert.AreEqual("Earshot could not be removed. Code 3.", string.Join("|", rig.Messages.Shown));
    }

    [TestMethod]
    public void APartialUninstallHasItsOwnSentence()
    {
        var rig = new Rig();
        rig.Launcher.Result = new ElevatedRun(2, StepOutcomes.FromWin32("runas", 0));

        rig.Verbs.Uninstall();

        StringAssert.Contains(rig.Messages.Shown.Single(), "not everything");
    }

    [TestMethod]
    public void AValueOrShortcutThatIsNotEarshotsIsLeftAlone()
    {
        var rig = new Rig();
        rig.Registry.Run = "\"D:\\Other\\Earshot.exe\" --startup";
        rig.Links.Read = new ShortcutRead(ShortcutReadStatus.Ok, new ExistingShortcut(@"D:\Other\Earshot.exe", null), null);

        IReadOnlyList<StepOutcome> steps = rig.Cleanup.Run(Exe);

        Assert.IsNotNull(rig.Registry.Run);
        Assert.IsEmpty(rig.Deleted);
        Assert.IsTrue(steps.All(s => !s.Ok && s.Detail!.Contains("left alone", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void NothingToRemoveIsNotAFailure()
    {
        var rig = new Rig();
        rig.Registry.Run = null;
        rig.Links.Read = new ShortcutRead(ShortcutReadStatus.NotFound, null, null);

        Assert.IsTrue(rig.Cleanup.Run(Exe).All(s => s.Ok));
    }

    [TestMethod]
    public void ARegistryFailureKeepsItsRawCodeInTheStep()
    {
        var rig = new Rig();
        rig.Registry.Fail = true;

        StepOutcome step = rig.Cleanup.Run(Exe).Single(s => s.Step == "remove-run-value");

        Assert.IsFalse(step.Ok);
        Assert.AreEqual(new UnauthorizedAccessException().HResult, step.Code);
    }

    [TestMethod]
    public void RepairRunsTheInstalledProgramsRepairWithTheCheckedValues()
    {
        var rig = new Rig();
        var device = new DeviceIdentity { Address = "0A1B2C3D4E5F", ContainerId = new Guid("11111111-2222-4333-8444-555555555501") };

        int code = rig.Verbs.Repair(Sid, device);

        Assert.AreEqual(0, code);
        Assert.AreEqual((Exe, "repair " + Sid + " 0A1B2C3D4E5F 11111111-2222-4333-8444-555555555501"), rig.Launcher.Runs.Single());
        Assert.IsNotNull(rig.Registry.Run, "Repair never touches the per-user entries.");
    }

    [TestMethod]
    public void RepairWithNoDeviceOrABadSidRaisesNoPrompt()
    {
        var rig = new Rig();

        Assert.AreEqual(ExitCodes.Config, rig.Verbs.Repair(Sid, null));
        Assert.AreEqual(ExitCodes.Config, rig.Verbs.Repair("S-1-5-18", new DeviceIdentity { Address = "0A1B2C3D4E5F", ContainerId = Guid.NewGuid() }));

        Assert.IsEmpty(rig.Launcher.Runs);
        Assert.HasCount(2, rig.Messages.Shown);
    }

    [TestMethod]
    public void TheEntryNamesTheVerbsTheseImplement()
    {
        string uninstall = InstalledAppsEntry.UninstallVerb;
        string repair = InstalledAppsEntry.RepairVerb;
        Assert.AreEqual("uninstall-ui", uninstall);
        Assert.AreEqual("repair-ui", repair);
        Assert.IsTrue(Program.PrivilegedModes.All(m => m != uninstall && m != repair), "They run unelevated.");
    }

    [TestMethod]
    public void BothVerbsRefuseInSafeModeBeforeAnyPrompt()
    {
        using var safe = new EnvironmentVariableScope("EARSHOT_SAFE_MODE", "1");

        int uninstall = Program.Dispatch(["uninstall-ui"], global::Earshot.Infra.Paths.Current, new CapturingLog());
        int repair = Program.Dispatch(["repair-ui"], global::Earshot.Infra.Paths.Current, new CapturingLog());

        Assert.AreEqual(ExitCodes.Refused, uninstall);
        Assert.AreEqual(ExitCodes.Refused, repair);
    }
}
