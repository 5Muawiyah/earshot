using System.Runtime.InteropServices;

namespace Earshot.Interop;

// Task Scheduler 2.0 COM surface used by install (folder with SDDL, SYSTEM principal, exec action, boot
// trigger, settings), the tray (GetTask, RunEx with a BSTR array, State, LastTaskResult,
// GetSecurityDescriptor, Xml) and uninstall (DeleteTask, DeleteFolder).
//
// Approach: early-bound dual interfaces ([ComImport], InterfaceIsDual) in the exact vtable order of the
// Windows SDK header um\taskschd.h, with [PreserveSig] on every method. Chosen over late binding through
// IDispatch because:
//   - every call hands back its HRESULT, so a missing task (0x80070002), an existing folder (0x800700B7)
//     or a SCHED_E_* code is recorded as a StepOutcome, with no exception-driven control flow and no
//     TargetInvocationException to unwrap;
//   - member names and argument types are checked by the compiler instead of being resolved by name at
//     run time, and nothing depends on the dynamic COM binder;
//   - the slot order is pinned by a hardware-free unit test (Marshal.GetComSlotForMethodInfo), and the
//     whole surface can be exercised read-only: Connect, GetFolder, GetTask, and NewTask to build an
//     unregistered definition whose XmlText is read back.
// A dual interface reserves the three IUnknown and four IDispatch slots itself, so each declaration starts
// at the first method after IDispatch::Invoke. Derived interfaces (IExecAction : IAction,
// IBootTrigger : ITrigger) repeat the base methods first, as the vtable does.
//
// VARIANT arguments are object values marshalled as VARIANT: null is VT_EMPTY, a string is VT_BSTR and a
// string[] is a SAFEARRAY of BSTR (the RunEx parameter form).
// https://learn.microsoft.com/en-us/windows/win32/taskschd/task-scheduler-reference
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex
internal static class TaskSchedulerCom
{
    internal static readonly Guid CLSID_TaskScheduler = new("0F87369F-A4E5-4CFC-BD3E-73E6154572DD");

    // TASK_LOGON_TYPE. SERVICE_ACCOUNT covers Local System; its password must be VT_EMPTY or VT_NULL.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_logon_type
    internal const int TASK_LOGON_NONE = 0;
    internal const int TASK_LOGON_PASSWORD = 1;
    internal const int TASK_LOGON_S4U = 2;
    internal const int TASK_LOGON_INTERACTIVE_TOKEN = 3;
    internal const int TASK_LOGON_GROUP = 4;
    internal const int TASK_LOGON_SERVICE_ACCOUNT = 5;
    internal const int TASK_LOGON_INTERACTIVE_TOKEN_OR_PASSWORD = 6;

    // TASK_RUNLEVEL_TYPE. Ignored for Local System.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_runlevel_type
    internal const int TASK_RUNLEVEL_LUA = 0;
    internal const int TASK_RUNLEVEL_HIGHEST = 1;

    // TASK_TRIGGER_TYPE2 (only the boot trigger is used). Only Administrators can create one.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-iboottrigger
    internal const int TASK_TRIGGER_BOOT = 8;

    // TASK_ACTION_TYPE (only exec is used).
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_action_type
    internal const int TASK_ACTION_EXEC = 0;

    // TASK_INSTANCES_POLICY. The schema default IgnoreNew drops a request while one runs; Earshot queues.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_instances_policy
    internal const int TASK_INSTANCES_PARALLEL = 0;
    internal const int TASK_INSTANCES_QUEUE = 1;
    internal const int TASK_INSTANCES_IGNORE_NEW = 2;
    internal const int TASK_INSTANCES_STOP_EXISTING = 3;

    // TASK_COMPATIBILITY.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_compatibility
    internal const int TASK_COMPATIBILITY_V2_4 = 6;

    // TASK_STATE. RunEx completion polling waits for the state to leave QUEUED and RUNNING.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_state
    internal const int TASK_STATE_UNKNOWN = 0;
    internal const int TASK_STATE_DISABLED = 1;
    internal const int TASK_STATE_QUEUED = 2;
    internal const int TASK_STATE_READY = 3;
    internal const int TASK_STATE_RUNNING = 4;

    // TASK_CREATION flags for RegisterTaskDefinition.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-registertaskdefinition
    internal const int TASK_VALIDATE_ONLY = 0x1;
    internal const int TASK_CREATE = 0x2;
    internal const int TASK_UPDATE = 0x4;
    internal const int TASK_CREATE_OR_UPDATE = TASK_CREATE | TASK_UPDATE;
    internal const int TASK_DISABLE = 0x8;
    internal const int TASK_DONT_ADD_PRINCIPAL_ACE = 0x10;
    internal const int TASK_IGNORE_REGISTRATION_TRIGGERS = 0x20;

    // TASK_ENUM_FLAGS for GetTasks and GetFolders.
    internal const int TASK_ENUM_HIDDEN = 0x1;

    // TASK_RUN_FLAGS for RunEx.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_run_flags
    internal const int TASK_RUN_NO_FLAGS = 0;
    internal const int TASK_RUN_AS_SELF = 0x1;
    internal const int TASK_RUN_IGNORE_CONSTRAINTS = 0x2;
    internal const int TASK_RUN_USE_SESSION_ID = 0x4;
    internal const int TASK_RUN_USER_SID = 0x8;

    // SECURITY_INFORMATION bits for GetSecurityDescriptor (winnt.h).
    // https://learn.microsoft.com/en-us/windows/win32/secauthz/security-information
    internal const int OWNER_SECURITY_INFORMATION = 0x1;
    internal const int GROUP_SECURITY_INFORMATION = 0x2;
    internal const int DACL_SECURITY_INFORMATION = 0x4;

    // HRESULTs Earshot branches on. Names come from NativeCodes.
    // A missing task or folder surfaces as ERROR_FILE_NOT_FOUND through COM.
    internal const int HRESULT_ERROR_FILE_NOT_FOUND = unchecked((int)0x80070002);

    // ITaskFolder::CreateFolder when the folder already exists.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-createfolder
    internal const int HRESULT_ERROR_ALREADY_EXISTS = unchecked((int)0x800700B7);

    // Creates the Task Scheduler service object with CoCreateInstance and CLSCTX_INPROC_SERVER, as
    // Microsoft's sample does, on a thread that has initialised COM. Call Connect before anything else.
    // Returns the HRESULT; service is null on failure.
    // https://learn.microsoft.com/en-us/windows/win32/taskschd/boot-trigger-example--c---
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskservice-connect
    internal static int TryCreateService(out ITaskService? service) =>
        ComActivation.Create(CLSID_TaskScheduler, ComActivation.CLSCTX_INPROC_SERVER, out service);
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-itaskservice
[ComImport]
[Guid("2FABA4C7-4DA9-4013-9697-20CC3FD40F85")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface ITaskService
{
    [PreserveSig]
    int GetFolder([MarshalAs(UnmanagedType.BStr)] string? path, out ITaskFolder? ppFolder);

    [PreserveSig]
    int GetRunningTasks(int flags, [MarshalAs(UnmanagedType.IUnknown)] out object? ppRunningTasks);

    // Returns an empty, unregistered definition. Nothing is written until RegisterTaskDefinition.
    [PreserveSig]
    int NewTask(uint flags, out ITaskDefinition? ppDefinition);

    // Pass null for every argument to connect to the local service as the caller.
    [PreserveSig]
    int Connect(
        [MarshalAs(UnmanagedType.Struct)] object? serverName,
        [MarshalAs(UnmanagedType.Struct)] object? user,
        [MarshalAs(UnmanagedType.Struct)] object? domain,
        [MarshalAs(UnmanagedType.Struct)] object? password);

    [PreserveSig]
    int get_Connected([MarshalAs(UnmanagedType.VariantBool)] out bool pConnected);

    [PreserveSig]
    int get_TargetServer([MarshalAs(UnmanagedType.BStr)] out string? pServer);

    [PreserveSig]
    int get_ConnectedUser([MarshalAs(UnmanagedType.BStr)] out string? pUser);

    [PreserveSig]
    int get_ConnectedDomain([MarshalAs(UnmanagedType.BStr)] out string? pDomain);

    [PreserveSig]
    int get_HighestVersion(out uint pVersion);
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-itaskfolder
[ComImport]
[Guid("8CFAC062-A080-4C15-9A88-AA7C2AF80DFC")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface ITaskFolder
{
    [PreserveSig]
    int get_Name([MarshalAs(UnmanagedType.BStr)] out string? pName);

    [PreserveSig]
    int get_Path([MarshalAs(UnmanagedType.BStr)] out string? pPath);

    [PreserveSig]
    int GetFolder([MarshalAs(UnmanagedType.BStr)] string path, out ITaskFolder? ppFolder);

    // ITaskFolderCollection is not declared; the collection comes back as IUnknown.
    [PreserveSig]
    int GetFolders(int flags, [MarshalAs(UnmanagedType.IUnknown)] out object? ppFolders);

    // sddl is an SDDL_REVISION_1 string (VT_BSTR). 0x800700B7 if the folder exists; E_INVALIDARG for bad SDDL.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-createfolder
    [PreserveSig]
    int CreateFolder([MarshalAs(UnmanagedType.BStr)] string subFolderName, [MarshalAs(UnmanagedType.Struct)] object? sddl, out ITaskFolder? ppFolder);

    // flags is reserved (0).
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-deletefolder
    [PreserveSig]
    int DeleteFolder([MarshalAs(UnmanagedType.BStr)] string subFolderName, int flags);

    // A missing task returns 0x80070002.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-gettask
    [PreserveSig]
    int GetTask([MarshalAs(UnmanagedType.BStr)] string path, out IRegisteredTask? ppTask);

    [PreserveSig]
    int GetTasks(int flags, out IRegisteredTaskCollection? ppTasks);

    // flags is reserved (0).
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-deletetask
    [PreserveSig]
    int DeleteTask([MarshalAs(UnmanagedType.BStr)] string name, int flags);

    [PreserveSig]
    int RegisterTask(
        [MarshalAs(UnmanagedType.BStr)] string? path,
        [MarshalAs(UnmanagedType.BStr)] string xmlText,
        int flags,
        [MarshalAs(UnmanagedType.Struct)] object? userId,
        [MarshalAs(UnmanagedType.Struct)] object? password,
        int logonType,
        [MarshalAs(UnmanagedType.Struct)] object? sddl,
        out IRegisteredTask? ppTask);

    // For TASK_LOGON_SERVICE_ACCOUNT pass userId "S-1-5-18" and password null (VT_EMPTY).
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itaskfolder-registertaskdefinition
    [PreserveSig]
    int RegisterTaskDefinition(
        [MarshalAs(UnmanagedType.BStr)] string? path,
        ITaskDefinition pDefinition,
        int flags,
        [MarshalAs(UnmanagedType.Struct)] object? userId,
        [MarshalAs(UnmanagedType.Struct)] object? password,
        int logonType,
        [MarshalAs(UnmanagedType.Struct)] object? sddl,
        out IRegisteredTask? ppTask);

    [PreserveSig]
    int GetSecurityDescriptor(int securityInformation, [MarshalAs(UnmanagedType.BStr)] out string? pSddl);

    [PreserveSig]
    int SetSecurityDescriptor([MarshalAs(UnmanagedType.BStr)] string sddl, int flags);
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-iregisteredtask
[ComImport]
[Guid("9C86F320-DEE3-4DD1-B972-A303F26B061E")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IRegisteredTask
{
    [PreserveSig]
    int get_Name([MarshalAs(UnmanagedType.BStr)] out string? pName);

    [PreserveSig]
    int get_Path([MarshalAs(UnmanagedType.BStr)] out string? pPath);

    // TASK_STATE.
    [PreserveSig]
    int get_State(out int pState);

    [PreserveSig]
    int get_Enabled([MarshalAs(UnmanagedType.VariantBool)] out bool pEnabled);

    [PreserveSig]
    int put_Enabled([MarshalAs(UnmanagedType.VariantBool)] bool enabled);

    [PreserveSig]
    int Run([MarshalAs(UnmanagedType.Struct)] object? parameters, out IRunningTask? ppRunningTask);

    // parameters: null, one string ($(Arg0)) or a string[] of up to 32 values ($(Arg0), $(Arg1), ...).
    // Anyone with execute permission on the task can pass any strings: the gate treats them as hostile.
    // RunEx on a disabled task returns S_OK and does not run.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex
    [PreserveSig]
    int RunEx([MarshalAs(UnmanagedType.Struct)] object? parameters, int flags, int sessionID, [MarshalAs(UnmanagedType.BStr)] string? user, out IRunningTask? ppRunningTask);

    // IRunningTaskCollection is not declared; the collection comes back as IUnknown.
    [PreserveSig]
    int GetInstances(int flags, [MarshalAs(UnmanagedType.IUnknown)] out object? ppRunningTasks);

    // DATE (OLE automation date).
    [PreserveSig]
    int get_LastRunTime(out double pLastRunTime);

    // Documented only as the result of the last run. That it equals the exec action's exit code is
    // undocumented, so it is advisory; the CfgMgr32 read is the ground truth.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-get_lasttaskresult
    [PreserveSig]
    int get_LastTaskResult(out int pLastTaskResult);

    [PreserveSig]
    int get_NumberOfMissedRuns(out int pNumberOfMissedRuns);

    [PreserveSig]
    int get_NextRunTime(out double pNextRunTime);

    [PreserveSig]
    int get_Definition(out ITaskDefinition? ppDefinition);

    [PreserveSig]
    int get_Xml([MarshalAs(UnmanagedType.BStr)] out string? pXml);

    // securityInformation: OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION.
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-getsecuritydescriptor
    [PreserveSig]
    int GetSecurityDescriptor(int securityInformation, [MarshalAs(UnmanagedType.BStr)] out string? pSddl);

    [PreserveSig]
    int SetSecurityDescriptor([MarshalAs(UnmanagedType.BStr)] string sddl, int flags);

    [PreserveSig]
    int Stop(int flags);

    // Hidden and restricted in the header; declared so the vtable is complete. pRunTimes is
    // CoTaskMem-allocated.
    [PreserveSig]
    int GetRunTimes(ref SYSTEMTIME pstStart, ref SYSTEMTIME pstEnd, ref uint pCount, out nint pRunTimes);
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-irunningtask
[ComImport]
[Guid("653758FB-7B9A-4F1E-A471-BEEB8E9B834E")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IRunningTask
{
    [PreserveSig]
    int get_Name([MarshalAs(UnmanagedType.BStr)] out string? pName);

    [PreserveSig]
    int get_InstanceGuid([MarshalAs(UnmanagedType.BStr)] out string? pGuid);

    [PreserveSig]
    int get_Path([MarshalAs(UnmanagedType.BStr)] out string? pPath);

    // TASK_STATE.
    [PreserveSig]
    int get_State(out int pState);

    [PreserveSig]
    int get_CurrentAction([MarshalAs(UnmanagedType.BStr)] out string? pName);

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Refresh();

    [PreserveSig]
    int get_EnginePID(out uint pPID);
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-iregisteredtaskcollection
[ComImport]
[Guid("86627EB4-42A7-41E4-A4D9-AC33A72F2D52")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IRegisteredTaskCollection
{
    [PreserveSig]
    int get_Count(out int pCount);

    // index: a 1-based int or a task name (VARIANT).
    [PreserveSig]
    int get_Item([MarshalAs(UnmanagedType.Struct)] object index, out IRegisteredTask? ppRegisteredTask);

    [PreserveSig]
    int get__NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object? ppEnum);
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-itaskdefinition
[ComImport]
[Guid("F5BC8FC5-536D-4F77-B852-FBC1356FDEB6")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface ITaskDefinition
{
    [PreserveSig]
    int get_RegistrationInfo(out IRegistrationInfo? ppRegistrationInfo);

    [PreserveSig]
    int put_RegistrationInfo(IRegistrationInfo pRegistrationInfo);

    [PreserveSig]
    int get_Triggers(out ITriggerCollection? ppTriggers);

    [PreserveSig]
    int put_Triggers(ITriggerCollection pTriggers);

    [PreserveSig]
    int get_Settings(out ITaskSettings? ppSettings);

    [PreserveSig]
    int put_Settings(ITaskSettings pSettings);

    [PreserveSig]
    int get_Data([MarshalAs(UnmanagedType.BStr)] out string? pData);

    [PreserveSig]
    int put_Data([MarshalAs(UnmanagedType.BStr)] string? data);

    [PreserveSig]
    int get_Principal(out IPrincipal? ppPrincipal);

    [PreserveSig]
    int put_Principal(IPrincipal pPrincipal);

    [PreserveSig]
    int get_Actions(out IActionCollection? ppActions);

    [PreserveSig]
    int put_Actions(IActionCollection pActions);

    [PreserveSig]
    int get_XmlText([MarshalAs(UnmanagedType.BStr)] out string? pXml);

    [PreserveSig]
    int put_XmlText([MarshalAs(UnmanagedType.BStr)] string xml);
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-iregistrationinfo
[ComImport]
[Guid("416D8B73-CB41-4EA1-805C-9BE9A5AC4A74")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IRegistrationInfo
{
    [PreserveSig]
    int get_Description([MarshalAs(UnmanagedType.BStr)] out string? pDescription);

    [PreserveSig]
    int put_Description([MarshalAs(UnmanagedType.BStr)] string? description);

    [PreserveSig]
    int get_Author([MarshalAs(UnmanagedType.BStr)] out string? pAuthor);

    [PreserveSig]
    int put_Author([MarshalAs(UnmanagedType.BStr)] string? author);

    [PreserveSig]
    int get_Version([MarshalAs(UnmanagedType.BStr)] out string? pVersion);

    [PreserveSig]
    int put_Version([MarshalAs(UnmanagedType.BStr)] string? version);

    [PreserveSig]
    int get_Date([MarshalAs(UnmanagedType.BStr)] out string? pDate);

    [PreserveSig]
    int put_Date([MarshalAs(UnmanagedType.BStr)] string? date);

    [PreserveSig]
    int get_Documentation([MarshalAs(UnmanagedType.BStr)] out string? pDocumentation);

    [PreserveSig]
    int put_Documentation([MarshalAs(UnmanagedType.BStr)] string? documentation);

    [PreserveSig]
    int get_XmlText([MarshalAs(UnmanagedType.BStr)] out string? pText);

    [PreserveSig]
    int put_XmlText([MarshalAs(UnmanagedType.BStr)] string text);

    [PreserveSig]
    int get_URI([MarshalAs(UnmanagedType.BStr)] out string? pUri);

    [PreserveSig]
    int put_URI([MarshalAs(UnmanagedType.BStr)] string? uri);

    [PreserveSig]
    int get_SecurityDescriptor([MarshalAs(UnmanagedType.Struct)] out object? pSddl);

    [PreserveSig]
    int put_SecurityDescriptor([MarshalAs(UnmanagedType.Struct)] object? sddl);

    [PreserveSig]
    int get_Source([MarshalAs(UnmanagedType.BStr)] out string? pSource);

    [PreserveSig]
    int put_Source([MarshalAs(UnmanagedType.BStr)] string? source);
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-iprincipal
[ComImport]
[Guid("D98D51E5-C9B4-496A-A9C1-18980261CF0F")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IPrincipal
{
    [PreserveSig]
    int get_Id([MarshalAs(UnmanagedType.BStr)] out string? pId);

    [PreserveSig]
    int put_Id([MarshalAs(UnmanagedType.BStr)] string? id);

    [PreserveSig]
    int get_DisplayName([MarshalAs(UnmanagedType.BStr)] out string? pName);

    [PreserveSig]
    int put_DisplayName([MarshalAs(UnmanagedType.BStr)] string? name);

    [PreserveSig]
    int get_UserId([MarshalAs(UnmanagedType.BStr)] out string? pUser);

    [PreserveSig]
    int put_UserId([MarshalAs(UnmanagedType.BStr)] string? user);

    // TASK_LOGON_TYPE.
    [PreserveSig]
    int get_LogonType(out int pLogon);

    [PreserveSig]
    int put_LogonType(int logon);

    [PreserveSig]
    int get_GroupId([MarshalAs(UnmanagedType.BStr)] out string? pGroup);

    [PreserveSig]
    int put_GroupId([MarshalAs(UnmanagedType.BStr)] string? group);

    // TASK_RUNLEVEL_TYPE.
    [PreserveSig]
    int get_RunLevel(out int pRunLevel);

    [PreserveSig]
    int put_RunLevel(int runLevel);
}

// Schema defaults that bite: DisallowStartIfOnBatteries defaults to true, MultipleInstances to IgnoreNew,
// ExecutionTimeLimit to 72 hours, and the StopIfGoingOnBatteries page contradicts itself. Set all of
// them explicitly.
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-itasksettings
// https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-disallowstartifonbatteries-settingstype-element
[ComImport]
[Guid("8FD4711D-2D02-4C8C-87E3-EFF699DE127E")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface ITaskSettings
{
    [PreserveSig]
    int get_AllowDemandStart([MarshalAs(UnmanagedType.VariantBool)] out bool pAllowDemandStart);

    [PreserveSig]
    int put_AllowDemandStart([MarshalAs(UnmanagedType.VariantBool)] bool allowDemandStart);

    [PreserveSig]
    int get_RestartInterval([MarshalAs(UnmanagedType.BStr)] out string? pRestartInterval);

    [PreserveSig]
    int put_RestartInterval([MarshalAs(UnmanagedType.BStr)] string? restartInterval);

    [PreserveSig]
    int get_RestartCount(out int pRestartCount);

    [PreserveSig]
    int put_RestartCount(int restartCount);

    // TASK_INSTANCES_POLICY.
    [PreserveSig]
    int get_MultipleInstances(out int pPolicy);

    [PreserveSig]
    int put_MultipleInstances(int policy);

    [PreserveSig]
    int get_StopIfGoingOnBatteries([MarshalAs(UnmanagedType.VariantBool)] out bool pStopIfOnBatteries);

    [PreserveSig]
    int put_StopIfGoingOnBatteries([MarshalAs(UnmanagedType.VariantBool)] bool stopIfOnBatteries);

    [PreserveSig]
    int get_DisallowStartIfOnBatteries([MarshalAs(UnmanagedType.VariantBool)] out bool pDisallowStart);

    [PreserveSig]
    int put_DisallowStartIfOnBatteries([MarshalAs(UnmanagedType.VariantBool)] bool disallowStart);

    [PreserveSig]
    int get_AllowHardTerminate([MarshalAs(UnmanagedType.VariantBool)] out bool pAllowHardTerminate);

    [PreserveSig]
    int put_AllowHardTerminate([MarshalAs(UnmanagedType.VariantBool)] bool allowHardTerminate);

    [PreserveSig]
    int get_StartWhenAvailable([MarshalAs(UnmanagedType.VariantBool)] out bool pStartWhenAvailable);

    [PreserveSig]
    int put_StartWhenAvailable([MarshalAs(UnmanagedType.VariantBool)] bool startWhenAvailable);

    [PreserveSig]
    int get_XmlText([MarshalAs(UnmanagedType.BStr)] out string? pText);

    [PreserveSig]
    int put_XmlText([MarshalAs(UnmanagedType.BStr)] string text);

    [PreserveSig]
    int get_RunOnlyIfNetworkAvailable([MarshalAs(UnmanagedType.VariantBool)] out bool pRunOnlyIfNetworkAvailable);

    [PreserveSig]
    int put_RunOnlyIfNetworkAvailable([MarshalAs(UnmanagedType.VariantBool)] bool runOnlyIfNetworkAvailable);

    // ISO 8601 duration, for example "PT2M".
    [PreserveSig]
    int get_ExecutionTimeLimit([MarshalAs(UnmanagedType.BStr)] out string? pExecutionTimeLimit);

    [PreserveSig]
    int put_ExecutionTimeLimit([MarshalAs(UnmanagedType.BStr)] string? executionTimeLimit);

    [PreserveSig]
    int get_Enabled([MarshalAs(UnmanagedType.VariantBool)] out bool pEnabled);

    [PreserveSig]
    int put_Enabled([MarshalAs(UnmanagedType.VariantBool)] bool enabled);

    [PreserveSig]
    int get_DeleteExpiredTaskAfter([MarshalAs(UnmanagedType.BStr)] out string? pExpirationDelay);

    [PreserveSig]
    int put_DeleteExpiredTaskAfter([MarshalAs(UnmanagedType.BStr)] string? expirationDelay);

    [PreserveSig]
    int get_Priority(out int pPriority);

    [PreserveSig]
    int put_Priority(int priority);

    // TASK_COMPATIBILITY.
    [PreserveSig]
    int get_Compatibility(out int pCompatLevel);

    [PreserveSig]
    int put_Compatibility(int compatLevel);

    [PreserveSig]
    int get_Hidden([MarshalAs(UnmanagedType.VariantBool)] out bool pHidden);

    [PreserveSig]
    int put_Hidden([MarshalAs(UnmanagedType.VariantBool)] bool hidden);

    // IIdleSettings is not declared: read as IUnknown, write as a raw interface pointer.
    [PreserveSig]
    int get_IdleSettings([MarshalAs(UnmanagedType.IUnknown)] out object? ppIdleSettings);

    [PreserveSig]
    int put_IdleSettings(nint pIdleSettings);

    [PreserveSig]
    int get_RunOnlyIfIdle([MarshalAs(UnmanagedType.VariantBool)] out bool pRunOnlyIfIdle);

    [PreserveSig]
    int put_RunOnlyIfIdle([MarshalAs(UnmanagedType.VariantBool)] bool runOnlyIfIdle);

    [PreserveSig]
    int get_WakeToRun([MarshalAs(UnmanagedType.VariantBool)] out bool pWake);

    [PreserveSig]
    int put_WakeToRun([MarshalAs(UnmanagedType.VariantBool)] bool wake);

    // INetworkSettings is not declared: read as IUnknown, write as a raw interface pointer.
    [PreserveSig]
    int get_NetworkSettings([MarshalAs(UnmanagedType.IUnknown)] out object? ppNetworkSettings);

    [PreserveSig]
    int put_NetworkSettings(nint pNetworkSettings);
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-iactioncollection
[ComImport]
[Guid("02820E19-7B98-4ED2-B2E8-FDCCCEFF619B")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IActionCollection
{
    [PreserveSig]
    int get_Count(out int pCount);

    // index is 1-based.
    [PreserveSig]
    int get_Item(int index, out IAction? ppAction);

    [PreserveSig]
    int get__NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object? ppEnum);

    [PreserveSig]
    int get_XmlText([MarshalAs(UnmanagedType.BStr)] out string? pText);

    [PreserveSig]
    int put_XmlText([MarshalAs(UnmanagedType.BStr)] string text);

    // TASK_ACTION_TYPE. Cast the result to IExecAction for TASK_ACTION_EXEC.
    [PreserveSig]
    int Create(int type, out IAction? ppAction);

    [PreserveSig]
    int Remove([MarshalAs(UnmanagedType.Struct)] object index);

    [PreserveSig]
    int Clear();

    [PreserveSig]
    int get_Context([MarshalAs(UnmanagedType.BStr)] out string? pContext);

    [PreserveSig]
    int put_Context([MarshalAs(UnmanagedType.BStr)] string? context);
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-iaction
[ComImport]
[Guid("BAE54997-48B1-4CBE-9965-D6BE263EBEA4")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IAction
{
    [PreserveSig]
    int get_Id([MarshalAs(UnmanagedType.BStr)] out string? pId);

    [PreserveSig]
    int put_Id([MarshalAs(UnmanagedType.BStr)] string? id);

    // TASK_ACTION_TYPE.
    [PreserveSig]
    int get_Type(out int pType);
}

// IExecAction : IAction. $(Arg0)..$(Arg32) are substituted in Arguments and WorkingDirectory, never in Path.
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-iexecaction
// https://learn.microsoft.com/en-us/windows/win32/taskschd/task-actions
[ComImport]
[Guid("4C3D624D-FD6B-49A3-B9B7-09CB3CD3F047")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IExecAction
{
    // IAction
    [PreserveSig]
    int get_Id([MarshalAs(UnmanagedType.BStr)] out string? pId);

    [PreserveSig]
    int put_Id([MarshalAs(UnmanagedType.BStr)] string? id);

    [PreserveSig]
    int get_Type(out int pType);

    // IExecAction
    [PreserveSig]
    int get_Path([MarshalAs(UnmanagedType.BStr)] out string? pPath);

    [PreserveSig]
    int put_Path([MarshalAs(UnmanagedType.BStr)] string? path);

    [PreserveSig]
    int get_Arguments([MarshalAs(UnmanagedType.BStr)] out string? pArgument);

    [PreserveSig]
    int put_Arguments([MarshalAs(UnmanagedType.BStr)] string? argument);

    [PreserveSig]
    int get_WorkingDirectory([MarshalAs(UnmanagedType.BStr)] out string? pWorkingDirectory);

    [PreserveSig]
    int put_WorkingDirectory([MarshalAs(UnmanagedType.BStr)] string? workingDirectory);
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-itriggercollection
[ComImport]
[Guid("85DF5081-1B24-4F32-878A-D9D14DF4CB77")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface ITriggerCollection
{
    [PreserveSig]
    int get_Count(out int pCount);

    // index is 1-based.
    [PreserveSig]
    int get_Item(int index, out ITrigger? ppTrigger);

    [PreserveSig]
    int get__NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object? ppEnum);

    // TASK_TRIGGER_TYPE2. Cast the result to IBootTrigger for TASK_TRIGGER_BOOT.
    [PreserveSig]
    int Create(int type, out ITrigger? ppTrigger);

    [PreserveSig]
    int Remove([MarshalAs(UnmanagedType.Struct)] object index);

    [PreserveSig]
    int Clear();
}

// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-itrigger
[ComImport]
[Guid("09941815-EA89-4B5B-89E0-2A773801FAC3")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface ITrigger
{
    // TASK_TRIGGER_TYPE2.
    [PreserveSig]
    int get_Type(out int pType);

    [PreserveSig]
    int get_Id([MarshalAs(UnmanagedType.BStr)] out string? pId);

    [PreserveSig]
    int put_Id([MarshalAs(UnmanagedType.BStr)] string? id);

    // IRepetitionPattern is not declared: read as IUnknown, write as a raw interface pointer.
    [PreserveSig]
    int get_Repetition([MarshalAs(UnmanagedType.IUnknown)] out object? ppRepeat);

    [PreserveSig]
    int put_Repetition(nint pRepeat);

    [PreserveSig]
    int get_ExecutionTimeLimit([MarshalAs(UnmanagedType.BStr)] out string? pTimeLimit);

    [PreserveSig]
    int put_ExecutionTimeLimit([MarshalAs(UnmanagedType.BStr)] string? timelimit);

    [PreserveSig]
    int get_StartBoundary([MarshalAs(UnmanagedType.BStr)] out string? pStart);

    [PreserveSig]
    int put_StartBoundary([MarshalAs(UnmanagedType.BStr)] string? start);

    [PreserveSig]
    int get_EndBoundary([MarshalAs(UnmanagedType.BStr)] out string? pEnd);

    [PreserveSig]
    int put_EndBoundary([MarshalAs(UnmanagedType.BStr)] string? end);

    [PreserveSig]
    int get_Enabled([MarshalAs(UnmanagedType.VariantBool)] out bool pEnabled);

    [PreserveSig]
    int put_Enabled([MarshalAs(UnmanagedType.VariantBool)] bool enabled);
}

// IBootTrigger : ITrigger. Starts the task when the Task Scheduler service starts at boot; its ordering
// against Bluetooth auto-reconnect is undocumented.
// https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nn-taskschd-iboottrigger
[ComImport]
[Guid("2A9C35DA-D357-41F4-BBC1-207AC1B1F3CB")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IBootTrigger
{
    // ITrigger
    [PreserveSig]
    int get_Type(out int pType);

    [PreserveSig]
    int get_Id([MarshalAs(UnmanagedType.BStr)] out string? pId);

    [PreserveSig]
    int put_Id([MarshalAs(UnmanagedType.BStr)] string? id);

    [PreserveSig]
    int get_Repetition([MarshalAs(UnmanagedType.IUnknown)] out object? ppRepeat);

    [PreserveSig]
    int put_Repetition(nint pRepeat);

    [PreserveSig]
    int get_ExecutionTimeLimit([MarshalAs(UnmanagedType.BStr)] out string? pTimeLimit);

    [PreserveSig]
    int put_ExecutionTimeLimit([MarshalAs(UnmanagedType.BStr)] string? timelimit);

    [PreserveSig]
    int get_StartBoundary([MarshalAs(UnmanagedType.BStr)] out string? pStart);

    [PreserveSig]
    int put_StartBoundary([MarshalAs(UnmanagedType.BStr)] string? start);

    [PreserveSig]
    int get_EndBoundary([MarshalAs(UnmanagedType.BStr)] out string? pEnd);

    [PreserveSig]
    int put_EndBoundary([MarshalAs(UnmanagedType.BStr)] string? end);

    [PreserveSig]
    int get_Enabled([MarshalAs(UnmanagedType.VariantBool)] out bool pEnabled);

    [PreserveSig]
    int put_Enabled([MarshalAs(UnmanagedType.VariantBool)] bool enabled);

    // IBootTrigger. ISO 8601 duration, for example "PT30S".
    [PreserveSig]
    int get_Delay([MarshalAs(UnmanagedType.BStr)] out string? pDelay);

    [PreserveSig]
    int put_Delay([MarshalAs(UnmanagedType.BStr)] string? delay);
}
