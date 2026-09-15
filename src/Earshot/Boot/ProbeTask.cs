using System.Globalization;
using System.Security.Principal;
using Earshot.App;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;

namespace Earshot;

// probe task: whether \Earshot and its three tasks exist, their security descriptors, principal and action,
// the same checks the tray runs before RunEx, and whether this user's token may start each task. Read-only:
// it connects, opens the folder and tasks and reads them. Nothing is registered, run, changed or deleted.
internal static partial class Program
{
    internal sealed record ProbeTaskRow(
        string Name,
        int OpenResult,
        TaskReadback? Task,
        TaskXmlCheck.Summary Summary,
        IReadOnlyList<string> Problems,
        uint UserMask);

    internal sealed record TaskProbeReport(
        string InstallFolder,
        string? UserSid,
        int FolderResult,
        string? FolderSddl,
        IReadOnlyList<string> FolderProblems,
        IReadOnlyList<ProbeTaskRow> Tasks)
    {
        public bool SetUp => Tasks.All(t => t.OpenResult == 0 && t.Problems.Count == 0);
    }

    static partial void ProbeTask(ProbeContext ctx)
    {
        Paths paths = Paths.Current;
        string? sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            sid = identity.User?.Value;
        }

        TaskProbeReport report;
        using (var worker = new SystemWorker(ctx.Services.Log))
        {
            report = worker.RunAsync(_ => ReadTaskProbe(new ComScheduledTasks(), ReadTaskFolder, paths.InstallFolder, sid)).GetAwaiter().GetResult();
        }

        WriteTaskProbe(ctx, report);
        ctx.Handled = true;
        ctx.ExitCode = report.FolderResult is 0 or TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND ? ExitCodes.Ok : ExitCodes.OsError;
    }

    internal static int ReadTaskFolder(string path, out string? sddl)
    {
        string? read = null;
        int hr = ComTaskScheduler.WithFolder(path, (_, folder) => folder.GetSecurityDescriptor(ComTaskScheduler.SecurityInformation, out read));
        sddl = read;
        return hr;
    }

    internal delegate int FolderReader(string path, out string? sddl);

    internal static TaskProbeReport ReadTaskProbe(IScheduledTasks tasks, FolderReader readFolder, string installFolder, string? userSid)
    {
        int folderHr = readFolder(TaskPlan.FolderPath, out string? folderSddl);
        IReadOnlyList<string> folderProblems = folderHr != 0 ? [] :
            Sddl.IsUserSid(userSid) ? AclCheck.CheckTaskFolder(folderSddl, userSid!) : ["The current user has no usable SID."];

        var rows = new List<ProbeTaskRow>();
        foreach (string name in TaskPlan.TaskNames)
        {
            int hr = tasks.ReadTask(TaskPlan.TaskPath(name), out TaskReadback? task);
            IReadOnlyList<string> problems = [];
            uint mask = 0;
            if (hr == 0 && task is not null)
            {
                bool mayRun = name != TaskPlan.BootTaskName;
                problems = Sddl.IsUserSid(userSid)
                    ? AclCheck.CheckTask(task.Sddl, userSid!, mayRun)
                        .Concat(TaskXmlCheck.VerifyInstalled(task.Xml, name, installFolder, userSid!, AccountSids.Translate, steps: null)).ToList()
                    : ["The current user has no usable SID."];
                mask = Sddl.IsUserSid(userSid) ? AclCheck.UserAllowedMask(task.Sddl, userSid!) : 0;
            }

            rows.Add(new ProbeTaskRow(name, hr, task, TaskXmlCheck.Describe(task?.Xml), problems, mask));
        }

        return new TaskProbeReport(installFolder, userSid, folderHr, folderSddl, folderProblems, rows);
    }

    // Null when LastTaskResult could not be read; the read failure is in the task's steps.
    private static string? LastResultName(int? lastTaskResult) =>
        lastTaskResult is int last ? GateExitCodes.NameOf(last) ?? NativeCodes.Name(last) : null;

    private static string Presence(int hr) =>
        hr == 0 ? "present" : hr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND ? "absent (" + NativeCodes.Name(hr) + ")" : "unreadable (" + NativeCodes.Name(hr) + ")";

    internal static void WriteTaskProbe(ProbeContext ctx, TaskProbeReport report)
    {
        if (ctx.Json)
        {
            ctx.WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("target", "task");
                w.WriteString("installFolder", report.InstallFolder);
                w.WriteString("userSid", report.UserSid);
                w.WriteBoolean("setUp", report.SetUp);
                w.WriteStartObject("folder");
                w.WriteString("path", TaskPlan.FolderPath);
                w.WriteString("result", NativeCodes.Name(report.FolderResult));
                w.WriteString("sddl", report.FolderSddl);
                w.WriteStartArray("problems");
                foreach (string problem in report.FolderProblems)
                {
                    w.WriteStringValue(problem);
                }

                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteStartArray("tasks");
                foreach (ProbeTaskRow row in report.Tasks)
                {
                    w.WriteStartObject();
                    w.WriteString("path", TaskPlan.TaskPath(row.Name));
                    w.WriteString("result", NativeCodes.Name(row.OpenResult));
                    w.WriteBoolean("present", row.OpenResult == 0);
                    w.WriteString("sddl", row.Task?.Sddl);
                    w.WriteString("userId", row.Summary.UserId);
                    w.WriteString("logonType", row.Summary.LogonType);
                    w.WriteString("runLevel", row.Summary.RunLevel);
                    w.WriteString("command", row.Summary.Command);
                    w.WriteString("arguments", row.Summary.Arguments);
                    if (row.Task is not null)
                    {
                        w.WriteNumber("state", row.Task.State);
                        w.WriteString("lastTaskResult", LastResultName(row.Task.LastTaskResult));
                    }

                    w.WriteString("userMask", "0x" + row.UserMask.ToString("X8", CultureInfo.InvariantCulture));
                    w.WriteBoolean("trayMayRun", (row.UserMask & Sddl.FileExecute) != 0);
                    w.WriteStartArray("problems");
                    foreach (string problem in row.Problems)
                    {
                        w.WriteStringValue(problem);
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            });
            return;
        }

        TextWriter o = ctx.Out;
        o.WriteLine("Install folder: " + report.InstallFolder);
        o.WriteLine("Current user: " + (report.UserSid ?? "unknown"));
        o.WriteLine(TaskPlan.FolderPath + ": " + Presence(report.FolderResult));
        if (report.FolderSddl is not null)
        {
            o.WriteLine("  SDDL " + report.FolderSddl);
            o.WriteLine("  Check: " + (report.FolderProblems.Count == 0 ? "ok" : string.Join(" ", report.FolderProblems)));
        }

        foreach (ProbeTaskRow row in report.Tasks)
        {
            o.WriteLine(TaskPlan.TaskPath(row.Name) + ": " + Presence(row.OpenResult));
            if (row.Task is null)
            {
                continue;
            }

            o.WriteLine("  SDDL " + (row.Task.Sddl ?? "unreadable"));
            o.WriteLine("  Principal " + (row.Summary.UserId ?? "?") + (row.Summary.LogonType is null ? "" : ", " + row.Summary.LogonType) +
                        (row.Summary.RunLevel is null ? "" : ", " + row.Summary.RunLevel));
            o.WriteLine("  Action " + (row.Summary.Command ?? "?") + " " + (row.Summary.Arguments ?? ""));
            o.WriteLine("  State " + row.Task.State.ToString(CultureInfo.InvariantCulture) + ", last result " +
                        (LastResultName(row.Task.LastTaskResult) ?? "unreadable"));
            o.WriteLine("  This user may start it: " + ((row.UserMask & Sddl.FileExecute) != 0 ? "yes" : "no") +
                        " (0x" + row.UserMask.ToString("X8", CultureInfo.InvariantCulture) + ")");
            o.WriteLine("  Check: " + (row.Problems.Count == 0 ? "ok" : string.Join(" ", row.Problems)));
        }

        o.WriteLine("Set up: " + (report.SetUp ? "yes" : "no"));
    }
}
