using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase4;

// The command lines of the elevated modes. Nothing here runs a real action: the action factories are
// test doubles that fail the test if a refused command line reaches them.
[TestClass]
public sealed class GateCommandLineTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private const string Container = "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13";

    [TestMethod]
    [DataRow("block")]
    [DataRow("allow")]
    [DataRow("status")]
    [DataRow("setboot-on")]
    [DataRow("setboot-off")]
    public void AcceptsEachVerbWithANonce(string verb)
    {
        Assert.IsTrue(Program.TryParseGateArgs(["gate", verb, Nonce], out GateRequest? request, out string? problem), problem);
        Assert.AreEqual(new GateRequest(verb, Nonce, null), request);
        Assert.AreEqual(GateMode.Gate, request.Mode);
    }

    [TestMethod]
    [DataRow("protect-on")]
    [DataRow("protect-off")]
    public void TheProtectVerbsParseOnlyInGateProtectMode(string verb)
    {
        Assert.IsTrue(Program.TryParseGateArgs(["gate-protect", verb, Nonce], out GateRequest? request, out string? problem), problem);
        Assert.AreEqual(new GateRequest(verb, Nonce, null, GateMode.Protect), request);

        Assert.IsFalse(Program.TryParseGateArgs(["gate", verb, Nonce], out GateRequest? refused, out problem));
        Assert.IsNull(refused);
        Assert.AreEqual("protect verbs run only through the Protect task", problem);
    }

    [TestMethod]
    [DataRow("gate-protect")]
    [DataRow("gate-protect", "protect-on")]
    [DataRow("gate-protect", "protect-on", Nonce, "$(Arg2)")]
    [DataRow("gate-protect", "protect-on", Nonce, "")]
    [DataRow("gate-protect", "block", Nonce)]
    [DataRow("gate-protect", "allow", Nonce)]
    [DataRow("gate-protect", "status", Nonce)]
    [DataRow("gate-protect", "setboot-off", Nonce)]
    [DataRow("gate-protect", "set-device", Nonce, "0A1B2C3D4E8C")]
    [DataRow("gate-protect", "boot")]
    [DataRow("gate-protect", "Protect-On", Nonce)]
    [DataRow("gate-protect", "protect-on", "0123456789ABCDEF0123456789ABCDEF")]
    [DataRow("gate-protect", "$(Arg0)", "$(Arg1)")]
    [DataRow("Gate-Protect", "protect-on", Nonce)]
    [DataRow("gate-protect ", "protect-on", Nonce)]
    public void GateProtectRejectsEverythingButAProtectVerbAndANonce(params string[] args)
    {
        Assert.IsFalse(Program.TryParseGateArgs(args, out GateRequest? request, out string? problem));
        Assert.IsNull(request);
        Assert.IsFalse(string.IsNullOrEmpty(problem));
    }

    [TestMethod]
    [DataRow("$(Arg2)")]
    [DataRow("")]
    public void AnUnsuppliedThirdTaskArgumentIsIgnored(string placeholder)
    {
        Assert.IsTrue(Program.TryParseGateArgs(["gate", "block", Nonce, placeholder], out GateRequest? request, out _));
        Assert.IsNull(request.Address);
    }

    [TestMethod]
    public void SetDeviceCarriesTheAddress()
    {
        Assert.IsTrue(Program.TryParseGateArgs(["gate", "set-device", Nonce, "0A1B2C3D4E8C"], out GateRequest? request, out _));
        Assert.AreEqual("0A1B2C3D4E8C", request.Address);
    }

    [TestMethod]
    public void BootTakesNothingAndGetsItsOwnNonce()
    {
        Assert.IsTrue(Program.TryParseGateArgs(["gate", "boot"], out GateRequest? first, out _));
        Assert.IsTrue(Program.TryParseGateArgs(["gate", "boot"], out GateRequest? second, out _));
        Assert.AreEqual(GateVerbs.Boot, first.Verb);
        Assert.IsTrue(BoundaryValidation.IsNonce(first.Nonce));
        Assert.AreNotEqual(first.Nonce, second.Nonce);
        Assert.IsNull(first.Address);
    }

    [TestMethod]
    [DataRow("gate")]
    [DataRow("gate", "block")]
    [DataRow("gate", "block", Nonce, "0A1B2C3D4E8C")]
    [DataRow("gate", "block", Nonce, "", "")]
    [DataRow("gate", "Block", Nonce)]
    [DataRow("gate", "BLOCK", Nonce)]
    [DataRow("gate", " block", Nonce)]
    [DataRow("gate", "install", Nonce)]
    [DataRow("gate", "uninstall", Nonce)]
    [DataRow("gate", "probe", Nonce)]
    [DataRow("gate", "diag", Nonce)]
    [DataRow("gate", "", Nonce)]
    [DataRow("gate", "$(Arg0)", "$(Arg1)", "$(Arg2)")]
    [DataRow("gate", "block", "$(Arg1)", "$(Arg2)")]
    [DataRow("gate", "block", "0123456789ABCDEF0123456789ABCDEF")]
    [DataRow("gate", "block", "0123456789abcdef0123456789abcde")]
    [DataRow("gate", "block", "0123456789abcdef0123456789abcdef0")]
    [DataRow("gate", "block", "..\\..\\windows\\system32\\x.json")]
    [DataRow("gate", "block", "0123456789abcdef0123456789abcdeg")]
    [DataRow("gate", "boot", Nonce)]
    [DataRow("gate", "boot", "$(Arg1)", "$(Arg2)")]
    [DataRow("gate", "boot", "")]
    [DataRow("gate", "set-device", Nonce)]
    [DataRow("gate", "set-device", Nonce, "$(Arg2)")]
    [DataRow("gate", "set-device", Nonce, "0a1b2c3d4e8c")]
    [DataRow("gate", "set-device", Nonce, "0A1B2C3D4E8")]
    [DataRow("gate", "set-device", Nonce, "0A1B2C3D4E8C\"")]
    [DataRow("gate", "set-device", Nonce, "0A1B2C3D4E8C & calc")]
    [DataRow("gate", "set-device", "bad", "0A1B2C3D4E8C")]
    [DataRow("gate", "set-device", Nonce, "0A1B2C3D4E8C", "extra")]
    [DataRow("install", "block", Nonce)]
    [DataRow("Gate", "block", Nonce)]
    public void RejectsEverythingElse(params string[] args)
    {
        Assert.IsFalse(Program.TryParseGateArgs(args, out GateRequest? request, out string? problem));
        Assert.IsNull(request);
        Assert.IsFalse(string.IsNullOrEmpty(problem));
    }

    [TestMethod]
    public void FuzzedVerbsNeverParse()
    {
        var random = new Random(20260915);
        const string alphabet = "abcdefghijklmnopqrstuvwxyz-_$()\"' ;&|0123456789ABCDEF";
        for (int i = 0; i < 5000; i++)
        {
            char[] chars = new char[random.Next(0, 14)];
            for (int c = 0; c < chars.Length; c++)
            {
                chars[c] = alphabet[random.Next(alphabet.Length)];
            }

            string verb = new(chars);
            bool parsed = Program.TryParseGateArgs(["gate", verb, Nonce], out _, out _);
            bool expected = GateVerbs.All.Contains(verb) && verb is not (GateVerbs.Boot or GateVerbs.SetDevice or GateVerbs.ProtectOn or GateVerbs.ProtectOff);
            Assert.AreEqual(expected, parsed, verb);

            bool parsedProtect = Program.TryParseGateArgs(["gate-protect", verb, Nonce], out _, out _);
            Assert.AreEqual(verb is GateVerbs.ProtectOn or GateVerbs.ProtectOff, parsedProtect, "gate-protect " + verb);
        }
    }

    [TestMethod]
    public void ARejectedGateCommandLineNeverBuildsTheActions()
    {
        var log = new CapturingLog();

        GateExitCode exit = Program.RunGate(["gate", "install", Nonce], FakeToken.System, log, NeverBuilt);

        Assert.AreEqual(GateExitCode.Rejected, exit);
        Assert.IsTrue(log.Has(LogLevel.Warn, "gate: rejected"));
    }

    [TestMethod]
    public void AProtectVerbSentThroughTheGateTaskNeverBuildsTheActions()
    {
        var log = new CapturingLog();

        GateExitCode exit = Program.RunGate(["gate", "protect-on", Nonce], FakeToken.System, log, NeverBuilt);

        Assert.AreEqual(GateExitCode.Rejected, exit);
        Assert.IsTrue(log.Has(LogLevel.Warn, "gate: rejected (protect verbs run only through the Protect task)"));
    }

    [TestMethod]
    public void ANodeVerbSentThroughTheProtectTaskNeverBuildsTheActions()
    {
        var log = new CapturingLog();

        GateExitCode exit = Program.RunGate(["gate-protect", "block", Nonce], FakeToken.System, log, NeverBuilt);

        Assert.AreEqual(GateExitCode.Rejected, exit);
        Assert.IsTrue(log.Has(LogLevel.Warn, "gate-protect: rejected"));
    }

    [TestMethod]
    public void TheGateRefusesAnUnelevatedToken()
    {
        var log = new CapturingLog();

        GateExitCode exit = Program.RunGate(["gate", "block", Nonce], FakeToken.PlainUser, log, NeverBuilt);

        Assert.AreEqual(GateExitCode.NotElevated, exit);
    }

    [TestMethod]
    public void AnAcceptedCommandLineRunsTheRequestAsTheTaskSendsIt()
    {
        using var temp = new TempFolder();
        string machine = Path.Combine(temp.Path, "ProgramData", "Earshot");
        Directory.CreateDirectory(machine);
        var store = new GateStore(machine);
        Assert.IsTrue(store.WriteDevice(RecordedNodes.AirPods()).Ok);
        FakeNodeApi nodes = RecordedNodes.Table();
        var log = new CapturingLog();

        // What "gate $(Arg0) $(Arg1) $(Arg2)" becomes when RunEx passes two parameters.
        GateExitCode exit = Program.RunGate(["gate", "block", Nonce, "$(Arg2)"], FakeToken.System, log,
            () => new GateActions(nodes, store, new FakeFolderSecurity(), log, new ManualTime()));

        Assert.AreEqual(GateExitCode.Success, exit);
        Assert.HasCount(9, nodes.Calls);
        Assert.AreEqual("success", store.ReadStatus(Nonce).Value!.Result);
    }

    // [gate, boot] is also what RunEx(["boot"]) on \Earshot\Gate may become when Task Scheduler substitutes
    // the unsupplied $(Arg1) and $(Arg2) as empty, so the gate cannot tell which task sent it. Pinned as
    // accepted: boot does nothing while Block at boot is off, and otherwise exactly what block does.
    [TestMethod]
    public void BootFromEitherTaskDoesNoMoreThanBlock()
    {
        using var temp = new TempFolder();
        string machine = Path.Combine(temp.Path, "ProgramData", "Earshot");
        Directory.CreateDirectory(machine);
        var store = new GateStore(machine);
        Assert.IsTrue(store.WriteDevice(RecordedNodes.AirPods()).Ok);
        Assert.IsTrue(store.WriteConfig(new GateConfig { BlockAtBoot = false }).Ok);
        FakeNodeApi bootNodes = RecordedNodes.Table();
        FakeNodeApi blockNodes = RecordedNodes.Table();
        var log = new CapturingLog();

        Assert.IsTrue(Program.TryParseGateArgs(["gate", "boot"], out GateRequest? parsed, out _));
        Assert.AreEqual(GateVerbs.Boot, parsed.Verb);
        Assert.AreEqual(GateExitCode.Success, Program.RunGate(["gate", "boot"], FakeToken.System, log,
            () => new GateActions(bootNodes, store, new FakeFolderSecurity(), log, new ManualTime())));
        Assert.IsEmpty(bootNodes.Calls, "With Block at boot off, boot changes nothing.");

        Assert.IsTrue(store.WriteConfig(new GateConfig { BlockAtBoot = true }).Ok);
        Assert.AreEqual(GateExitCode.Success, Program.RunGate(["gate", "boot"], FakeToken.System, log,
            () => new GateActions(bootNodes, store, new FakeFolderSecurity(), log, new ManualTime())));
        Assert.AreEqual(GateExitCode.Success, Program.RunGate(["gate", "block", Nonce], FakeToken.System, log,
            () => new GateActions(blockNodes, store, new FakeFolderSecurity(), log, new ManualTime())));

        CollectionAssert.AreEqual(blockNodes.Calls, bootNodes.Calls, "Boot makes the same calls as block.");
        Assert.HasCount(9, bootNodes.Calls);
    }

    [TestMethod]
    public void TheLoggedCommandLineIsBoundedAndPrintable()
    {
        string described = Program.DescribeArgs(["gate", "block\r\n\u0000" + new string('x', 500), "a", "b", "c", "d", "e", "f", "g", "h"]);

        Assert.IsLessThan(700, described.Length);
        Assert.IsFalse(described.Any(char.IsControl));
        StringAssert.StartsWith(described, "10 args:");
    }

    private static GateActions NeverBuilt() => throw new AssertFailedException("A refused command line must not reach the gate actions.");

    private static InstallResult NeverRun(InstallRequest request) => throw new AssertFailedException("A refused install must not run.");

    private static InstallResult NeverRunUninstall() => throw new AssertFailedException("A refused uninstall must not run.");

    [TestMethod]
    public void InstallParsesTheIdentityAndThePrincipal()
    {
        Assert.IsTrue(Program.TryParseInstallArgs(["install", TestUsers.Sid, "0A1B2C3D4E8C", Container], out InstallRequest? request, out string? problem), problem);
        Assert.AreEqual(new InstallRequest(TestUsers.Sid, "0A1B2C3D4E8C", RecordedNodes.AirPodsContainer, TaskPrincipalMode.System), request);

        Assert.IsTrue(Program.TryParseInstallArgs(["install", TestUsers.Sid, "0A1B2C3D4E8C", Container, "--principal", "user"], out request, out _));
        Assert.AreEqual(TaskPrincipalMode.InteractiveUser, request.Principal);
    }

    [TestMethod]
    [DataRow("install")]
    [DataRow("install", TestUsers.Sid, "0A1B2C3D4E8C")]
    [DataRow("install", TestUsers.Sid, "0A1B2C3D4E8C", Container, "--principal")]
    [DataRow("install", TestUsers.Sid, "0A1B2C3D4E8C", Container, "--principal", "system")]
    [DataRow("install", TestUsers.Sid, "0A1B2C3D4E8C", Container, "--principal", "User")]
    [DataRow("install", TestUsers.Sid, "0A1B2C3D4E8C", Container, "--elevate", "user")]
    [DataRow("install", TestUsers.Sid, "0A1B2C3D4E8C", Container, "--principal", "user", "x")]
    [DataRow("install", "S-1-5-18", "0A1B2C3D4E8C", Container)]
    [DataRow("install", "S-1-1-0", "0A1B2C3D4E8C", Container)]
    [DataRow("install", TestUsers.Sid, "0a1b2c3d4e8c", Container)]
    [DataRow("install", TestUsers.Sid, "0A1B2C3D4E8C", "{5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13}")]
    [DataRow("install", TestUsers.Sid, "0A1B2C3D4E8C", "5c3a9e214b7d5f189a6c2d8e0b4f7a13")]
    [DataRow("install", TestUsers.Sid, "0A1B2C3D4E8C", "00000000-0000-0000-0000-000000000000")]
    [DataRow("install", TestUsers.Sid, "0A1B2C3D4E8C", "00000000-0000-0000-ffff-ffffffffffff")]
    [DataRow("uninstall", TestUsers.Sid, "0A1B2C3D4E8C", Container)]
    public void InstallRejectsAnythingElse(params string[] args)
    {
        Assert.IsFalse(Program.TryParseInstallArgs(args, out InstallRequest? request, out string? problem));
        Assert.IsNull(request);
        Assert.IsFalse(string.IsNullOrEmpty(problem));
    }

    [TestMethod]
    public void InstallAndUninstallRefuseSystemAndUnelevatedTokens()
    {
        string[] install = ["install", TestUsers.Sid, "0A1B2C3D4E8C", Container];
        var log = new CapturingLog();

        Assert.AreEqual(GateExitCode.RunningAsSystem, Program.RunInstall(install, FakeToken.System, log, NeverRun));
        Assert.AreEqual(GateExitCode.NotElevated, Program.RunInstall(install, FakeToken.PlainUser, log, NeverRun));
        Assert.AreEqual(GateExitCode.Rejected, Program.RunInstall(["install", TestUsers.Sid], FakeToken.ElevatedUser, log, NeverRun));
        Assert.AreEqual(GateExitCode.RunningAsSystem, Program.RunUninstall(["uninstall"], FakeToken.System, log, NeverRunUninstall));
        Assert.AreEqual(GateExitCode.NotElevated, Program.RunUninstall(["uninstall"], FakeToken.PlainUser, log, NeverRunUninstall));
        Assert.AreEqual(GateExitCode.Rejected, Program.RunUninstall(["uninstall", "now"], FakeToken.ElevatedUser, log, NeverRunUninstall));
    }

    [TestMethod]
    public void AnAcceptedInstallRunsWithTheParsedRequestAndReturnsItsOutcome()
    {
        InstallRequest? seen = null;
        var log = new CapturingLog();

        GateExitCode exit = Program.RunInstall(
            ["install", TestUsers.Sid, "0A1B2C3D4E8C", Container, "--principal", "user"],
            FakeToken.ElevatedUser,
            log,
            request =>
            {
                seen = request;
                return new InstallResult(GateExitCode.Partial, [StepOutcomes.FromHResult("x", unchecked((int)0x80070005))]);
            });

        Assert.AreEqual(GateExitCode.Partial, exit);
        Assert.AreEqual(TaskPrincipalMode.InteractiveUser, seen!.Principal);
        Assert.IsTrue(log.Has(LogLevel.Warn, "install: x E_ACCESSDENIED"));
        Assert.IsTrue(log.Has(LogLevel.Info, "install: partial (2)."));
    }
}
