using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Repair;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Repair;

// The fix-apply mode as the elevated process runs it: arguments, token and environment are checked before anything is read.
[TestClass]
public sealed class FixApplyModeTests
{
    private static readonly string Hash = new('A', 64);

    private static string PlanPath => Path.Combine(Path.GetTempPath(), "earshot-tests", "x", FixPlanFiles.FileName);

    private static (GateExitCode Exit, int Runs, CapturingLog Log) Call(IReadOnlyList<string> args, IProcessToken token, IEnumerable<string>? environment = null)
    {
        int runs = 0;
        var log = new CapturingLog();
        GateExitCode exit = Program.RunFixApply(args, token, log, _ =>
        {
            runs++;
            return new FixApplyResult(GateExitCode.Success, [], []);
        }, environment ?? []);
        return (exit, runs, log);
    }

    [TestMethod]
    public void AnElevatedAdministratorWithAGoodCommandLineRuns()
    {
        (GateExitCode exit, int runs, _) = Call(["fix-apply", PlanPath, Hash], FakeToken.ElevatedUser);

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.AreEqual(1, runs);
    }

    [TestMethod]
    public void AUserWhoIsNotElevatedIsRefusedBeforeAnythingRuns()
    {
        (GateExitCode exit, int runs, _) = Call(["fix-apply", PlanPath, Hash], FakeToken.PlainUser);

        Assert.AreEqual(GateExitCode.NotElevated, exit);
        Assert.AreEqual(0, runs);
    }

    [TestMethod]
    public void SystemIsRefused()
    {
        (GateExitCode exit, int runs, _) = Call(["fix-apply", PlanPath, Hash], FakeToken.System);

        Assert.AreEqual(GateExitCode.RunningAsSystem, exit);
        Assert.AreEqual(0, runs);
    }

    [TestMethod]
    public void ARuntimeLoadingVariableIsRefusedAndNamed()
    {
        (GateExitCode exit, int runs, CapturingLog log) = Call(["fix-apply", PlanPath, Hash], FakeToken.ElevatedUser, ["DOTNET_STARTUP_HOOKS"]);

        Assert.AreEqual(GateExitCode.UnsafeEnvironment, exit);
        Assert.AreEqual(0, runs);
        Assert.IsTrue(log.Has(LogLevel.Warn, "DOTNET_STARTUP_HOOKS"));
    }

    [TestMethod]
    public void TheWrongNumberOfArgumentsIsRejected()
    {
        foreach (string[] args in new[]
                 {
                     new[] { "fix-apply" },
                     ["fix-apply", PlanPath],
                     ["fix-apply", PlanPath, Hash, "extra"],
                     ["fix-apply", PlanPath, Hash, "--yes"],
                 })
        {
            (GateExitCode exit, int runs, _) = Call(args, FakeToken.ElevatedUser);
            Assert.AreEqual(GateExitCode.Rejected, exit, string.Join(" ", args));
            Assert.AreEqual(0, runs);
        }
    }

    [TestMethod]
    public void ABadHashOrPathIsRejected()
    {
        foreach (string[] args in new[]
                 {
                     new[] { "fix-apply", PlanPath, "xyz" },
                     ["fix-apply", PlanPath, new string('G', 64)],
                     ["fix-apply", @"relative\fix-plan.json", Hash],
                     ["fix-apply", @"\\server\share\fix-plan.json", Hash],
                     ["fix-apply", @"C:\a\..\b\fix-plan.json", Hash],
                     ["fix-apply", @"C:\a\other.json", Hash],
                     ["fix-apply", "C:\\a\\fix-plan.json\u0001", Hash],
                 })
        {
            (GateExitCode exit, int runs, _) = Call(args, FakeToken.ElevatedUser);
            Assert.AreEqual(GateExitCode.Rejected, exit, string.Join(" ", args));
            Assert.AreEqual(0, runs);
        }
    }

    [TestMethod]
    public void TheModeIsRefusedInSafeModeAndWithARedirectedRootByTheHookItself()
    {
        // The dispatcher's own list is Program.cs's; the hook refuses on its own as well.
        var safe = Earshot.Infra.Paths.FromEnvironment(name => name == Earshot.Infra.Paths.SafeModeVariable ? "1" : null);
        Assert.IsNotNull(Program.FixApplyPathsRefusal(safe));
        var redirected = Earshot.Infra.Paths.FromEnvironment(name => name == Earshot.Infra.Paths.DataRootVariable ? Path.GetTempPath() : null);
        Assert.IsNotNull(Program.FixApplyPathsRefusal(redirected));
    }
}
