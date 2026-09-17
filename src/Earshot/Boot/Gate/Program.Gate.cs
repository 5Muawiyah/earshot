using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Earshot.AudioProtection;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;

namespace Earshot;

// The elevated run modes.
//
//   gate <verb> <nonce>                 run by \Earshot\Gate as SYSTEM; every verb except protect-on and protect-off
//   gate set-device <nonce> <address>   the only verb that carries an address
//   gate boot                           run by \Earshot\BootBlock at boot
//   gate-protect <verb> <nonce>         run by \Earshot\Protect; protect-on and protect-off only
//   install <userSid> <address> <containerGuid> [--principal user]
//   uninstall
//
// Program.Dispatch has already refused all of them in safe mode and while EARSHOT_DATA_ROOT is set. The
// command line is attacker-controlled (anyone who may start the gate task chooses $(Arg0) to $(Arg2)), so
// it is matched exactly and never used in a shell, a path or a query. A command line that does not match
// exits with GateExitCode.Rejected and does nothing else.
// A log with no file, for an elevated run that has nowhere safe to write one: every entry goes to the debugger
// output with the reason, so it is not lost silently to anyone watching.
internal sealed class DebugOutputLog(string why) : ILog
{
    public void Write(LogLevel level, string message, Exception? ex = null) =>
        System.Diagnostics.Trace.WriteLine("Earshot (no log file, because " + why + ") " + level + " " + message + (ex is null ? "" : " " + ex));
}

internal static partial class Program
{
    internal const string InstallPrincipalFlag = "--principal";
    internal const string InstallPrincipalUser = "user";

    static partial void TryRunGate(RunContext ctx)
    {
        Paths paths = Paths.Current;
        ILog log = GateLog(paths, CurrentTokenOrNull(), new NtfsFolderSecurity());
        ctx.ExitCode = (int)Guarded(log, "gate", () =>
            RunGate(ctx.Args, WindowsProcessToken.Current(), log, () => GateActions.ForMachine(paths.MachineFolder, log)));
    }

    // The log of a gate run, in a folder no standard user can redirect, since an elevated write that follows a
    // junction or link a user planted can land on any file.
    //   As SYSTEM (the default principal): SYSTEM's own profile, which only administrators can change.
    //   As the interactive user (install --principal user): that user can write their own profile and can start
    //   the task, so the log goes to %ProgramData%\Earshot\logs once the machine folder passes its ACL check (its
    //   inheritable entries protect the subfolder too). When it does not pass, no file is written: the run
    //   refuses anyway and its exit code says why.
    // Running as the user is itself a same-user elevation surface: that user can also set .NET runtime variables
    // in HKCU\Environment, which a task started with the user's environment would load. That is why SYSTEM is the
    // default and the user principal is only the fall-back the owner's live test may choose.
    // https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks
    internal static ILog GateLog(Paths paths, IProcessToken? token, IFolderSecurity folders)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(folders);
        if (token is { IsLocalSystem: true })
        {
            return new FileLog(paths.LogFolder);
        }

        StepOutcome read = folders.ReadSddl(paths.MachineFolder, out string? sddl);
        if (read.Ok && AclCheck.CheckMachineFolder(sddl).Count == 0)
        {
            return new FileLog(Path.Combine(paths.MachineFolder, "logs"));
        }

        return new DebugOutputLog("the machine folder did not pass its check (" + read.Step + " " + read.CodeName + ")");
    }

    // The token for choosing the log, or null when it cannot be read. RunGate reads it again, and a failure there
    // is logged by Guarded.
    private static WindowsProcessToken? CurrentTokenOrNull()
    {
        try
        {
            return WindowsProcessToken.Current();
        }
        catch (System.Security.SecurityException ex)
        {
            System.Diagnostics.Trace.WriteLine("Earshot gate: the process token could not be read: " + ex.Message);
            return null;
        }
    }

    static partial void TryRunInstall(RunContext ctx)
    {
        Paths paths = Paths.Current;

        // Elevated under the user's own profile: append only (see FileLog).
        var log = new FileLog(paths.LogFolder, rolls: false);
        var layout = new InstallLayout(AppContext.BaseDirectory, paths.InstallFolder, paths.MachineFolder);
        ctx.ExitCode = (int)Guarded(log, "install", () => RunInstall(ctx.Args, WindowsProcessToken.Current(), log, request =>
        {
            // Task Scheduler COM runs on an MTA thread, as it does in the tray.
            using var worker = new SystemWorker(log);
            return worker.RunAsync(_ => new InstallActions(layout, new NtfsFolderSecurity(), new CfgMgr32NodeReader(), new ComTaskRegistrar(), AccountSids.Translate, log).Run(request))
                .GetAwaiter().GetResult();
        }));
    }

    static partial void TryRunUninstall(RunContext ctx)
    {
        Paths paths = Paths.Current;

        // Elevated under the user's own profile: append only (see FileLog).
        var log = new FileLog(paths.LogFolder, rolls: false);
        var layout = new InstallLayout(AppContext.BaseDirectory, paths.InstallFolder, paths.MachineFolder);
        ctx.ExitCode = (int)Guarded(log, "uninstall", () => RunUninstall(ctx.Args, WindowsProcessToken.Current(), log, () =>
        {
            using var worker = new SystemWorker(log);
            return worker.RunAsync(_ => new UninstallActions(layout, new NtfsFolderSecurity(), new CfgMgr32NodeApi(), new ComTaskRegistrar(), new MoveFileRebootDelete(), log, new MachineGateMutex(), new BluetoothServiceApi()).Run())
                .GetAwaiter().GetResult();
        }));
    }

    // The actions record their own failures; this is the last resort for anything around them (the worker, the
    // COM connection, a token read), so an elevated mode never ends without a line in the log.
    private static GateExitCode Guarded(ILog log, string mode, Func<GateExitCode> run)
    {
        try
        {
            return run();
        }
        catch (Exception ex)
        {
            log.Error(mode + ": " + GateActions.Describe(ElevatedFailure.Step(mode, ex)), ex);
            return GateExitCode.Failed;
        }
    }

    internal static GateExitCode RunGate(IReadOnlyList<string> args, IProcessToken token, ILog log, Func<GateActions> actions)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(actions);

        if (!TryParseGateArgs(args, out GateRequest? request, out string? problem))
        {
            log.Warn((args.Count > 0 && args[0] == GateModes.ProtectToken ? GateModes.ProtectToken : GateModes.GateToken) +
                     ": rejected (" + problem + "): " + DescribeArgs(args));
            return GateExitCode.Rejected;
        }

        if (!token.IsLocalSystem && !token.IsElevatedAdministrator)
        {
            log.Warn("gate: refused, not running as SYSTEM or an elevated administrator.");
            return GateExitCode.NotElevated;
        }

        return actions().Run(request);
    }

    internal static GateExitCode RunInstall(IReadOnlyList<string> args, IProcessToken token, ILog log, Func<InstallRequest, InstallResult> run)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(run);

        GateExitCode? refusal = ElevatedModeRefusal("install", token, log);
        if (refusal is not null)
        {
            return refusal.Value;
        }

        if (!TryParseInstallArgs(args, out InstallRequest? request, out string? problem))
        {
            log.Warn("install: rejected (" + problem + "): " + DescribeArgs(args));
            return GateExitCode.Rejected;
        }

        log.Info("install for " + request.UserSid + ", device " + request.Address + ", container " +
                 request.ContainerId.ToString("D") + ", " + request.Principal + " principal.");
        InstallResult result = run(request);
        LogSteps(log, "install", result);
        return result.Outcome;
    }

    internal static GateExitCode RunUninstall(IReadOnlyList<string> args, IProcessToken token, ILog log, Func<InstallResult> run)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(run);

        GateExitCode? refusal = ElevatedModeRefusal("uninstall", token, log);
        if (refusal is not null)
        {
            return refusal.Value;
        }

        if (args.Count != 1 || args[0] != "uninstall")
        {
            log.Warn("uninstall: rejected (it takes no arguments): " + DescribeArgs(args));
            return GateExitCode.Rejected;
        }

        InstallResult result = run();
        LogSteps(log, "uninstall", result);
        return result.Outcome;
    }

    // install and uninstall run from a user's UAC prompt: never as SYSTEM, and only with an elevated token.
    private static GateExitCode? ElevatedModeRefusal(string mode, IProcessToken token, ILog log)
    {
        if (token.IsLocalSystem)
        {
            log.Warn(mode + ": refused, it does not run as SYSTEM.");
            return GateExitCode.RunningAsSystem;
        }

        if (!token.IsElevatedAdministrator)
        {
            log.Warn(mode + ": refused, it needs an elevated administrator.");
            return GateExitCode.NotElevated;
        }

        return null;
    }

    // Exactly [gate, verb, nonce], [gate, set-device, nonce, address], [gate, boot] or
    // [gate-protect, protect-on|protect-off, nonce]. Task Scheduler may pass an unsupplied $(Arg2) as the literal
    // placeholder or as an empty string, so a fourth gate argument that normalises to empty is dropped for the
    // verbs without an address. The Protect task's action has no $(Arg2), so gate-protect takes no fourth one.
    //
    // A protect verb is refused in gate mode and every other verb in gate-protect mode: BluetoothSetServiceState
    // installs or removes drivers for an undocumented time, so it runs only under \Earshot\Protect's longer time
    // limit, and a node change never runs under it.
    //
    // [gate, boot] is accepted whoever started the gate. \Earshot\BootBlock passes it, but a caller allowed to
    // start \Earshot\Gate can produce the same command line too: RunEx(["boot"]) with $(Arg1) and $(Arg2)
    // substituted as empty strings, and a value holding spaces may split into several arguments, because how
    // Task Scheduler quotes substituted values is undocumented. So no fixed BootBlock token could be kept out
    // of reach. This is accepted: boot does no more than block (and nothing when BlockAtBoot is off), takes its
    // identity only from device.json, and writes a status file under a fresh random nonce that is size-capped
    // and pruned like every other.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex
    internal static bool TryParseGateArgs(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out GateRequest? request,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        request = null;

        if (args.Count == 0 || args[0] is not (GateModes.GateToken or GateModes.ProtectToken))
        {
            problem = "not a gate command line";
            return false;
        }

        if (args[0] == GateModes.ProtectToken)
        {
            return TryParseGateProtectArgs(args, out request, out problem);
        }

        if (args.Count == 2 && args[1] == GateVerbs.Boot)
        {
            request = new GateRequest(GateVerbs.Boot, Guid.NewGuid().ToString("N"), null);
            problem = null;
            return true;
        }

        if (args.Count is < 3 or > 4)
        {
            problem = "wrong number of arguments";
            return false;
        }

        string verb = args[1];
        string nonce = args[2];
        string address = args.Count == 4 ? BoundaryValidation.Normalise(args[3]) : "";

        if (!GateVerbs.All.Contains(verb) || verb == GateVerbs.Boot)
        {
            problem = "unknown verb";
            return false;
        }

        if (GateModes.IsProtectVerb(verb))
        {
            problem = "protect verbs run only through the Protect task";
            return false;
        }

        if (!BoundaryValidation.IsNonce(nonce))
        {
            problem = "bad nonce";
            return false;
        }

        if (verb == GateVerbs.SetDevice)
        {
            if (!BoundaryValidation.IsAddress12(address))
            {
                problem = "set-device needs a 12 character upper-case hex address";
                return false;
            }

            request = new GateRequest(verb, nonce, address);
            problem = null;
            return true;
        }

        if (address.Length != 0)
        {
            problem = "only set-device takes an address";
            return false;
        }

        request = new GateRequest(verb, nonce, null);
        problem = null;
        return true;
    }

    private static bool TryParseGateProtectArgs(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out GateRequest? request,
        [NotNullWhen(false)] out string? problem)
    {
        request = null;
        if (args.Count != 3)
        {
            problem = "wrong number of arguments";
            return false;
        }

        if (!GateModes.IsProtectVerb(args[1]))
        {
            problem = "gate-protect takes only protect-on or protect-off";
            return false;
        }

        if (!BoundaryValidation.IsNonce(args[2]))
        {
            problem = "bad nonce";
            return false;
        }

        request = new GateRequest(args[1], args[2], null, GateMode.Protect);
        problem = null;
        return true;
    }

    // Exactly [install, userSid, address, containerGuid] or the same followed by [--principal, user].
    internal static bool TryParseInstallArgs(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out InstallRequest? request,
        [NotNullWhen(false)] out string? problem)
    {
        ArgumentNullException.ThrowIfNull(args);
        request = null;

        if (args.Count is not (4 or 6) || args[0] != "install")
        {
            problem = "wrong number of arguments";
            return false;
        }

        if (!Sddl.IsUserSid(args[1]))
        {
            problem = "not a user SID";
            return false;
        }

        if (!BoundaryValidation.IsAddress12(args[2]))
        {
            problem = "not a 12 character upper-case hex address";
            return false;
        }

        if (!Guid.TryParseExact(args[3], "D", out Guid container) || !NodeMatch.IsValidTargetContainer(container))
        {
            problem = "not a device container GUID";
            return false;
        }

        TaskPrincipalMode principal = TaskPrincipalMode.System;
        if (args.Count == 6)
        {
            if (args[4] != InstallPrincipalFlag || args[5] != InstallPrincipalUser)
            {
                problem = "the only extra argument is --principal user";
                return false;
            }

            principal = TaskPrincipalMode.InteractiveUser;
        }

        request = new InstallRequest(args[1], args[2], container, principal);
        problem = null;
        return true;
    }

    // A bounded, printable rendering of an untrusted command line for the log.
    internal static string DescribeArgs(IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        sb.Append(args.Count.ToString(CultureInfo.InvariantCulture)).Append(" args:");
        foreach (string arg in args.Take(8))
        {
            sb.Append(" [");
            foreach (char c in arg.Take(64))
            {
                sb.Append(c is >= ' ' and <= '~' ? c : '?');
            }

            if (arg.Length > 64)
            {
                sb.Append("...");
            }

            sb.Append(']');
        }

        return sb.ToString();
    }

    private static void LogSteps(ILog log, string mode, InstallResult result)
    {
        foreach (StepOutcome step in result.Steps)
        {
            string line = mode + ": " + GateActions.Describe(step);
            if (step.Ok)
            {
                log.Info(line);
            }
            else
            {
                log.Warn(line);
            }
        }

        log.Info(mode + ": " + GateExitCodes.ResultName(result.Outcome) + " (" + (int)result.Outcome + ").");
    }
}
