using System.Globalization;
using System.Runtime.InteropServices;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KSPROPERTY = Earshot.Interop.KSIDENTIFIER;

namespace Earshot.Tests.Interop;

// Read-only smoke tests of the interop declarations against the real audio and Bluetooth stacks.
//
// They only enumerate and read: Core Audio endpoints and property stores, the topology walk to IKsControl
// with KSPROPSETID_Pin Get requests, CfgMgr32 lists, locates, status and properties, and the Bluetooth
// radio, device and installed-service enumerations without an inquiry. They never send a KSPROPSETID_BtAudio
// request, never set a property, never disable or enable a devnode and never change a service state.
// With no matching hardware present they are inconclusive, not failed.
[TestClass]
[TestCategory("ReadOnlyHardware")]
public sealed class ReadOnlyHardwareSmokeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void CoreAudioEnumeratesEndpointsAndReadsProperties()
    {
        RunAndReport(report =>
        {
            int createHr = CoreAudio.TryCreateEnumerator(out IMMDeviceEnumerator? enumerator);
            Assert.AreEqual(0, createHr, "CoCreateInstance(MMDeviceEnumerator) " + NativeCodes.Name(createHr));
            Assert.IsNotNull(enumerator);
            IMMDeviceCollection? collection = null;
            try
            {
                int hr = enumerator.EnumAudioEndpoints(CoreAudio.eAll, CoreAudio.DEVICE_STATEMASK_ALL, out collection);
                Assert.AreEqual(0, hr, "EnumAudioEndpoints " + NativeCodes.Name(hr));
                Assert.IsNotNull(collection);
                hr = collection.GetCount(out uint count);
                Assert.AreEqual(0, hr, "GetCount " + NativeCodes.Name(hr));
                report.Add("Endpoints (eAll, DEVICE_STATEMASK_ALL): " + count);

                var states = new Dictionary<string, int>(StringComparer.Ordinal);
                int activeEndpoints = 0;
                int activeNamesRead = 0;
                for (uint index = 0; index < count; index++)
                {
                    hr = collection.Item(index, out IMMDevice? device);
                    if (hr < 0 || device is null)
                    {
                        report.Add("  [" + index + "] Item " + NativeCodes.Name(hr));
                        continue;
                    }

                    try
                    {
                        EndpointFacts facts = ReadEndpoint(device);
                        states[facts.State] = states.GetValueOrDefault(facts.State) + 1;
                        if (facts.StateBits == CoreAudio.DEVICE_STATE_ACTIVE)
                        {
                            activeEndpoints++;
                            if (facts.Name is not null)
                            {
                                activeNamesRead++;
                            }
                        }

                        report.Add("  [" + index + "] " + facts);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }

                report.Add("By state: " + string.Join(", ", states.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + " " + pair.Value)));

                // Registration round trip. It subscribes and unsubscribes; no device changes.
                var client = new SilentNotificationClient();
                int register = enumerator.RegisterEndpointNotificationCallback(client);
                int unregister = enumerator.UnregisterEndpointNotificationCallback(client);
                int unregisterAgain = enumerator.UnregisterEndpointNotificationCallback(client);
                GC.KeepAlive(client);
                report.Add("Register " + NativeCodes.Name(register) + ", Unregister " + NativeCodes.Name(unregister) +
                           ", second Unregister " + NativeCodes.Name(unregisterAgain));
                Assert.AreEqual(0, register, "RegisterEndpointNotificationCallback");
                Assert.AreEqual(0, unregister, "UnregisterEndpointNotificationCallback");
                Assert.AreEqual(CoreAudio.E_NOTFOUND, unregisterAgain, "A second Unregister of the same client");

                if (count == 0)
                {
                    Assert.Inconclusive("No audio endpoints on this machine.");
                }

                if (activeEndpoints > 0)
                {
                    Assert.IsGreaterThan(0, activeNamesRead, "No ACTIVE endpoint returned a VT_LPWSTR friendly name.");
                }
            }
            finally
            {
                if (collection is not null)
                {
                    Marshal.ReleaseComObject(collection);
                }

                Marshal.ReleaseComObject(enumerator);
            }
        });
    }

    [TestMethod]
    public void TopologyWalkReachesIKsControlAndReadsPinProperties()
    {
        RunAndReport(report =>
        {
            int createHr = CoreAudio.TryCreateEnumerator(out IMMDeviceEnumerator? enumerator);
            Assert.AreEqual(0, createHr, "CoCreateInstance(MMDeviceEnumerator) " + NativeCodes.Name(createHr));
            Assert.IsNotNull(enumerator);
            try
            {
                var adapters = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int jackReads = 0;
                foreach (EndpointHandle endpoint in Endpoints(enumerator, report))
                {
                    try
                    {
                        WalkEndpoint(endpoint, adapters, report, ref jackReads);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(endpoint.Device);
                    }
                }

                report.Add("Distinct adapter (KS filter) ids: " + adapters.Count);
                int filtersReached = 0;
                int pinCountsRead = 0;
                int dataFlowsRead = 0;
                foreach ((string adapterId, string firstEndpoint) in adapters)
                {
                    ReadFilter(enumerator, adapterId, firstEndpoint, report, ref filtersReached, ref pinCountsRead, ref dataFlowsRead);
                }

                report.Add("Filters reached: " + filtersReached + ", PIN_CTYPES read: " + pinCountsRead +
                           ", PIN_DATAFLOW read: " + dataFlowsRead + ", jack descriptions read: " + jackReads);

                if (filtersReached == 0)
                {
                    Assert.Inconclusive("No KS filter was reachable through the topology walk.");
                }

                Assert.IsGreaterThan(0, pinCountsRead, "No reachable filter answered KSPROPERTY_PIN_CTYPES.");
                Assert.IsGreaterThan(0, dataFlowsRead, "No pin answered KSPROPERTY_PIN_DATAFLOW through a KSP_PIN descriptor.");
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        });
    }

    [TestMethod]
    public void CfgMgr32EnumeratesBluetoothNodesReadOnly()
    {
        RunAndReport(report =>
        {
            uint cr = CfgMgr32.GetDeviceIdList(null, CfgMgr32.CM_GETIDLIST_FILTER_NONE, out string[] all);
            Assert.AreEqual(CfgMgr32.CR_SUCCESS, cr, "CM_Get_Device_ID_ListW(FILTER_NONE) " + NativeCodes.ConfigRet(cr));
            uint presentCr = CfgMgr32.GetDeviceIdList(null, CfgMgr32.CM_GETIDLIST_FILTER_PRESENT, out string[] present);
            uint enumCr = CfgMgr32.GetDeviceIdList("BTHENUM", CfgMgr32.CM_GETIDLIST_FILTER_ENUMERATOR, out string[] bthenum);
            report.Add("Devnodes: " + all.Length + " (FILTER_NONE), " + present.Length + " present (" + NativeCodes.ConfigRet(presentCr) +
                       "), " + bthenum.Length + " BTHENUM (" + NativeCodes.ConfigRet(enumCr) + ")");

            string[] bluetooth = all.Where(IsBluetoothInstance).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
            report.Add("Bluetooth devnodes (BTHENUM, BTHHFENUM, BTH, BTHLE, BTHLEDEVICE): " + bluetooth.Length);
            if (bluetooth.Length == 0)
            {
                Assert.Inconclusive("No Bluetooth devnodes on this machine.");
            }

            int located = 0;
            int namesRead = 0;
            int statusRead = 0;
            foreach (string id in bluetooth)
            {
                uint locate = CfgMgr32.LocateDevNode(id, CfgMgr32.CM_LOCATE_DEVNODE_PHANTOM, out uint devInst);
                if (locate != CfgMgr32.CR_SUCCESS)
                {
                    report.Add("  " + id + " | locate PHANTOM " + NativeCodes.ConfigRet(locate));
                    continue;
                }

                located++;
                string name = ReadNodeString(devInst, CfgMgr32.DEVPKEY_NAME, ref namesRead);
                string container = ReadNodeGuid(devInst, CfgMgr32.DEVPKEY_Device_ContainerId);
                string isPresent = ReadNodeBoolean(devInst, CfgMgr32.DEVPKEY_Device_IsPresent);
                string configFlags = ReadNodeUInt32(devInst, CfgMgr32.DEVPKEY_Device_ConfigFlags);

                string status;
                uint normal = CfgMgr32.LocateDevNode(id, CfgMgr32.CM_LOCATE_DEVNODE_NORMAL, out uint liveInst);
                if (normal == CfgMgr32.CR_SUCCESS)
                {
                    uint statusCr = CfgMgr32.CM_Get_DevNode_Status(out uint bits, out uint problem, liveInst, 0);
                    if (statusCr == CfgMgr32.CR_SUCCESS)
                    {
                        statusRead++;
                        bool hasProblem = (bits & CfgMgr32.DN_HAS_PROBLEM) != 0;
                        status = "status 0x" + bits.ToString("X8", CultureInfo.InvariantCulture) +
                                 (hasProblem ? " problem " + problem : " no problem") +
                                 ((bits & CfgMgr32.DN_DEVICE_DISCONNECTED) != 0 ? " disconnected-bit" : "");
                    }
                    else
                    {
                        status = "status " + NativeCodes.ConfigRet(statusCr);
                    }
                }
                else
                {
                    status = "locate NORMAL " + NativeCodes.ConfigRet(normal);
                }

                report.Add("  " + id + " | " + name + " | container " + container + " | present " + isPresent +
                           " | " + status + " | ConfigFlags " + configFlags);
            }

            report.Add("Located " + located + ", DEVPKEY_NAME read " + namesRead + ", status read " + statusRead);
            Assert.IsGreaterThan(0, located, "No listed Bluetooth devnode could be located with CM_LOCATE_DEVNODE_PHANTOM.");
            Assert.IsGreaterThan(0, namesRead, "No Bluetooth devnode returned DEVPKEY_NAME.");
        });
    }

    [TestMethod]
    public void BluetoothEnumeratesRadiosDevicesAndInstalledServices()
    {
        RunAndReport(report =>
        {
            uint radioError = BluetoothApis.CountRadios(out int radios, out uint radioCloseError);
            report.Add("Radios: " + radios + " (" + NativeCodes.Win32(radioError) + ", close " + NativeCodes.Win32(radioCloseError) + ")");
            Assert.AreEqual(BluetoothApis.ERROR_SUCCESS, radioError, "BluetoothFindFirstRadio/NextRadio");
            Assert.AreEqual(BluetoothApis.ERROR_SUCCESS, radioCloseError, "Closing radio handles");
            if (radios == 0)
            {
                Assert.Inconclusive("No Bluetooth radio on this machine.");
            }

            uint findError = BluetoothApis.FindPairedDevices(out List<BLUETOOTH_DEVICE_INFO> devices, out uint findCloseError);
            report.Add("Paired Classic devices (no inquiry): " + devices.Count + " (" + NativeCodes.Win32(findError) +
                       ", close " + NativeCodes.Win32(findCloseError) + ")");
            Assert.AreEqual(BluetoothApis.ERROR_SUCCESS, findError, "BluetoothFindFirstDevice/NextDevice");
            Assert.AreEqual(BluetoothApis.ERROR_SUCCESS, findCloseError, "BluetoothFindDeviceClose");
            if (devices.Count == 0)
            {
                Assert.Inconclusive("No paired Classic Bluetooth device on this machine.");
            }

            int completeServiceLists = 0;
            foreach (BLUETOOTH_DEVICE_INFO device in devices)
            {
                Assert.AreEqual(560u, device.dwSize, "dwSize after enumeration");
                uint rc = BluetoothApis.GetInstalledServices(device, out Guid[] services);
                if (rc == BluetoothApis.ERROR_SUCCESS)
                {
                    completeServiceLists++;
                }

                report.Add("  " + BluetoothApis.FormatAddress12(device.Address) + " '" + BluetoothApis.GetName(device) + "'" +
                           " CoD 0x" + device.ulClassofDevice.ToString("X6", CultureInfo.InvariantCulture) +
                           " connected " + (device.fConnected != 0) + " (raw " + device.fConnected + ")" +
                           " remembered " + (device.fRemembered != 0) + " authenticated " + (device.fAuthenticated != 0));
                report.Add("    installed services (" + NativeCodes.Win32(rc) + "): " + services.Length + " " +
                           string.Join(" ", services.Select(DescribeService)));
            }

            Assert.IsGreaterThan(0, completeServiceLists, "No device returned a complete installed-service list.");
        });
    }

    private void RunAndReport(Action<List<string>> body)
    {
        var report = new List<string>();
        try
        {
            MtaThread.Run(() => body(report), Timeout);
        }
        finally
        {
            foreach (string line in report)
            {
                TestContext.WriteLine(line);
            }
        }
    }

    private static bool IsBluetoothInstance(string id) =>
        id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("BTHHFENUM\\", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("BTH\\", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("BTHLEDEVICE\\", StringComparison.OrdinalIgnoreCase);

    private static string DescribeService(Guid service)
    {
        string text = "{" + service.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + "}";
        if (service == BluetoothApis.HandsfreeServiceClass)
        {
            return text + "=Handsfree";
        }

        if (service == BluetoothApis.HeadsetServiceClass)
        {
            return text + "=Headset";
        }

        return service == BluetoothApis.AudioSinkServiceClass ? text + "=A2DP-sink" : text;
    }

    private static string ReadNodeString(uint devInst, DEVPROPKEY key, ref int reads)
    {
        uint cr = CfgMgr32.GetDevNodeProperty(devInst, key, out uint type, out byte[] data);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            return NativeCodes.ConfigRet(cr);
        }

        if (CfgMgr32.TryDecodeString(type, data, out string? value))
        {
            reads++;
            return "'" + value + "'";
        }

        return "type 0x" + type.ToString("X", CultureInfo.InvariantCulture);
    }

    private static string ReadNodeGuid(uint devInst, DEVPROPKEY key)
    {
        uint cr = CfgMgr32.GetDevNodeProperty(devInst, key, out uint type, out byte[] data);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            return NativeCodes.ConfigRet(cr);
        }

        return CfgMgr32.TryDecodeGuid(type, data, out Guid value)
            ? "{" + value.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + "}"
            : "type 0x" + type.ToString("X", CultureInfo.InvariantCulture);
    }

    private static string ReadNodeBoolean(uint devInst, DEVPROPKEY key)
    {
        uint cr = CfgMgr32.GetDevNodeProperty(devInst, key, out uint type, out byte[] data);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            return NativeCodes.ConfigRet(cr);
        }

        return CfgMgr32.TryDecodeBoolean(type, data, out bool value)
            ? value.ToString()
            : "type 0x" + type.ToString("X", CultureInfo.InvariantCulture);
    }

    private static string ReadNodeUInt32(uint devInst, DEVPROPKEY key)
    {
        uint cr = CfgMgr32.GetDevNodeProperty(devInst, key, out uint type, out byte[] data);
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            return NativeCodes.ConfigRet(cr);
        }

        return CfgMgr32.TryDecodeUInt32(type, data, out uint value)
            ? "0x" + value.ToString("X", CultureInfo.InvariantCulture)
            : "type 0x" + type.ToString("X", CultureInfo.InvariantCulture);
    }

    private static EndpointFacts ReadEndpoint(IMMDevice device)
    {
        int idHr = device.GetId(out nint idPointer);
        string id = CoreAudio.TakeCoTaskString(idPointer) ?? "(GetId " + NativeCodes.Name(idHr) + ")";
        int stateHr = device.GetState(out uint state);
        string flow = "?";
        if (device is IMMEndpoint endpoint)
        {
            int flowHr = endpoint.GetDataFlow(out int dataFlow);
            flow = flowHr < 0 ? "flow " + NativeCodes.Name(flowHr) : dataFlow == CoreAudio.eRender ? "RENDER" : "CAPTURE";
        }

        string? name = null;
        string nameText;
        string containerText;
        int storeHr = device.OpenPropertyStore(CoreAudio.STGM_READ, out IPropertyStore? store);
        if (storeHr < 0 || store is null)
        {
            nameText = "(OpenPropertyStore " + NativeCodes.Name(storeHr) + ")";
            containerText = nameText;
        }
        else
        {
            try
            {
                PropertyRead<string> friendly = PropVariantInterop.ReadString(store, CoreAudio.PKEY_Device_FriendlyName);
                PropertyRead<Guid> container = PropVariantInterop.ReadGuid(store, CoreAudio.PKEY_Device_ContainerId);
                Assert.AreEqual(0, friendly.ClearHr, "PropVariantClear after FriendlyName");
                Assert.AreEqual(0, container.ClearHr, "PropVariantClear after ContainerId");
                name = friendly.Value;
                nameText = friendly.HasValue
                    ? "'" + friendly.Value + "'"
                    : "(name " + NativeCodes.Name(friendly.Hr) + " vt " + friendly.VarType + ")";
                containerText = container.HasValue
                    ? "{" + container.Value.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + "}"
                    : "(container " + NativeCodes.Name(container.Hr) + " vt " + container.VarType + ")";
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        string stateText = stateHr < 0 ? "state " + NativeCodes.Name(stateHr) : StateName(state);
        return new EndpointFacts(id, flow, stateHr < 0 ? 0 : state, stateText, name, nameText, containerText);
    }

    private static string StateName(uint state) => state switch
    {
        CoreAudio.DEVICE_STATE_ACTIVE => "ACTIVE",
        CoreAudio.DEVICE_STATE_DISABLED => "DISABLED",
        CoreAudio.DEVICE_STATE_NOTPRESENT => "NOTPRESENT",
        CoreAudio.DEVICE_STATE_UNPLUGGED => "UNPLUGGED",
        _ => "0x" + state.ToString("X", CultureInfo.InvariantCulture),
    };

    private static List<EndpointHandle> Endpoints(IMMDeviceEnumerator enumerator, List<string> report)
    {
        var endpoints = new List<EndpointHandle>();
        int hr = enumerator.EnumAudioEndpoints(CoreAudio.eAll, CoreAudio.DEVICE_STATEMASK_ALL, out IMMDeviceCollection? collection);
        Assert.AreEqual(0, hr, "EnumAudioEndpoints " + NativeCodes.Name(hr));
        Assert.IsNotNull(collection);
        try
        {
            hr = collection.GetCount(out uint count);
            Assert.AreEqual(0, hr, "GetCount " + NativeCodes.Name(hr));
            for (uint index = 0; index < count; index++)
            {
                int itemHr = collection.Item(index, out IMMDevice? device);
                if (itemHr >= 0 && device is not null)
                {
                    endpoints.Add(new EndpointHandle(index, device));
                }
                else
                {
                    report.Add("  [" + index + "] Item " + NativeCodes.Name(itemHr));
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }

        return endpoints;
    }

    // Endpoint -> IDeviceTopology -> connector 0 -> adapter id; for ACTIVE endpoints also the adapter-side
    // connector's IPart -> IKsJackDescription. Reads only; ConnectTo and Disconnect are never called.
    private static void WalkEndpoint(EndpointHandle endpoint, SortedDictionary<string, string> adapters, List<string> report, ref int jackReads)
    {
        int stateHr = endpoint.Device.GetState(out uint state);
        string label = "[" + endpoint.Index + "] " + (stateHr < 0 ? NativeCodes.Name(stateHr) : StateName(state));
        int hr = CoreAudio.Activate(endpoint.Device, out IDeviceTopology? topology);
        if (hr < 0 || topology is null)
        {
            report.Add("  " + label + " Activate(IDeviceTopology) " + NativeCodes.Name(hr));
            return;
        }

        IConnector? connector = null;
        IConnector? other = null;
        try
        {
            int countHr = topology.GetConnectorCount(out uint connectors);
            hr = topology.GetConnector(0, out connector);
            if (hr < 0 || connector is null)
            {
                report.Add("  " + label + " connectors " + connectors + " (" + NativeCodes.Name(countHr) + "), GetConnector(0) " + NativeCodes.Name(hr));
                return;
            }

            int typeHr = connector.GetConnectorType(out int connectorType);
            int flowHr = connector.GetDataFlow(out int dataFlow);
            int idHr = connector.GetDeviceIdConnectedTo(out nint adapterPointer);
            string? adapterId = CoreAudio.TakeCoTaskString(adapterPointer);
            string line = "  " + label + " connectors " + connectors + " type " + (typeHr < 0 ? NativeCodes.Name(typeHr) : connectorType) +
                          " flow " + (flowHr < 0 ? NativeCodes.Name(flowHr) : dataFlow) + " GetDeviceIdConnectedTo " + NativeCodes.Name(idHr);
            if (adapterId is not null)
            {
                adapters.TryAdd(adapterId, label);
                line += " -> " + adapterId;
            }

            if (stateHr >= 0 && state == CoreAudio.DEVICE_STATE_ACTIVE)
            {
                line += " | " + ReadJack(connector, ref other, ref jackReads);
            }

            report.Add(line);
        }
        finally
        {
            if (other is not null)
            {
                Marshal.ReleaseComObject(other);
            }

            if (connector is not null)
            {
                Marshal.ReleaseComObject(connector);
            }

            Marshal.ReleaseComObject(topology);
        }
    }

    private static string ReadJack(IConnector connector, ref IConnector? other, ref int jackReads)
    {
        int hr = connector.GetConnectedTo(out other);
        if (hr < 0 || other is null)
        {
            return "GetConnectedTo " + NativeCodes.Name(hr);
        }

        if (other is not IPart part)
        {
            return "adapter connector has no IPart";
        }

        hr = DeviceTopology.Activate(part, out IKsJackDescription? jack);
        if (hr < 0 || jack is null)
        {
            return "IPart.Activate(IKsJackDescription) " + NativeCodes.Name(hr);
        }

        try
        {
            int countHr = jack.GetJackCount(out uint jacks);
            if (countHr < 0 || jacks == 0)
            {
                return "jacks " + jacks + " (" + NativeCodes.Name(countHr) + ")";
            }

            int descriptionHr = jack.GetJackDescription(0, out KSJACK_DESCRIPTION description);
            if (descriptionHr < 0)
            {
                return "GetJackDescription " + NativeCodes.Name(descriptionHr);
            }

            jackReads++;
            return "jacks " + jacks + " IsConnected " + (description.IsConnected != 0);
        }
        finally
        {
            Marshal.ReleaseComObject(jack);
        }
    }

    // GetDevice(adapterId) -> GetState -> ContainerId -> Activate(IKsControl) -> KSPROPSETID_Pin Get requests.
    private static void ReadFilter(
        IMMDeviceEnumerator enumerator, string adapterId, string firstEndpoint, List<string> report,
        ref int filtersReached, ref int pinCountsRead, ref int dataFlowsRead)
    {
        string bluetooth = adapterId.Contains("bthenum", StringComparison.OrdinalIgnoreCase) ||
                           adapterId.Contains("bthhfenum", StringComparison.OrdinalIgnoreCase) ? " [Bluetooth]" : "";
        string line = "  filter" + bluetooth + " (from endpoint " + firstEndpoint + ") " + adapterId;
        int hr = enumerator.GetDevice(adapterId, out IMMDevice? adapter);
        if (hr < 0 || adapter is null)
        {
            report.Add(line + " | GetDevice " + NativeCodes.Name(hr));
            return;
        }

        IKsControl? control = null;
        try
        {
            int stateHr = adapter.GetState(out uint state);
            line += " | state " + (stateHr < 0 ? NativeCodes.Name(stateHr) : StateName(state));
            int storeHr = adapter.OpenPropertyStore(CoreAudio.STGM_READ, out IPropertyStore? store);
            if (storeHr >= 0 && store is not null)
            {
                try
                {
                    PropertyRead<Guid> container = PropVariantInterop.ReadGuid(store, CoreAudio.PKEY_Device_ContainerId);
                    line += " | container " + (container.HasValue
                        ? "{" + container.Value.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + "}"
                        : NativeCodes.Name(container.Hr));
                }
                finally
                {
                    Marshal.ReleaseComObject(store);
                }
            }
            else
            {
                line += " | OpenPropertyStore " + NativeCodes.Name(storeHr);
            }

            hr = CoreAudio.Activate(adapter, out control);
            if (hr < 0 || control is null)
            {
                report.Add(line + " | Activate(IKsControl) " + NativeCodes.Name(hr));
                return;
            }

            filtersReached++;
            var ctypes = new KSPROPERTY(KsControl.KSPROPSETID_Pin, KsControl.KSPROPERTY_PIN_CTYPES, KsControl.KSPROPERTY_TYPE_GET);
            hr = GetPinProperty(control, ref ctypes, (uint)Marshal.SizeOf<KSPROPERTY>(), out uint pinCount, out uint bytes);
            line += " | PIN_CTYPES " + NativeCodes.Name(hr) + " bytes " + bytes;
            if (hr < 0 || bytes < 4)
            {
                report.Add(line);
                return;
            }

            pinCountsRead++;
            line += " pins " + pinCount + " | dataflow";
            for (uint pinId = 0; pinId < Math.Min(pinCount, 8u); pinId++)
            {
                var pin = new KSP_PIN(new KSPROPERTY(KsControl.KSPROPSETID_Pin, KsControl.KSPROPERTY_PIN_DATAFLOW, KsControl.KSPROPERTY_TYPE_GET), pinId);
                int flowHr = GetPinProperty(control, ref KsControl.AsProperty(ref pin), (uint)Marshal.SizeOf<KSP_PIN>(), out uint flow, out uint flowBytes);
                if (flowHr >= 0 && flowBytes >= 4 && flow is 1 or 2)
                {
                    dataFlowsRead++;
                    line += " " + pinId + ":" + (flow == KsControl.KSPIN_DATAFLOW_IN ? "in" : "out");
                }
                else
                {
                    line += " " + pinId + ":" + NativeCodes.Name(flowHr) + "/" + flow;
                }
            }

            report.Add(line);
        }
        finally
        {
            if (control is not null)
            {
                Marshal.ReleaseComObject(control);
            }

            Marshal.ReleaseComObject(adapter);
        }
    }

    // The only KS request this class makes. It refuses anything but a KSPROPSETID_Pin Get, so a test bug can
    // never put a Bluetooth audio request, or any Set request, in front of a driver.
    private static int GetPinProperty(IKsControl control, ref KSPROPERTY property, uint propertyLength, out uint value, out uint bytesReturned)
    {
        if (property.Set != KsControl.KSPROPSETID_Pin || property.Flags != KsControl.KSPROPERTY_TYPE_GET)
        {
            throw new AssertFailedException("Refused a KS request that is not a KSPROPSETID_Pin Get.");
        }

        var buffer = new byte[4];
        GCHandle pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            int hr = control.KsProperty(ref property, propertyLength, pinned.AddrOfPinnedObject(), (uint)buffer.Length, out bytesReturned);
            value = BitConverter.ToUInt32(buffer, 0);
            return hr;
        }
        finally
        {
            pinned.Free();
        }
    }

    private sealed record EndpointHandle(uint Index, IMMDevice Device);

    private sealed record EndpointFacts(string Id, string Flow, uint StateBits, string State, string? Name, string NameText, string ContainerText)
    {
        public override string ToString() => Flow + " " + State + " " + NameText + " container " + ContainerText + " id " + Id;
    }

    // Returns S_OK and does nothing; it only proves the managed client can be registered and unregistered.
    private sealed class SilentNotificationClient : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string pwstrDeviceId, uint dwNewState) => 0;

        public int OnDeviceAdded(string pwstrDeviceId) => 0;

        public int OnDeviceRemoved(string pwstrDeviceId) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, string? pwstrDefaultDeviceId) => 0;

        public int OnPropertyValueChanged(string pwstrDeviceId, PROPERTYKEY key) => 0;
    }
}
