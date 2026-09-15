using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Boot;

internal enum TaskPrincipalMode
{
    // Default: every task runs as Local System (TASK_LOGON_SERVICE_ACCOUNT).
    System,

    // install --principal user: Gate and Protect run as the interactive user with the highest run level;
    // BootBlock stays SYSTEM because it must run before anyone logs on.
    InteractiveUser,
}

internal sealed record TaskPrincipal(string UserId, int LogonType, int RunLevel);

internal sealed record TaskSpec(
    string Name,
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    string ExecutionTimeLimit,
    bool BootTrigger,
    TaskPrincipal Principal,
    string Sddl,
    bool UserMayRun)
{
    public string Path => TaskPlan.TaskPath(Name);
}

// The three scheduled tasks install registers, described once so install, the tray's pre-run check and
// the probe agree on every value.
//
//   \Earshot\Gate       on demand   gate $(Arg0) $(Arg1) $(Arg2)   PT2M   the user may start it
//   \Earshot\Protect    on demand   gate $(Arg0) $(Arg1) $(Arg2)   PT5M   the user may start it
//   \Earshot\BootBlock  boot        gate boot                      PT2M   SYSTEM only, the user may read it
//
// The literal gate token comes before any caller-supplied value, so $(Arg0) can never select install,
// uninstall or probe. $(ArgN) is substituted in Arguments, never in Path. Settings that default badly are
// set explicitly: DisallowStartIfOnBatteries and StopIfGoingOnBatteries false, MultipleInstances Queue.
// https://learn.microsoft.com/en-us/windows/win32/taskschd/task-actions
// https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-disallowstartifonbatteries-settingstype-element
// https://learn.microsoft.com/en-us/windows/security/application-security/application-control/administrator-protection/
internal static class TaskPlan
{
    public const string FolderName = "Earshot";
    public const string FolderPath = "\\Earshot";
    public const string GateTaskName = "Gate";
    public const string ProtectTaskName = "Protect";
    public const string BootTaskName = "BootBlock";
    public const string GateArguments = "gate $(Arg0) $(Arg1) $(Arg2)";
    public const string BootArguments = "gate boot";
    public const string GateTimeLimit = "PT2M";

    // BluetoothSetServiceState installs and removes drivers for an undocumented time and must not be
    // stopped part way.
    public const string ProtectTimeLimit = "PT5M";

    public const string ExecutableName = "Earshot.exe";

    public static readonly IReadOnlyList<string> TaskNames = [GateTaskName, ProtectTaskName, BootTaskName];

    public static string TaskPath(string name) => FolderPath + "\\" + name;

    // The one place a task principal is chosen.
    public static TaskPrincipal PrincipalFor(string taskName, TaskPrincipalMode mode, string userSid)
    {
        ArgumentNullException.ThrowIfNull(taskName);
        if (mode == TaskPrincipalMode.System || string.Equals(taskName, BootTaskName, StringComparison.Ordinal))
        {
            // RunLevel is ignored for Local System.
            // https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks
            return new TaskPrincipal(Sddl.LocalSystemSid, TaskSchedulerCom.TASK_LOGON_SERVICE_ACCOUNT, TaskSchedulerCom.TASK_RUNLEVEL_HIGHEST);
        }

        if (!Sddl.IsUserSid(userSid))
        {
            throw new ArgumentException("Not a user SID: " + userSid, nameof(userSid));
        }

        return new TaskPrincipal(userSid, TaskSchedulerCom.TASK_LOGON_INTERACTIVE_TOKEN, TaskSchedulerCom.TASK_RUNLEVEL_HIGHEST);
    }

    public static IReadOnlyList<TaskSpec> Build(string installFolder, string userSid, TaskPrincipalMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installFolder);
        string exe = System.IO.Path.Combine(installFolder, ExecutableName);
        return
        [
            new TaskSpec(GateTaskName, exe, GateArguments, installFolder, GateTimeLimit, BootTrigger: false,
                PrincipalFor(GateTaskName, mode, userSid), Sddl.RunnableTask(userSid), UserMayRun: true),
            new TaskSpec(ProtectTaskName, exe, GateArguments, installFolder, ProtectTimeLimit, BootTrigger: false,
                PrincipalFor(ProtectTaskName, mode, userSid), Sddl.RunnableTask(userSid), UserMayRun: true),
            new TaskSpec(BootTaskName, exe, BootArguments, installFolder, GateTimeLimit, BootTrigger: true,
                PrincipalFor(BootTaskName, mode, userSid), Sddl.ReadableTask(userSid), UserMayRun: false),
        ];
    }

    public static TaskSpec Spec(string taskName, string installFolder, string userSid, TaskPrincipalMode mode) =>
        Build(installFolder, userSid, mode).Single(s => string.Equals(s.Name, taskName, StringComparison.Ordinal));
}

// Checks a task's registered XML against its spec and lists every difference. Elements the service omits
// when they hold the schema default (Enabled and AllowStartOnDemand, both true) may be absent; the settings
// whose defaults are wrong for Earshot must be present. A Local System principal reads back as S-1-5-18
// without a LogonType element; an interactive user reads back as the SID with LogonType InteractiveToken.
// https://learn.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-schema
internal static class TaskXmlCheck
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        MaxCharactersInDocument = 1024 * 1024,
    };

    // steps, when given, receives the account lookup step when a principal name does not resolve.
    public static IReadOnlyList<string> Verify(string? xml, TaskSpec expected, Func<string, AccountLookup> accountToSid, IList<StepOutcome>? steps)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(accountToSid);
        var problems = new List<string>();
        XElement? root = Load(xml, problems);
        if (root is null)
        {
            return problems;
        }

        CheckPrincipal(root, expected, accountToSid, problems, steps);
        CheckAction(root, expected, problems);
        CheckTriggers(root, expected, problems);
        CheckSettings(root, expected, problems);
        return problems;
    }

    // For a reader that does not know which principal install chose: the SYSTEM form, or for Gate and
    // Protect the interactive user form, must match in full.
    public static IReadOnlyList<string> VerifyInstalled(
        string? xml, string taskName, string installFolder, string userSid, Func<string, AccountLookup> accountToSid, IList<StepOutcome>? steps)
    {
        if (!Sddl.IsUserSid(userSid))
        {
            return ["The current user has no usable SID."];
        }

        IReadOnlyList<string> asSystem = Verify(xml, TaskPlan.Spec(taskName, installFolder, userSid, TaskPrincipalMode.System), accountToSid, steps);
        if (asSystem.Count == 0 || string.Equals(taskName, TaskPlan.BootTaskName, StringComparison.Ordinal))
        {
            return asSystem;
        }

        // A principal name that does not resolve adds its lookup step here even though the SYSTEM form's
        // problems are the ones returned.
        IReadOnlyList<string> asUser = Verify(xml, TaskPlan.Spec(taskName, installFolder, userSid, TaskPrincipalMode.InteractiveUser), accountToSid, steps);
        return asUser.Count == 0 ? asUser : asSystem;
    }

    // The principal and action a task's XML names, for display. Null fields were absent or unreadable.
    internal sealed record Summary(string? UserId, string? LogonType, string? RunLevel, string? Command, string? Arguments, int Triggers);

    public static Summary Describe(string? xml)
    {
        var ignored = new List<string>();
        XElement? root = Load(xml, ignored);
        if (root is null)
        {
            return new Summary(null, null, null, null, null, 0);
        }

        XElement? principal = root.Element(Ns + "Principals")?.Element(Ns + "Principal");
        XElement? exec = root.Element(Ns + "Actions")?.Element(Ns + "Exec");
        return new Summary(
            principal?.Element(Ns + "UserId")?.Value ?? principal?.Element(Ns + "GroupId")?.Value,
            principal?.Element(Ns + "LogonType")?.Value,
            principal?.Element(Ns + "RunLevel")?.Value,
            exec?.Element(Ns + "Command")?.Value,
            exec?.Element(Ns + "Arguments")?.Value,
            root.Element(Ns + "Triggers")?.Elements().Count() ?? 0);
    }

    private static XElement? Load(string? xml, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            problems.Add("No task XML was read.");
            return null;
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings);
            XElement root = XDocument.Load(reader).Root!;
            if (root.Name != Ns + "Task")
            {
                problems.Add("The XML is not a task definition.");
                return null;
            }

            return root;
        }
        catch (XmlException ex)
        {
            problems.Add("The task XML does not parse: " + ex.Message);
            return null;
        }
    }

    private static void CheckPrincipal(
        XElement root, TaskSpec expected, Func<string, AccountLookup> accountToSid, List<string> problems, IList<StepOutcome>? steps)
    {
        List<XElement> principals = root.Element(Ns + "Principals")?.Elements(Ns + "Principal").ToList() ?? [];
        if (principals.Count != 1)
        {
            problems.Add("The task has " + principals.Count + " principals, not one.");
            return;
        }

        XElement principal = principals[0];
        if (principal.Element(Ns + "GroupId") is not null)
        {
            problems.Add("The principal is a group.");
        }

        string userId = principal.Element(Ns + "UserId")?.Value.Trim() ?? "";
        string? logonType = principal.Element(Ns + "LogonType")?.Value.Trim();
        string? runLevel = principal.Element(Ns + "RunLevel")?.Value.Trim();

        if (expected.Principal.LogonType == TaskSchedulerCom.TASK_LOGON_SERVICE_ACCOUNT)
        {
            bool isSystem = userId.Equals(Sddl.LocalSystemSid, StringComparison.OrdinalIgnoreCase) ||
                            userId.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                            userId.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase);
            if (!isSystem)
            {
                problems.Add("The principal is " + userId + ", not Local System.");
            }

            if (logonType is not (null or "ServiceAccount"))
            {
                problems.Add("The logon type is " + logonType + ", not ServiceAccount.");
            }

            return;
        }

        string? sid = userId;
        StepOutcome? failedLookup = null;
        if (!userId.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            AccountLookup lookup = accountToSid(userId);
            sid = lookup.Sid;
            if (sid is null)
            {
                failedLookup = lookup.Step;
                steps?.Add(lookup.Step);
            }
        }

        if (!string.Equals(sid, expected.Principal.UserId, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("The principal is " + userId + ", not the user " + expected.Principal.UserId +
                         (failedLookup is null ? "." : " (the name did not resolve: " + failedLookup.CodeName + ")."));
        }

        if (!string.Equals(logonType, "InteractiveToken", StringComparison.Ordinal))
        {
            problems.Add("The logon type is " + (logonType ?? "missing") + ", not InteractiveToken.");
        }

        if (!string.Equals(runLevel, "HighestAvailable", StringComparison.Ordinal))
        {
            problems.Add("The run level is " + (runLevel ?? "missing") + ", not HighestAvailable.");
        }
    }

    private static void CheckAction(XElement root, TaskSpec expected, List<string> problems)
    {
        List<XElement> actions = root.Element(Ns + "Actions")?.Elements().ToList() ?? [];
        if (actions.Count != 1 || actions[0].Name != Ns + "Exec")
        {
            problems.Add("The task does not have exactly one Exec action.");
            return;
        }

        XElement exec = actions[0];
        string command = (exec.Element(Ns + "Command")?.Value ?? "").Trim().Trim('"');
        if (!string.Equals(command, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("The action runs " + command + ", not " + expected.ExecutablePath + ".");
        }

        string arguments = exec.Element(Ns + "Arguments")?.Value ?? "";
        if (!string.Equals(arguments, expected.Arguments, StringComparison.Ordinal))
        {
            problems.Add("The action arguments are '" + arguments + "', not '" + expected.Arguments + "'.");
        }

        string? workingDirectory = exec.Element(Ns + "WorkingDirectory")?.Value.Trim().Trim('"').TrimEnd('\\');
        if (workingDirectory is not null &&
            !string.Equals(workingDirectory, expected.WorkingDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("The working directory is " + workingDirectory + ".");
        }
    }

    private static void CheckTriggers(XElement root, TaskSpec expected, List<string> problems)
    {
        List<XElement> triggers = root.Element(Ns + "Triggers")?.Elements().ToList() ?? [];
        if (!expected.BootTrigger)
        {
            if (triggers.Count != 0)
            {
                problems.Add("The task has triggers; it should start only on demand.");
            }

            return;
        }

        if (triggers.Count != 1 || triggers[0].Name != Ns + "BootTrigger")
        {
            problems.Add("The task does not have exactly one boot trigger.");
            return;
        }

        if (!IsAbsentOr(triggers[0].Element(Ns + "Enabled"), "true"))
        {
            problems.Add("The boot trigger is disabled.");
        }
    }

    private static void CheckSettings(XElement root, TaskSpec expected, List<string> problems)
    {
        XElement? settings = root.Element(Ns + "Settings");
        if (settings is null)
        {
            problems.Add("The task has no settings.");
            return;
        }

        RequireValue(settings, "DisallowStartIfOnBatteries", "false", problems);
        RequireValue(settings, "StopIfGoingOnBatteries", "false", problems);
        RequireValue(settings, "ExecutionTimeLimit", expected.ExecutionTimeLimit, problems);
        RequireValue(settings, "MultipleInstancesPolicy", "Queue", problems);
        if (!IsAbsentOr(settings.Element(Ns + "AllowStartOnDemand"), "true"))
        {
            problems.Add("AllowStartOnDemand is not true.");
        }

        if (!IsAbsentOr(settings.Element(Ns + "Enabled"), "true"))
        {
            problems.Add("The task is disabled.");
        }
    }

    private static void RequireValue(XElement parent, string name, string expected, List<string> problems)
    {
        string? value = parent.Element(Ns + name)?.Value.Trim();
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            problems.Add(name + " is " + (value ?? "missing") + ", not " + expected + ".");
        }
    }

    private static bool IsAbsentOr(XElement? element, string value) =>
        element is null || string.Equals(element.Value.Trim(), value, StringComparison.Ordinal);
}

// Fills a new, unregistered ITaskDefinition from a spec. Nothing is written to the service here; install
// passes the result to RegisterTaskDefinition, and a read-only test reads its XmlText back. Every property
// call is a step; the first failure stops. Must run on the thread that created the definition.
// https://learn.microsoft.com/en-us/windows/win32/taskschd/boot-trigger-example--c---
internal static class TaskDefinitionWriter
{
    public static int Apply(ITaskDefinition definition, TaskSpec spec, IList<StepOutcome> steps)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(steps);
        var created = new List<object>();
        try
        {
            string step = "task-definition:" + spec.Name;

            int hr = definition.get_RegistrationInfo(out IRegistrationInfo? info);
            if (!Track(steps, step + ":registration", hr, info, created) || info is null)
            {
                return Failed(hr);
            }

            if (!Put(steps, step + ":description", hr = info.put_Description("Earshot boot block: " + spec.Name + ".")))
            {
                return Failed(hr);
            }

            hr = definition.get_Principal(out IPrincipal? principal);
            if (!Track(steps, step + ":principal", hr, principal, created) || principal is null ||
                !Put(steps, step + ":user", hr = principal.put_UserId(spec.Principal.UserId)) ||
                !Put(steps, step + ":logon-type", hr = principal.put_LogonType(spec.Principal.LogonType)) ||
                !Put(steps, step + ":run-level", hr = principal.put_RunLevel(spec.Principal.RunLevel)))
            {
                return Failed(hr);
            }

            hr = definition.get_Settings(out ITaskSettings? settings);
            if (!Track(steps, step + ":settings", hr, settings, created) || settings is null ||
                !Put(steps, step + ":enabled", hr = settings.put_Enabled(true)) ||
                !Put(steps, step + ":allow-demand-start", hr = settings.put_AllowDemandStart(true)) ||
                !Put(steps, step + ":disallow-on-batteries", hr = settings.put_DisallowStartIfOnBatteries(false)) ||
                !Put(steps, step + ":stop-on-batteries", hr = settings.put_StopIfGoingOnBatteries(false)) ||
                !Put(steps, step + ":time-limit", hr = settings.put_ExecutionTimeLimit(spec.ExecutionTimeLimit)) ||
                !Put(steps, step + ":instances", hr = settings.put_MultipleInstances(TaskSchedulerCom.TASK_INSTANCES_QUEUE)) ||
                !Put(steps, step + ":compatibility", hr = settings.put_Compatibility(TaskSchedulerCom.TASK_COMPATIBILITY_V2_4)))
            {
                return Failed(hr);
            }

            hr = definition.get_Actions(out IActionCollection? actions);
            if (!Track(steps, step + ":actions", hr, actions, created) || actions is null)
            {
                return Failed(hr);
            }

            hr = actions.Create(TaskSchedulerCom.TASK_ACTION_EXEC, out IAction? action);
            if (!Track(steps, step + ":exec-action", hr, action, created) || action is null)
            {
                return Failed(hr);
            }

            var exec = (IExecAction)action;
            if (!Put(steps, step + ":path", hr = exec.put_Path(spec.ExecutablePath)) ||
                !Put(steps, step + ":arguments", hr = exec.put_Arguments(spec.Arguments)) ||
                !Put(steps, step + ":working-directory", hr = exec.put_WorkingDirectory(spec.WorkingDirectory)))
            {
                return Failed(hr);
            }

            if (spec.BootTrigger)
            {
                hr = definition.get_Triggers(out ITriggerCollection? triggers);
                if (!Track(steps, step + ":triggers", hr, triggers, created) || triggers is null)
                {
                    return Failed(hr);
                }

                hr = triggers.Create(TaskSchedulerCom.TASK_TRIGGER_BOOT, out ITrigger? trigger);
                if (!Track(steps, step + ":boot-trigger", hr, trigger, created) || trigger is null ||
                    !Put(steps, step + ":boot-trigger-enabled", hr = trigger.put_Enabled(true)))
                {
                    return Failed(hr);
                }
            }

            return 0;
        }
        finally
        {
            created.Reverse();
            foreach (object comObject in created)
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
    }

    private static int Failed(int hr) => hr < 0 ? hr : ComActivation.E_POINTER;

    private static bool Track(IList<StepOutcome> steps, string step, int hr, object? comObject, List<object> created)
    {
        if (comObject is not null)
        {
            created.Add(comObject);
        }

        if (hr < 0 || comObject is null)
        {
            steps.Add(StepOutcomes.FromHResult(step, hr < 0 ? hr : ComActivation.E_POINTER));
            return false;
        }

        return true;
    }

    private static bool Put(IList<StepOutcome> steps, string step, int hr)
    {
        if (hr < 0)
        {
            steps.Add(StepOutcomes.FromHResult(step, hr));
            return false;
        }

        return true;
    }
}
