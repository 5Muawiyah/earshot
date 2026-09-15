using System.Globalization;
using System.Security;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Interop;

namespace Earshot.Tests.Phase4;

internal static class TestUsers
{
    public const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
}

// Creates plain folders (no ACL) and returns the SDDL a test chooses for each path.
internal sealed class FakeFolderSecurity : IFolderSecurity
{
    private readonly Dictionary<string, Queue<string>> _sddl = new(StringComparer.OrdinalIgnoreCase);

    public string DefaultMachineSddl { get; set; } = Sddl.MachineFolder;

    public string DefaultInstallSddl { get; set; } =
        "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;0x1200a9;;;BU)(A;OICIIOID;GA;;;CO)";

    public List<string> Created { get; } = new();

    public bool FailCreate { get; set; }

    // Each read of path returns the next queued SDDL; the last one repeats.
    public void Queue(string path, params string[] sddl) => _sddl[path] = new Queue<string>(sddl);

    public StepOutcome CreateHardened(string path)
    {
        if (FailCreate)
        {
            return StepOutcomes.FromHResult("machine-folder-create", unchecked((int)0x80070005), path);
        }

        Directory.CreateDirectory(path);
        Created.Add(path);
        return new StepOutcome("machine-folder-create", true, 0, "S_OK", path);
    }

    public StepOutcome ReadSddl(string path, out string? sddl)
    {
        sddl = null;
        if (!Directory.Exists(path))
        {
            return StepOutcomes.FromHResult("folder-acl-read", unchecked((int)0x80070003), path);
        }

        if (_sddl.TryGetValue(path, out Queue<string>? queue) && queue.Count > 0)
        {
            sddl = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        }
        else
        {
            sddl = path.Contains("ProgramData", StringComparison.OrdinalIgnoreCase) ? DefaultMachineSddl : DefaultInstallSddl;
        }

        return new StepOutcome("folder-acl-read", true, 0, "S_OK", path);
    }
}

// An in-memory Task Scheduler for install and uninstall. Registered tasks read back as the XML the real
// service produces for that spec (SYSTEM without LogonType, defaults omitted).
internal sealed class FakeTaskRegistrar : ITaskRegistrar
{
    public const int NotFound = unchecked((int)0x80070002);

    public string? FolderSddl { get; set; }

    // When set, reads of the folder return this instead of the SDDL it was created with.
    public string? FolderSddlReadBack { get; set; }

    public Dictionary<string, (string Sddl, string Xml)> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Calls { get; } = new();

    public int CreateFolderResult { get; set; }

    public int DeleteFolderResult { get; set; }

    public Func<TaskSpec, string>? XmlOverride { get; set; }

    public Func<TaskSpec, string>? SddlOverride { get; set; }

    public int RegisterFailsFor { get; set; } = -1;

    public bool FolderExists => FolderSddl is not null;

    public int ReadFolderSddl(string folderPath, out string? sddl)
    {
        Calls.Add("read-folder " + folderPath);
        sddl = FolderSddl is null ? null : FolderSddlReadBack ?? FolderSddl;
        return FolderExists ? 0 : NotFound;
    }

    public int ListTasks(string folderPath, out IReadOnlyList<string> names)
    {
        Calls.Add("list " + folderPath);
        names = Tasks.Keys.Select(k => k[(TaskPlan.FolderPath.Length + 1)..]).ToList();
        return FolderExists ? 0 : NotFound;
    }

    public int DeleteTask(string folderPath, string name)
    {
        Calls.Add("delete-task " + name);
        if (!FolderExists)
        {
            return unchecked((int)0x80070003);
        }

        return Tasks.Remove(folderPath + "\\" + name) ? 0 : NotFound;
    }

    public int DeleteFolder(string parentPath, string name)
    {
        Calls.Add("delete-folder " + name);
        if (DeleteFolderResult < 0)
        {
            return DeleteFolderResult;
        }

        if (!FolderExists)
        {
            return NotFound;
        }

        FolderSddl = null;
        return 0;
    }

    public int CreateFolder(string parentPath, string name, string sddl)
    {
        Calls.Add("create-folder " + name);
        if (CreateFolderResult < 0)
        {
            return CreateFolderResult;
        }

        if (FolderExists)
        {
            return unchecked((int)0x800700B7);
        }

        FolderSddl = sddl;
        return 0;
    }

    public int Register(string folderPath, TaskSpec spec, IList<StepOutcome> steps)
    {
        Calls.Add("register " + spec.Name + " " + spec.Principal.UserId + " " + spec.Principal.LogonType.ToString(CultureInfo.InvariantCulture));
        if (RegisterFailsFor == Tasks.Count)
        {
            return unchecked((int)0x80070005);
        }

        Tasks[folderPath + "\\" + spec.Name] = (SddlOverride?.Invoke(spec) ?? spec.Sddl, XmlOverride?.Invoke(spec) ?? TaskXml.For(spec));
        return 0;
    }

    public int ReadTask(string taskPath, out string? sddl, out string? xml)
    {
        Calls.Add("read-task " + taskPath);
        if (Tasks.TryGetValue(taskPath, out var task))
        {
            sddl = task.Sddl;
            xml = task.Xml;
            return 0;
        }

        sddl = null;
        xml = null;
        return NotFound;
    }
}

// Task XML in the shape Task Scheduler returns for a registered task.
internal static class TaskXml
{
    public static string For(TaskSpec spec, string? userId = null, string? logonType = null, string? runLevel = "HighestAvailable",
        string? command = null, string? arguments = null, string? timeLimit = null, bool includeTrigger = true, string multipleInstances = "Queue")
    {
        bool system = spec.Principal.LogonType == TaskSchedulerCom.TASK_LOGON_SERVICE_ACCOUNT;
        userId ??= spec.Principal.UserId;
        logonType ??= system ? null : "InteractiveToken";
        string principal = "<UserId>" + SecurityElement.Escape(userId) + "</UserId>" +
                           (logonType is null ? "" : "<LogonType>" + logonType + "</LogonType>") +
                           (runLevel is null ? "" : "<RunLevel>" + runLevel + "</RunLevel>");
        string triggers = spec.BootTrigger && includeTrigger ? "<Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>" : "";
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
               "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
               "<RegistrationInfo><Description>Earshot</Description><URI>" + SecurityElement.Escape(spec.Path) + "</URI></RegistrationInfo>" +
               triggers +
               "<Principals><Principal id=\"Author\">" + principal + "</Principal></Principals>" +
               "<Settings><MultipleInstancesPolicy>" + multipleInstances + "</MultipleInstancesPolicy>" +
               "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
               "<IdleSettings><StopOnIdleEnd>true</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
               "<ExecutionTimeLimit>" + (timeLimit ?? spec.ExecutionTimeLimit) + "</ExecutionTimeLimit><Priority>7</Priority></Settings>" +
               "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(command ?? spec.ExecutablePath) + "</Command>" +
               "<Arguments>" + SecurityElement.Escape(arguments ?? spec.Arguments) + "</Arguments>" +
               "<WorkingDirectory>" + SecurityElement.Escape(spec.WorkingDirectory) + "</WorkingDirectory></Exec></Actions></Task>";
    }
}

internal sealed class FakeToken(string? sid, bool admin) : IProcessToken
{
    public string? UserSid { get; } = sid;

    public bool IsLocalSystem => UserSid == Sddl.LocalSystemSid;

    public bool IsElevatedAdministrator { get; } = admin;

    public static FakeToken System => new(Sddl.LocalSystemSid, true);

    public static FakeToken ElevatedUser => new(TestUsers.Sid, true);

    public static FakeToken PlainUser => new(TestUsers.Sid, false);
}

internal sealed class ManualTime : TimeProvider
{
    private long _ticks;
    private DateTimeOffset _now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _ticks += by.Ticks;
        _now += by;
    }
}

internal sealed class RebootDeleteRecorder : IRebootDelete
{
    public List<string> Scheduled { get; } = new();

    public StepOutcome ScheduleDelete(string path)
    {
        Scheduled.Add(path);
        return StepOutcomes.FromWin32("delete-at-restart", 0, path);
    }
}
