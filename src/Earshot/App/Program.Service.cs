using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Service;

namespace Earshot;

// The hand-back service run mode: "Earshot.exe service", the image path the install registers for the service and the
// only command line that reaches it. Program.Dispatch has already refused it in safe mode and while EARSHOT_DATA_ROOT
// is set. Run from a console or by a person it is refused by the service control manager itself, before it opens a device
// or writes a file (GateExitCode.NotAService).
//
// What this code controls, and what it does not. Program.Main calls SetDefaultDllDirectories first, so a library this
// program loads by name afterwards is looked for in the system folder and the program's own folder, never in the working
// directory or on PATH. That is all it controls. What the host and the runtime load before Main runs is resolved by them,
// and may use the machine PATH; if a folder on that PATH can be written by a standard user, the install folder's access
// list is not what protects that load. The service checks its own install folder's access list when it starts, which
// covers the files it is started from and not that earlier step, and no check of what is loaded at that step has been made.
//
// The log is SYSTEM's own, as for every elevated run, and any other account gets the debugger output only, so the
// service never writes under a standard user's profile.
internal static partial class Program
{
    static partial void TryRunService(RunContext ctx)
    {
        Paths paths = Paths.Current;
        ILog log = ModeLog(paths, privileged: true, RunningAsLocalSystem);

        // The image path carries the literal "service" and nothing else, so anything more is not the control manager.
        if (ctx.Args.Length != 1)
        {
            log.Warn(HandBackServiceText.Refused("it takes no arguments", GateExitCode.Rejected));
            ctx.ExitCode = (int)GateExitCode.Rejected;
            return;
        }

        var host = new WindowsServiceHost(ex => log.Error(HandBackServiceText.Prefix + "a callback stopped with " + ex.GetType().Name + ".", ex));
        GateExitCode exit = HandBackServiceProgram.Run(
            host, () => WindowsProcessToken.Current(), new NtfsFolderSecurity(), paths.InstallFolder, paths.MachineFolder, log, TimeProvider.System,
            (nonce, deadline) => GateActions.ForPreshutdown(paths.MachineFolder, log).RunPreshutdown(nonce, deadline));
        ctx.ExitCode = (int)exit;
    }
}
