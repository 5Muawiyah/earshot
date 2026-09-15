using System.Reflection;
using System.Runtime.InteropServices;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Interop;

// Pins the COM vtable slot of every declared method to the SDK header order (um\mmdeviceapi.idl,
// um\propsys.idl, um\devicetopology.idl, shared\ksproxy.h, um\taskschd.h). The learn.microsoft.com
// interface pages list methods alphabetically, so a declaration copied from them would pass a name check
// and call the wrong method. IUnknown interfaces start at slot 3; dual interfaces at slot 7.
[TestClass]
public sealed class ComVtableOrderTests
{
    private const int IUnknownSlots = 3;
    private const int IDispatchSlots = 7;

    private static readonly string[] MMDeviceEnumerator =
        ["EnumAudioEndpoints", "GetDefaultAudioEndpoint", "GetDevice", "RegisterEndpointNotificationCallback", "UnregisterEndpointNotificationCallback"];

    private static readonly string[] MMDeviceCollection = ["GetCount", "Item"];

    private static readonly string[] MMDevice = ["Activate", "OpenPropertyStore", "GetId", "GetState"];

    private static readonly string[] MMEndpoint = ["GetDataFlow"];

    private static readonly string[] PropertyStore = ["GetCount", "GetAt", "GetValue", "SetValue", "Commit"];

    private static readonly string[] MMNotificationClient =
        ["OnDeviceStateChanged", "OnDeviceAdded", "OnDeviceRemoved", "OnDefaultDeviceChanged", "OnPropertyValueChanged"];

    private static readonly string[] DeviceTopologyMethods =
        ["GetConnectorCount", "GetConnector", "GetSubunitCount", "GetSubunit", "GetPartById", "GetDeviceId", "GetSignalPath"];

    // IConnector::GetType is declared as GetConnectorType so it does not hide object.GetType.
    private static readonly string[] Connector =
        ["GetConnectorType", "GetDataFlow", "ConnectTo", "Disconnect", "IsConnected", "GetConnectedTo", "GetConnectorIdConnectedTo", "GetDeviceIdConnectedTo"];

    private static readonly string[] Part =
    [
        "GetName", "GetLocalId", "GetGlobalId", "GetPartType", "GetSubType", "GetControlInterfaceCount", "GetControlInterface",
        "EnumPartsIncoming", "EnumPartsOutgoing", "GetTopologyObject", "Activate", "RegisterControlChangeCallback", "UnregisterControlChangeCallback",
    ];

    private static readonly string[] KsJackDescription = ["GetJackCount", "GetJackDescription"];

    private static readonly string[] KsControlMethods = ["KsProperty", "KsMethod", "KsEvent"];

    private static readonly string[] TaskService =
        ["GetFolder", "GetRunningTasks", "NewTask", "Connect", "get_Connected", "get_TargetServer", "get_ConnectedUser", "get_ConnectedDomain", "get_HighestVersion"];

    private static readonly string[] TaskFolder =
    [
        "get_Name", "get_Path", "GetFolder", "GetFolders", "CreateFolder", "DeleteFolder", "GetTask", "GetTasks", "DeleteTask",
        "RegisterTask", "RegisterTaskDefinition", "GetSecurityDescriptor", "SetSecurityDescriptor",
    ];

    private static readonly string[] RegisteredTask =
    [
        "get_Name", "get_Path", "get_State", "get_Enabled", "put_Enabled", "Run", "RunEx", "GetInstances", "get_LastRunTime",
        "get_LastTaskResult", "get_NumberOfMissedRuns", "get_NextRunTime", "get_Definition", "get_Xml", "GetSecurityDescriptor",
        "SetSecurityDescriptor", "Stop", "GetRunTimes",
    ];

    private static readonly string[] RunningTask =
        ["get_Name", "get_InstanceGuid", "get_Path", "get_State", "get_CurrentAction", "Stop", "Refresh", "get_EnginePID"];

    private static readonly string[] RegisteredTaskCollection = ["get_Count", "get_Item", "get__NewEnum"];

    private static readonly string[] TaskDefinition =
    [
        "get_RegistrationInfo", "put_RegistrationInfo", "get_Triggers", "put_Triggers", "get_Settings", "put_Settings", "get_Data", "put_Data",
        "get_Principal", "put_Principal", "get_Actions", "put_Actions", "get_XmlText", "put_XmlText",
    ];

    private static readonly string[] RegistrationInfo =
    [
        "get_Description", "put_Description", "get_Author", "put_Author", "get_Version", "put_Version", "get_Date", "put_Date",
        "get_Documentation", "put_Documentation", "get_XmlText", "put_XmlText", "get_URI", "put_URI", "get_SecurityDescriptor",
        "put_SecurityDescriptor", "get_Source", "put_Source",
    ];

    private static readonly string[] Principal =
    [
        "get_Id", "put_Id", "get_DisplayName", "put_DisplayName", "get_UserId", "put_UserId", "get_LogonType", "put_LogonType",
        "get_GroupId", "put_GroupId", "get_RunLevel", "put_RunLevel",
    ];

    private static readonly string[] TaskSettings =
    [
        "get_AllowDemandStart", "put_AllowDemandStart", "get_RestartInterval", "put_RestartInterval", "get_RestartCount", "put_RestartCount",
        "get_MultipleInstances", "put_MultipleInstances", "get_StopIfGoingOnBatteries", "put_StopIfGoingOnBatteries",
        "get_DisallowStartIfOnBatteries", "put_DisallowStartIfOnBatteries", "get_AllowHardTerminate", "put_AllowHardTerminate",
        "get_StartWhenAvailable", "put_StartWhenAvailable", "get_XmlText", "put_XmlText", "get_RunOnlyIfNetworkAvailable",
        "put_RunOnlyIfNetworkAvailable", "get_ExecutionTimeLimit", "put_ExecutionTimeLimit", "get_Enabled", "put_Enabled",
        "get_DeleteExpiredTaskAfter", "put_DeleteExpiredTaskAfter", "get_Priority", "put_Priority", "get_Compatibility", "put_Compatibility",
        "get_Hidden", "put_Hidden", "get_IdleSettings", "put_IdleSettings", "get_RunOnlyIfIdle", "put_RunOnlyIfIdle", "get_WakeToRun",
        "put_WakeToRun", "get_NetworkSettings", "put_NetworkSettings",
    ];

    private static readonly string[] ActionCollection =
        ["get_Count", "get_Item", "get__NewEnum", "get_XmlText", "put_XmlText", "Create", "Remove", "Clear", "get_Context", "put_Context"];

    private static readonly string[] Action = ["get_Id", "put_Id", "get_Type"];

    private static readonly string[] ExecAction =
        ["get_Id", "put_Id", "get_Type", "get_Path", "put_Path", "get_Arguments", "put_Arguments", "get_WorkingDirectory", "put_WorkingDirectory"];

    private static readonly string[] TriggerCollection = ["get_Count", "get_Item", "get__NewEnum", "Create", "Remove", "Clear"];

    private static readonly string[] Trigger =
    [
        "get_Type", "get_Id", "put_Id", "get_Repetition", "put_Repetition", "get_ExecutionTimeLimit", "put_ExecutionTimeLimit",
        "get_StartBoundary", "put_StartBoundary", "get_EndBoundary", "put_EndBoundary", "get_Enabled", "put_Enabled",
    ];

    private static readonly string[] BootTrigger = [.. Trigger, "get_Delay", "put_Delay"];

    public static IEnumerable<object[]> Interfaces =>
    [
        [typeof(IMMDeviceEnumerator), IUnknownSlots, MMDeviceEnumerator],
        [typeof(IMMDeviceCollection), IUnknownSlots, MMDeviceCollection],
        [typeof(IMMDevice), IUnknownSlots, MMDevice],
        [typeof(IMMEndpoint), IUnknownSlots, MMEndpoint],
        [typeof(IPropertyStore), IUnknownSlots, PropertyStore],
        [typeof(IMMNotificationClient), IUnknownSlots, MMNotificationClient],
        [typeof(IDeviceTopology), IUnknownSlots, DeviceTopologyMethods],
        [typeof(IConnector), IUnknownSlots, Connector],
        [typeof(IPart), IUnknownSlots, Part],
        [typeof(IKsJackDescription), IUnknownSlots, KsJackDescription],
        [typeof(IKsControl), IUnknownSlots, KsControlMethods],
        [typeof(ITaskService), IDispatchSlots, TaskService],
        [typeof(ITaskFolder), IDispatchSlots, TaskFolder],
        [typeof(IRegisteredTask), IDispatchSlots, RegisteredTask],
        [typeof(IRunningTask), IDispatchSlots, RunningTask],
        [typeof(IRegisteredTaskCollection), IDispatchSlots, RegisteredTaskCollection],
        [typeof(ITaskDefinition), IDispatchSlots, TaskDefinition],
        [typeof(IRegistrationInfo), IDispatchSlots, RegistrationInfo],
        [typeof(IPrincipal), IDispatchSlots, Principal],
        [typeof(ITaskSettings), IDispatchSlots, TaskSettings],
        [typeof(IActionCollection), IDispatchSlots, ActionCollection],
        [typeof(IAction), IDispatchSlots, Action],
        [typeof(IExecAction), IDispatchSlots, ExecAction],
        [typeof(ITriggerCollection), IDispatchSlots, TriggerCollection],
        [typeof(ITrigger), IDispatchSlots, Trigger],
        [typeof(IBootTrigger), IDispatchSlots, BootTrigger],
    ];

    public static string InterfaceName(MethodInfo method, object[] data) => ((Type)data[0]).Name;

    [TestMethod]
    [DynamicData(nameof(Interfaces), DynamicDataDisplayName = nameof(InterfaceName))]
    public void MethodsOccupyTheHeaderSlots(Type type, int firstSlot, string[] headerOrder)
    {
        // The runtime lays out a [ComImport] vtable from the start slot in method definition order, which is
        // metadata token order.
        string[] definitionOrder = type.GetMethods().OrderBy(method => method.MetadataToken).Select(method => method.Name).ToArray();

        Assert.AreEqual(firstSlot, Marshal.GetStartComSlot(type), type.Name + " first slot");
        Assert.AreEqual(firstSlot + headerOrder.Length - 1, Marshal.GetEndComSlot(type), type.Name + " last slot");
        CollectionAssert.AreEqual(headerOrder, definitionOrder, type.Name + " method order differs from the header.");
    }

    [TestMethod]
    [DynamicData(nameof(Interfaces), DynamicDataDisplayName = nameof(InterfaceName))]
    public void EveryMethodKeepsItsHresult(Type type, int firstSlot, string[] headerOrder)
    {
        Assert.IsGreaterThanOrEqualTo(IUnknownSlots, firstSlot);
        Assert.IsNotEmpty(headerOrder);
        foreach (MethodInfo method in type.GetMethods())
        {
            Assert.AreEqual(typeof(int), method.ReturnType, type.Name + "." + method.Name + " must return the HRESULT.");
            Assert.IsTrue(
                (method.MethodImplementationFlags & MethodImplAttributes.PreserveSig) != 0,
                type.Name + "." + method.Name + " must be [PreserveSig].");
        }
    }

    [TestMethod]
    public void ComImportInterfacesDeclareTheirBinding()
    {
        foreach (Type type in Interfaces.Select(row => (Type)row[0]))
        {
            var binding = type.GetCustomAttribute<InterfaceTypeAttribute>();
            Assert.IsNotNull(binding, type.Name);
            Assert.IsTrue(type.IsImport, type.Name + " must be [ComImport].");
            ComInterfaceType expected = Marshal.GetStartComSlot(type) == IDispatchSlots
                ? ComInterfaceType.InterfaceIsDual
                : ComInterfaceType.InterfaceIsIUnknown;
            Assert.AreEqual(expected, binding.Value, type.Name);
        }
    }
}
