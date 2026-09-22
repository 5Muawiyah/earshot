using System.Globalization;
using System.Reflection;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Interop;

// GUIDs and constants against the values in the SDK 10.0.26100.0 headers and the linked Microsoft docs. The
// expected strings are typed out independently of the declarations.
[TestClass]
public sealed class InteropConstantsTests
{
    [TestMethod]
    [DataRow(typeof(IMMDeviceEnumerator), "A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [DataRow(typeof(IMMDeviceCollection), "0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [DataRow(typeof(IMMDevice), "D666063F-1587-4E43-81F1-B948E807363F")]
    [DataRow(typeof(IMMEndpoint), "1BE09788-6894-4089-8586-9A2A6C265AC5")]
    [DataRow(typeof(IPropertyStore), "886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [DataRow(typeof(IMMNotificationClient), "7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [DataRow(typeof(IDeviceTopology), "2A07407E-6497-4A18-9787-32F79BD0D98F")]
    [DataRow(typeof(IConnector), "9C2C4058-23F5-41DE-877A-DF3AF236A09E")]
    [DataRow(typeof(IPart), "AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9")]
    [DataRow(typeof(IKsJackDescription), "4509F757-2D46-4637-8E62-CE7DB944F57B")]
    [DataRow(typeof(IKsControl), "28F54685-06FD-11D2-B27A-00A0C9223196")]
    [DataRow(typeof(ITaskService), "2FABA4C7-4DA9-4013-9697-20CC3FD40F85")]
    [DataRow(typeof(ITaskFolder), "8CFAC062-A080-4C15-9A88-AA7C2AF80DFC")]
    [DataRow(typeof(IRegisteredTask), "9C86F320-DEE3-4DD1-B972-A303F26B061E")]
    [DataRow(typeof(IRunningTask), "653758FB-7B9A-4F1E-A471-BEEB8E9B834E")]
    [DataRow(typeof(IRegisteredTaskCollection), "86627EB4-42A7-41E4-A4D9-AC33A72F2D52")]
    [DataRow(typeof(ITaskDefinition), "F5BC8FC5-536D-4F77-B852-FBC1356FDEB6")]
    [DataRow(typeof(IRegistrationInfo), "416D8B73-CB41-4EA1-805C-9BE9A5AC4A74")]
    [DataRow(typeof(IPrincipal), "D98D51E5-C9B4-496A-A9C1-18980261CF0F")]
    [DataRow(typeof(ITaskSettings), "8FD4711D-2D02-4C8C-87E3-EFF699DE127E")]
    [DataRow(typeof(IActionCollection), "02820E19-7B98-4ED2-B2E8-FDCCCEFF619B")]
    [DataRow(typeof(IAction), "BAE54997-48B1-4CBE-9965-D6BE263EBEA4")]
    [DataRow(typeof(IExecAction), "4C3D624D-FD6B-49A3-B9B7-09CB3CD3F047")]
    [DataRow(typeof(ITriggerCollection), "85DF5081-1B24-4F32-878A-D9D14DF4CB77")]
    [DataRow(typeof(ITrigger), "09941815-EA89-4B5B-89E0-2A773801FAC3")]
    [DataRow(typeof(IBootTrigger), "2A9C35DA-D357-41F4-BBC1-207AC1B1F3CB")]
    public void InterfaceIdsMatchTheSdk(Type type, string iid)
    {
        Assert.AreEqual(new Guid(iid), type.GUID, type.Name);
    }

    [TestMethod]
    public void ClassIdsMatchTheSdk()
    {
        Assert.AreEqual(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), CoreAudio.CLSID_MMDeviceEnumerator);
        Assert.AreEqual(new Guid("0F87369F-A4E5-4CFC-BD3E-73E6154572DD"), TaskSchedulerCom.CLSID_TaskScheduler);
    }

    [TestMethod]
    public void PropertyKeysMatchTheSdk()
    {
        AssertKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14, CoreAudio.PKEY_Device_FriendlyName);
        AssertKey("026E516E-B814-414B-83CD-856D6FEF4822", 2, CoreAudio.PKEY_DeviceInterface_FriendlyName);
        AssertKey("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2, CoreAudio.PKEY_Device_ContainerId);
        AssertKey("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E", 0, CoreAudio.PKEY_AudioEndpoint_FormFactor);
    }

    [TestMethod]
    public void DevPropKeysMatchTheSdk()
    {
        AssertKey("B725F130-47EF-101A-A5F1-02608C9EEBAC", 10, CfgMgr32.DEVPKEY_NAME);
        AssertKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14, CfgMgr32.DEVPKEY_Device_FriendlyName);
        AssertKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 12, CfgMgr32.DEVPKEY_Device_ConfigFlags);
        AssertKey("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2, CfgMgr32.DEVPKEY_Device_ContainerId);
        AssertKey("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 4, CfgMgr32.DEVPKEY_Device_InLocalMachineContainer);
        AssertKey("4340A6C5-93FA-4706-972C-7B648008A5A7", 2, CfgMgr32.DEVPKEY_Device_DevNodeStatus);
        AssertKey("4340A6C5-93FA-4706-972C-7B648008A5A7", 3, CfgMgr32.DEVPKEY_Device_ProblemCode);
        AssertKey("4340A6C5-93FA-4706-972C-7B648008A5A7", 8, CfgMgr32.DEVPKEY_Device_Parent);
        AssertKey("4340A6C5-93FA-4706-972C-7B648008A5A7", 9, CfgMgr32.DEVPKEY_Device_Children);
        AssertKey("540B947E-8B40-45BC-A8A2-6A0B894CBDA2", 5, CfgMgr32.DEVPKEY_Device_IsPresent);
        AssertKey("540B947E-8B40-45BC-A8A2-6A0B894CBDA2", 6, CfgMgr32.DEVPKEY_Device_HasProblem);
    }

    [TestMethod]
    public void PropertySetGuidsMatchTheSdk()
    {
        Assert.AreEqual(new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D"), KsControl.KSPROPSETID_BtAudio);
        Assert.AreEqual(new Guid("8C134960-51AD-11CF-878A-94F801C10000"), KsControl.KSPROPSETID_Pin);
    }

    [TestMethod]
    public void BluetoothServiceClassesMatchTheDocumentedServiceGuids()
    {
        Assert.AreEqual(new Guid("0000111E-0000-1000-8000-00805F9B34FB"), BluetoothApis.HandsfreeServiceClass);
        Assert.AreEqual(new Guid("00001108-0000-1000-8000-00805F9B34FB"), BluetoothApis.HeadsetServiceClass);
        Assert.AreEqual(new Guid("0000110B-0000-1000-8000-00805F9B34FB"), BluetoothApis.AudioSinkServiceClass);
    }

    // Constants are read back through reflection so the table compares the compiled values rather than two
    // constant expressions folded together by the compiler.
    [TestMethod]
    [DataRow(typeof(KsControl), "KSPROPERTY_ONESHOT_RECONNECT", 0L)]
    [DataRow(typeof(KsControl), "KSPROPERTY_ONESHOT_DISCONNECT", 1L)]
    [DataRow(typeof(KsControl), "KSPROPERTY_TYPE_GET", 0x1L)]
    [DataRow(typeof(KsControl), "KSPROPERTY_TYPE_SET", 0x2L)]
    [DataRow(typeof(KsControl), "KSPROPERTY_TYPE_BASICSUPPORT", 0x200L)]
    [DataRow(typeof(KsControl), "KSPROPERTY_PIN_CTYPES", 1L)]
    [DataRow(typeof(KsControl), "KSPROPERTY_PIN_DATAFLOW", 2L)]
    [DataRow(typeof(KsControl), "KSPROPERTY_PIN_NAME", 12L)]
    [DataRow(typeof(KsControl), "KSPIN_DATAFLOW_IN", 1L)]
    [DataRow(typeof(KsControl), "KSPIN_DATAFLOW_OUT", 2L)]
    [DataRow(typeof(CoreAudio), "eRender", 0L)]
    [DataRow(typeof(CoreAudio), "eCapture", 1L)]
    [DataRow(typeof(CoreAudio), "eAll", 2L)]
    [DataRow(typeof(CoreAudio), "eConsole", 0L)]
    [DataRow(typeof(CoreAudio), "eMultimedia", 1L)]
    [DataRow(typeof(CoreAudio), "eCommunications", 2L)]
    [DataRow(typeof(CoreAudio), "DEVICE_STATE_ACTIVE", 0x1L)]
    [DataRow(typeof(CoreAudio), "DEVICE_STATE_DISABLED", 0x2L)]
    [DataRow(typeof(CoreAudio), "DEVICE_STATE_NOTPRESENT", 0x4L)]
    [DataRow(typeof(CoreAudio), "DEVICE_STATE_UNPLUGGED", 0x8L)]
    [DataRow(typeof(CoreAudio), "DEVICE_STATEMASK_ALL", 0xFL)]
    [DataRow(typeof(CoreAudio), "STGM_READ", 0L)]
    [DataRow(typeof(CoreAudio), "E_NOTFOUND", -2147023728L)]
    [DataRow(typeof(CoreAudio), "E_NOINTERFACE", -2147467262L)]
    [DataRow(typeof(CoreAudio), "E_POINTER", -2147467261L)]
    [DataRow(typeof(CoreAudio), "AUDCLNT_E_DEVICE_INVALIDATED", -2004287484L)]
    [DataRow(typeof(CoreAudio), "ERROR_NO_SUCH_DEVICE_INTERFACE", -536870363L)]
    [DataRow(typeof(CoreAudio), "ERROR_NO_SUCH_DEVINST", -536870389L)]
    [DataRow(typeof(DeviceTopology), "CLSCTX_ALL", 0x17L)]
    [DataRow(typeof(PropVariantInterop), "VT_EMPTY", 0L)]
    [DataRow(typeof(PropVariantInterop), "VT_UI4", 19L)]
    [DataRow(typeof(PropVariantInterop), "VT_LPWSTR", 31L)]
    [DataRow(typeof(PropVariantInterop), "VT_CLSID", 72L)]
    [DataRow(typeof(CfgMgr32), "CM_DISABLE_UI_NOT_OK", 0x4L)]
    [DataRow(typeof(CfgMgr32), "CM_DISABLE_PERSIST", 0x8L)]
    [DataRow(typeof(CfgMgr32), "CM_LOCATE_DEVNODE_NORMAL", 0x0L)]
    [DataRow(typeof(CfgMgr32), "CM_LOCATE_DEVNODE_PHANTOM", 0x1L)]
    [DataRow(typeof(CfgMgr32), "CM_GETIDLIST_FILTER_NONE", 0x0L)]
    [DataRow(typeof(CfgMgr32), "CM_GETIDLIST_FILTER_ENUMERATOR", 0x1L)]
    [DataRow(typeof(CfgMgr32), "CM_GETIDLIST_FILTER_PRESENT", 0x100L)]
    [DataRow(typeof(CfgMgr32), "DN_HAS_PROBLEM", 0x400L)]
    [DataRow(typeof(CfgMgr32), "DN_DEVICE_DISCONNECTED", 0x02000000L)]
    [DataRow(typeof(CfgMgr32), "CM_PROB_DISABLED", 22L)]
    [DataRow(typeof(CfgMgr32), "CM_PROB_PHANTOM", 45L)]
    [DataRow(typeof(CfgMgr32), "CONFIGFLAG_DISABLED", 0x1L)]
    [DataRow(typeof(CfgMgr32), "CR_SUCCESS", 0x00L)]
    [DataRow(typeof(CfgMgr32), "CR_NO_SUCH_DEVNODE", 0x0DL)]
    [DataRow(typeof(CfgMgr32), "CR_REMOVE_VETOED", 0x17L)]
    [DataRow(typeof(CfgMgr32), "CR_BUFFER_SMALL", 0x1AL)]
    [DataRow(typeof(CfgMgr32), "CR_NEED_RESTART", 0x22L)]
    [DataRow(typeof(CfgMgr32), "CR_NO_SUCH_VALUE", 0x25L)]
    [DataRow(typeof(CfgMgr32), "CR_NOT_DISABLEABLE", 0x28L)]
    [DataRow(typeof(CfgMgr32), "CR_ACCESS_DENIED", 0x33L)]
    [DataRow(typeof(CfgMgr32), "DEVPROP_TYPE_UINT32", 0x7L)]
    [DataRow(typeof(CfgMgr32), "DEVPROP_TYPE_GUID", 0xDL)]
    [DataRow(typeof(CfgMgr32), "DEVPROP_TYPE_BOOLEAN", 0x11L)]
    [DataRow(typeof(CfgMgr32), "DEVPROP_TYPE_STRING", 0x12L)]
    [DataRow(typeof(CfgMgr32), "DEVPROP_TYPE_STRING_LIST", 0x2012L)]
    [DataRow(typeof(CfgMgr32), "MAX_DEVICE_ID_LEN", 200L)]
    [DataRow(typeof(BluetoothApis), "BLUETOOTH_SERVICE_DISABLE", 0x00L)]
    [DataRow(typeof(BluetoothApis), "BLUETOOTH_SERVICE_ENABLE", 0x01L)]
    [DataRow(typeof(BluetoothApis), "BLUETOOTH_MAX_NAME_SIZE", 248L)]
    [DataRow(typeof(BluetoothApis), "ERROR_MORE_DATA", 234L)]
    [DataRow(typeof(BluetoothApis), "ERROR_NO_MORE_ITEMS", 259L)]
    [DataRow(typeof(BluetoothApis), "ERROR_SERVICE_DOES_NOT_EXIST", 1060L)]
    [DataRow(typeof(BluetoothApis), "ERROR_REVISION_MISMATCH", 1306L)]
    [DataRow(typeof(BluetoothApis), "E_INVALIDARG", 0x80070057L)]
    [DataRow(typeof(TaskSchedulerCom), "TASK_LOGON_INTERACTIVE_TOKEN", 3L)]
    [DataRow(typeof(TaskSchedulerCom), "TASK_LOGON_SERVICE_ACCOUNT", 5L)]
    [DataRow(typeof(TaskSchedulerCom), "TASK_RUNLEVEL_HIGHEST", 1L)]
    [DataRow(typeof(TaskSchedulerCom), "TASK_TRIGGER_BOOT", 8L)]
    [DataRow(typeof(TaskSchedulerCom), "TASK_ACTION_EXEC", 0L)]
    [DataRow(typeof(TaskSchedulerCom), "TASK_INSTANCES_QUEUE", 1L)]
    [DataRow(typeof(TaskSchedulerCom), "TASK_COMPATIBILITY_V2_4", 6L)]
    [DataRow(typeof(TaskSchedulerCom), "TASK_CREATE_OR_UPDATE", 6L)]
    [DataRow(typeof(TaskSchedulerCom), "TASK_ENUM_HIDDEN", 0x1L)]
    [DataRow(typeof(TaskSchedulerCom), "TASK_STATE_QUEUED", 2L)]
    [DataRow(typeof(TaskSchedulerCom), "TASK_STATE_READY", 3L)]
    [DataRow(typeof(TaskSchedulerCom), "TASK_STATE_RUNNING", 4L)]
    [DataRow(typeof(TaskSchedulerCom), "HRESULT_ERROR_FILE_NOT_FOUND", -2147024894L)]
    [DataRow(typeof(TaskSchedulerCom), "HRESULT_ERROR_ALREADY_EXISTS", -2147024713L)]
    [DataRow(typeof(NativeMethods), "WM_QUERYENDSESSION", 0x0011L)]
    [DataRow(typeof(NativeMethods), "WM_ENDSESSION", 0x0016L)]
    [DataRow(typeof(NativeMethods), "WM_POWERBROADCAST", 0x0218L)]
    [DataRow(typeof(NativeMethods), "PBT_APMSUSPEND", 0x0004L)]
    [DataRow(typeof(NativeMethods), "PBT_APMRESUMESUSPEND", 0x0007L)]
    [DataRow(typeof(NativeMethods), "PBT_APMRESUMEAUTOMATIC", 0x0012L)]
    [DataRow(typeof(NativeMethods), "WM_SETTINGCHANGE", 0x001AL)]
    [DataRow(typeof(NativeMethods), "WM_MOUSEACTIVATE", 0x0021L)]
    [DataRow(typeof(NativeMethods), "WM_DISPLAYCHANGE", 0x007EL)]
    [DataRow(typeof(NativeMethods), "MA_NOACTIVATE", 3L)]
    [DataRow(typeof(NativeMethods), "WS_EX_NOACTIVATE", 0x08000000L)]
    [DataRow(typeof(NativeMethods), "WS_EX_TOOLWINDOW", 0x00000080L)]
    [DataRow(typeof(NativeMethods), "WS_EX_TOPMOST", 0x00000008L)]
    [DataRow(typeof(NativeMethods), "SWP_NOACTIVATE", 0x0010L)]
    [DataRow(typeof(NativeMethods), "SWP_NOZORDER", 0x0004L)]
    [DataRow(typeof(NativeMethods), "ATTACH_PARENT_PROCESS", 4294967295L)]
    [DataRow(typeof(NativeMethods), "ERROR_ACCESS_DENIED", 5L)]
    [DataRow(typeof(NativeMethods), "STD_OUTPUT_HANDLE", 4294967285L)]
    [DataRow(typeof(NativeMethods), "FILE_TYPE_UNKNOWN", 0x0000L)]
    [DataRow(typeof(NativeMethods), "FILE_TYPE_DISK", 0x0001L)]
    [DataRow(typeof(NativeMethods), "FILE_TYPE_CHAR", 0x0002L)]
    [DataRow(typeof(NativeMethods), "FILE_TYPE_PIPE", 0x0003L)]
    [DataRow(typeof(NativeMethods), "FILE_TYPE_REMOTE", 0x8000L)]
    [DataRow(typeof(Shell), "ABM_GETTASKBARPOS", 5L)]
    [DataRow(typeof(Shell), "SM_CXSMICON", 49L)]
    [DataRow(typeof(Shell), "MDT_EFFECTIVE_DPI", 0L)]
    [DataRow(typeof(Shell), "MONITOR_DEFAULTTONEAREST", 2L)]
    [DataRow(typeof(Shell), "TPM_WORKAREA", 0x10000L)]
    [DataRow(typeof(Shell), "QUNS_ACCEPTS_NOTIFICATIONS", 5L)]
    [DataRow(typeof(Shell), "QUNS_APP", 7L)]
    [DataRow(typeof(Dwm), "DWMWA_WINDOW_CORNER_PREFERENCE", 33L)]
    [DataRow(typeof(Dwm), "DWMWCP_ROUND", 2L)]
    [DataRow(typeof(Dwm), "DWMWA_SYSTEMBACKDROP_TYPE", 38L)]
    [DataRow(typeof(DevQuery), "DevObjectTypeAEP", 5L)]
    [DataRow(typeof(DevQuery), "DevObjectTypeAEPContainer", 6L)]
    [DataRow(typeof(DevQuery), "DevQueryFlagAllProperties", 2L)]
    [DataRow(typeof(DevQuery), "DEVPROP_OPERATOR_EQUALS", 2L)]
    [DataRow(typeof(DevQuery), "DEVPROP_STORE_SYSTEM", 0L)]
    [DataRow(typeof(DevQuery), "DEVPROP_TYPE_BYTE", 3L)]
    [DataRow(typeof(DevQuery), "DEVPROP_MASK_TYPE", 0xFFFL)]
    [DataRow(typeof(FileApis), "MOVEFILE_DELAY_UNTIL_REBOOT", 4L)]
    [DataRow(typeof(FileApis), "FILE_FLAG_OPEN_REPARSE_POINT", 0x00200000L)]
    [DataRow(typeof(FileApis), "FILE_ATTRIBUTE_REPARSE_POINT", 0x400L)]
    public void ConstantMatchesTheSdk(Type owner, string name, long expected)
    {
        Assert.AreEqual(expected, ReadConstant(owner, name), owner.Name + "." + name);
    }

    [TestMethod]
    public void GateDisableFlagsCombineToTwelve()
    {
        long flags = ReadConstant(typeof(CfgMgr32), "CM_DISABLE_PERSIST") | ReadConstant(typeof(CfgMgr32), "CM_DISABLE_UI_NOT_OK");
        Assert.AreEqual(0x0CL, flags);
    }

    [TestMethod]
    public void TopmostHandlesAreMinusOneAndMinusTwo()
    {
        nint topmost = NativeMethods.HWND_TOPMOST;
        nint notTopmost = NativeMethods.HWND_NOTOPMOST;
        Assert.AreEqual(-1L, (long)topmost);
        Assert.AreEqual(-2L, (long)notTopmost);
    }

    [TestMethod]
    public void InvalidHandleValueIsMinusOne()
    {
        nint invalid = NativeMethods.INVALID_HANDLE_VALUE;
        Assert.AreEqual(-1L, (long)invalid);
    }

    [TestMethod]
    public void TaskbarCreatedMessageNameIsTheShellString()
    {
        FieldInfo? field = typeof(Shell).GetField("TaskbarCreatedMessageName", AnyStatic);
        Assert.IsNotNull(field);
        Assert.AreEqual("TaskbarCreated", field.GetRawConstantValue());
    }

    [TestMethod]
    public void RawCodesDecodeThroughNativeCodes()
    {
        Assert.AreEqual("ERROR_NO_SUCH_DEVICE_INTERFACE", NativeCodes.Name(unchecked((int)ReadConstant(typeof(CoreAudio), "ERROR_NO_SUCH_DEVICE_INTERFACE"))));
        Assert.AreEqual("E_INVALIDARG", NativeCodes.Win32(BluetoothApis.E_INVALIDARG));
    }

    // Every CONFIGRET constant decodes to its own name through ConfigRet, and every Win32 constant the
    // Bluetooth declarations use decodes to its own name through Win32, including the values the two
    // families share (CR_INVALID_DEVNODE and ERROR_ACCESS_DENIED are both 0x5).
    [TestMethod]
    public void EveryDeclaredCodeDecodesThroughItsOwnFamily()
    {
        string[] configRets = DeclaredConstantNames(typeof(CfgMgr32), "CR_");
        Assert.IsGreaterThan(10, configRets.Length);
        foreach (string name in configRets)
        {
            string expected = name == "CR_NO_SUCH_DEVINST" ? "CR_NO_SUCH_DEVNODE" : name;
            Assert.AreEqual(expected, NativeCodes.ConfigRet((uint)ReadConstant(typeof(CfgMgr32), name)), name);
        }

        string[] win32 = DeclaredConstantNames(typeof(BluetoothApis), "ERROR_");
        Assert.IsGreaterThan(5, win32.Length);
        foreach (string name in win32)
        {
            Assert.AreEqual(name, NativeCodes.Win32((uint)ReadConstant(typeof(BluetoothApis), name)), name);
        }

        Assert.AreEqual("CR_INVALID_DEVNODE", NativeCodes.ConfigRet(CfgMgr32.CR_INVALID_DEVNODE));
        Assert.AreEqual("ERROR_ACCESS_DENIED", NativeCodes.Win32(BluetoothApis.ERROR_ACCESS_DENIED));
    }

    private static string[] DeclaredConstantNames(Type owner, string prefix) =>
        owner.GetFields(AnyStatic)
             .Where(f => f.IsLiteral && f.Name.StartsWith(prefix, StringComparison.Ordinal) && f.FieldType == typeof(uint))
             .Select(f => f.Name)
             .ToArray();

    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    private static long ReadConstant(Type owner, string name)
    {
        FieldInfo? field = owner.GetField(name, AnyStatic);
        Assert.IsNotNull(field, owner.Name + "." + name + " is not declared.");
        Assert.IsTrue(field.IsLiteral, owner.Name + "." + name + " must be a const.");
        object? value = field.GetRawConstantValue();
        Assert.IsNotNull(value, owner.Name + "." + name);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void AssertKey(string fmtid, uint pid, PROPERTYKEY key)
    {
        Assert.AreEqual(new Guid(fmtid), key.fmtid);
        Assert.AreEqual(pid, key.pid);
    }

    private static void AssertKey(string fmtid, uint pid, DEVPROPKEY key)
    {
        Assert.AreEqual(new Guid(fmtid), key.fmtid);
        Assert.AreEqual(pid, key.pid);
    }
}
