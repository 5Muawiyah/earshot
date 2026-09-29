using System.Diagnostics;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Tests.Phase4;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Update;

// The hand-over's arguments, and the real launcher run against cmd.exe: the way the live-test harness proves its
// process launcher, because a fake at a boundary proves everything except the boundary. cmd.exe is started with no
// verb, so nothing here elevates.
[TestClass]
public sealed class UpdateHandoverTests
{
    private static readonly Guid Container = new("5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13");

    private static readonly string CmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    // ----- the identity and the arguments -----

    [TestMethod]
    public void TheIdentityIsThePinnedDeviceAndTheSignedInUser()
    {
        var settings = new EarshotSettings { PinnedAddress = "0A1B2C3D4E8C", PinnedContainerId = Container };

        HandoverIdentity? identity = UpdateHandover.TryIdentity(TestUsers.Sid, settings);

        Assert.AreEqual(new HandoverIdentity(TestUsers.Sid, "0A1B2C3D4E8C", Container), identity);
    }

    [TestMethod]
    public void NoIdentityWithoutAPinnedDeviceOrAUserSid()
    {
        Assert.IsNull(UpdateHandover.TryIdentity(TestUsers.Sid, new EarshotSettings()), "Nothing pinned.");
        Assert.IsNull(UpdateHandover.TryIdentity(TestUsers.Sid, new EarshotSettings { PinnedAddress = "0A1B2C3D4E8C" }), "No container.");
        Assert.IsNull(UpdateHandover.TryIdentity(TestUsers.Sid, new EarshotSettings { PinnedAddress = "0a1b2c3d4e8c", PinnedContainerId = Container }), "Lower-case address.");
        Assert.IsNull(UpdateHandover.TryIdentity(null, new EarshotSettings { PinnedAddress = "0A1B2C3D4E8C", PinnedContainerId = Container }), "No SID.");
        Assert.IsNull(UpdateHandover.TryIdentity("S-1-5-18", new EarshotSettings { PinnedAddress = "0A1B2C3D4E8C", PinnedContainerId = Container }), "SYSTEM is not a user.");
    }

    [TestMethod]
    public void TheHandoverSendsExactlyWhatSetupSendsAndInstallAcceptsIt()
    {
        // The real BlockController, built as its own tests build it, with its launcher faked: what it would start
        // for "Set up Earshot..." is compared with what the update hands over.
        using var temp = new TempFolder();
        string machine = Path.Combine(temp.Path, "ProgramData", "Earshot");
        Directory.CreateDirectory(machine);
        var store = new GateStore(machine);
        var settings = new FakeSettings();
        settings.Current.PinnedAddress = RecordedNodes.AirPodsAddress;
        settings.Current.PinnedContainerId = RecordedNodes.AirPodsContainer;
        var time = new ManualTime();
        var tasks = new FakeScheduledTasks();
        var gate = new TaskSchedulerGate(tasks, store, Path.Combine(temp.Path, "Program Files", "Earshot"), TestUsers.Sid, Lookups.None, time,
            (delay, ct) => !ct.IsCancellationRequested);
        var launcher = new FakeLauncher();
        launcher.Result = _ => new ElevatedRun((int)GateExitCode.Failed, StepOutcomes.FromWin32("runas", 0));
        using var worker = new SystemWorker(new CapturingLog());
        using var controller = new BlockController(new CapturingLog(), settings, store, RecordedNodes.Table(), gate, launcher, TestUsers.Sid, @"C:\Program Files\Earshot\Earshot.exe", worker);
        controller.RunSetupAsync().GetAwaiter().GetResult();
        string setup = launcher.Launches.Single().Arguments;

        HandoverIdentity identity = UpdateHandover.TryIdentity(TestUsers.Sid, settings.Current)!;
        IReadOnlyList<string> arguments = UpdateHandover.InstallArguments(identity);

        Assert.AreEqual(setup, UpdateHandover.JoinArguments(arguments), "The update runs the same command line as Set up Earshot.");
        Assert.IsTrue(Program.TryParseInstallArgs(arguments, out InstallRequest? request, out string? problem), "Install accepts it: " + problem);
        Assert.AreEqual(TestUsers.Sid, request!.UserSid);
        Assert.AreEqual(RecordedNodes.AirPodsAddress, request.Address);
        Assert.AreEqual(RecordedNodes.AirPodsContainer, request.ContainerId);
        Assert.AreEqual(TaskPrincipalMode.System, request.Principal, "The default SYSTEM tasks, never --principal user.");
    }

    // ----- the command line -----

    [TestMethod]
    [DataRow(new[] { "install", "S-1-5-21-1-2-3-1001", "0A1B2C3D4E8C", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13" }, "install S-1-5-21-1-2-3-1001 0A1B2C3D4E8C 5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13")]
    [DataRow(new[] { "a b" }, "\"a b\"")]
    [DataRow(new[] { "" }, "\"\"")]
    [DataRow(new[] { "say \"hi\"" }, "\"say \\\"hi\\\"\"")]
    [DataRow(new[] { "C:\\dir with space\\" }, "\"C:\\dir with space\\\\\"")]
    [DataRow(new[] { "C:\\plain\\" }, "C:\\plain\\")]
    [DataRow(new[] { "x", "y z" }, "x \"y z\"")]
    public void ArgumentsAreQuotedTheWayCreateProcessReadsThemBack(string[] arguments, string expected)
    {
        Assert.AreEqual(expected, UpdateHandover.JoinArguments(arguments));
    }

    // ----- the real launcher, against cmd.exe -----

    private static uint WaitForExit(LaunchResult result)
    {
        Assert.AreEqual(LaunchOutcome.Started, result.Outcome, result.Detail);
        using Process process = result.Process!;
        Assert.IsTrue(process.WaitForExit(TimeSpan.FromSeconds(30)), "cmd.exe did not finish.");
        return unchecked((uint)process.ExitCode);
    }

    [TestMethod]
    public void TheRealLauncherPassesArgumentsAndReturnsTheProcessSoItsExitCodeCanBeRead()
    {
        var launcher = new ElevatedUpdateLauncher(verb: null, hidden: true);

        Assert.AreEqual(7u, WaitForExit(launcher.Launch(CmdExe, ["/c", "exit", "7"], Environment.SystemDirectory)));
        Assert.AreEqual(0u, WaitForExit(launcher.Launch(CmdExe, ["/c", "exit", "0"], Environment.SystemDirectory)));
    }

    [TestMethod]
    public void TheRealLauncherRunsAProgramWhoseFolderHasSpacesInItsName()
    {
        using var temp = new TempFolder();
        string folder = Path.Combine(temp.Path, "a folder with spaces");
        Directory.CreateDirectory(folder);
        string copy = Path.Combine(folder, "my cmd.exe");
        File.Copy(CmdExe, copy);
        var launcher = new ElevatedUpdateLauncher(verb: null, hidden: true);

        Assert.AreEqual(9u, WaitForExit(launcher.Launch(copy, ["/c", "exit", "9"], folder)));
    }

    [TestMethod]
    public void TheRealLauncherStartsTheProgramInTheFolderItIsGiven()
    {
        using var temp = new TempFolder();
        string folder = Path.Combine(temp.Path, "work");
        Directory.CreateDirectory(folder);
        string marker = Path.Combine(temp.Path, "cwd.txt");
        var launcher = new ElevatedUpdateLauncher(verb: null, hidden: true);

        // cmd writes its current folder to a file whose path has no space, so no quoting is involved.
        Assert.IsFalse(marker.Contains(' ', StringComparison.Ordinal), "The temp path holds a space; this check needs one that does not.");
        WaitForExit(launcher.Launch(CmdExe, ["/c", "cd>" + marker], folder));

        Assert.IsTrue(string.Equals(folder, File.ReadAllText(marker).Trim(), StringComparison.OrdinalIgnoreCase), "cmd reported " + File.ReadAllText(marker));
    }

    [TestMethod]
    public void AProgramThatIsNotThereIsReportedWithItsWin32ErrorNotSwallowed()
    {
        using var temp = new TempFolder();
        var launcher = new ElevatedUpdateLauncher(verb: null, hidden: true);

        LaunchResult result = launcher.Launch(Path.Combine(temp.Path, "missing.exe"), ["install"], temp.Path);

        Assert.AreEqual(LaunchOutcome.Failed, result.Outcome);
        Assert.AreEqual(2u, result.Win32Error, "ERROR_FILE_NOT_FOUND.");
        StringAssert.Contains(result.Detail, "Win32 error 2");
        Assert.IsNull(result.Process);
    }

    [TestMethod]
    public void TheProductionLauncherAsksForTheAdministratorPromptAndNothingElse()
    {
        var launcher = new ElevatedUpdateLauncher();

        ProcessStartInfo info = launcher.CreateStartInfo(@"C:\Users\x\AppData\Local\Earshot\update\u1\app\Earshot.exe",
            ["install", TestUsers.Sid, "0A1B2C3D4E8C", Container.ToString("D")], @"C:\Users\x\AppData\Local\Earshot\update\u1\app");

        Assert.AreEqual("runas", info.Verb);
        Assert.IsTrue(info.UseShellExecute);
        Assert.AreEqual("install " + TestUsers.Sid + " 0A1B2C3D4E8C " + Container.ToString("D"), info.Arguments);
        Assert.AreEqual(@"C:\Users\x\AppData\Local\Earshot\update\u1\app", info.WorkingDirectory);
        Assert.AreEqual(ProcessWindowStyle.Normal, info.WindowStyle);
    }
}
