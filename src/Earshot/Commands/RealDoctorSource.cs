using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security;
using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;
using Earshot.Service;
using Microsoft.Win32;

namespace Earshot.Commands;

// Reads this PC for the doctor report and changes nothing. It lists device nodes, services, tasks, processes and registry
// values; it never connects, pages, enables, disables or asks a device anything. A part that cannot be read is recorded with the
// reason and left empty.
internal sealed class RealDoctorSource(Paths paths, ILog log) : IDoctorSource
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Earshot";
    // The Bluetooth device setup class.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/install/system-defined-device-setup-classes-available-to-vendors
    private const string BluetoothClass ="{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string EnumKey = @"SYSTEM\CurrentControlSet\Enum";

    private static readonly string[] BluetoothServiceNames = ["bthserv", "BTAGService", "BthAvctpSvc"];

    public DoctorFacts Read()
    {
        var facts = new DoctorFacts();
        facts.Install = Part(facts, "install", ReadInstall);
        facts.TrayProcesses = Part(facts, "tray", ReadTray);
        facts.Tasks = Part(facts, "tasks", ReadTasks);
        facts.Service = Part(facts, "service", ReadHandBackService);
        facts.Devices = Part(facts, "devices", ReadDevices);
        facts.BtServices = Part(facts, "btServices", ReadBluetoothServices);
        facts.Radio = Part(facts, "radio", ReadRadio);
        List<string> airpodsIds = (facts.Devices ?? []).Where(d => !string.IsNullOrEmpty(AirPodsModels.ModelOf(d.InstanceId))).Select(d => d.InstanceId).ToList();
        facts.Filters = Part(facts, "filters", () => ReadFilters(airpodsIds));
        facts.History = Part(facts, "history", ReadHistory);
        facts.LastReadingSaved = Part(facts, "lastReading", () => (bool?)File.Exists(paths.LastReadingFile));
        facts.Identity = new IdentityFact
        {
            User = Environment.UserName,
            Computer = Environment.MachineName,
            Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        return facts;
    }

    // Runs one read. Each way a read of Windows can fail is named, recorded with its message and logged; any other failure is a
    // bug and is not hidden.
    private T? Part<T>(DoctorFacts facts, string name, Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or InvalidOperationException)
        {
            log.Warn("doctor: " + name + " could not be read: " + ex.Message);
            facts.Unreadable.Add(new UnreadableFact { Part = name, Reason = ex.Message });
            return default;
        }
    }

    private InstallFacts ReadInstall()
    {
        string exe = paths.InstalledExe;
        bool installed = File.Exists(exe);
        string? version = installed ? ReleaseVersion(exe) : null;
        bool entry = false;
        string? shown = null;
        using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(UninstallKey))
        {
            if (key is not null)
            {
                entry = true;
                shown = key.GetValue("DisplayVersion") as string;
            }
        }

        // The shortcut the toast route needs, in the per-user Start menu, and the sign-in entry. Both are only looked for.
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        bool shortcut = programs.Length > 0 && File.Exists(Path.Combine(programs, Widget.Alert.NotificationRegistration.ShortcutFileName));
        bool run;
        using (RegistryKey? runKey = Registry.CurrentUser.OpenSubKey(Tray.CurrentUserStartupRegistry.RunKey))
        {
            run = runKey?.GetValue("Earshot") is not null;
        }

        return new InstallFacts { Folder = paths.InstallFolder, Installed = installed, Version = version, AppsEntry = entry, AppsVersion = shown, StartMenuShortcut = shortcut, RunValue = run };
    }

    // major.minor.patch of a program file's product version, or null when it has none.
    private static string? ReleaseVersion(string exe)
    {
        string? text = FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        int plus = text.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            text = text[..plus];
        }

        string[] bits = text.Split('.');
        return bits.Length is 3 or 4 && bits.All(b => b.Length is > 0 and <= 9 && b.All(char.IsAsciiDigit))
            ? string.Join('.', bits.Take(3))
            : null;
    }

    // Other copies of this program running now; the doctor's own process is not the tray.
    private static int? ReadTray()
    {
        int self = Environment.ProcessId;
        int count = 0;
        foreach (Process p in Process.GetProcessesByName("Earshot"))
        {
            using (p)
            {
                if (p.Id != self)
                {
                    count++;
                }
            }
        }

        return count;
    }

    // The task states as Task Scheduler names them (TASK_STATE).
    // https://learn.microsoft.com/en-us/windows/win32/api/taskschd/ne-taskschd-task_state
    private static string TaskStateName(int state) => state switch
    {
        1 => "Disabled",
        2 => "Queued",
        3 => "Ready",
        4 => "Running",
        _ => "Unknown",
    };

    private List<TaskFact> ReadTasks()
    {
        using var worker = new SystemWorker(log);
        return worker.RunAsync(_ =>
        {
            var rows = new List<TaskFact>();
            var tasks = new ComScheduledTasks();
            foreach (string name in new[] { TaskPlan.GateTaskName, TaskPlan.ProtectTaskName, TaskPlan.BootTaskName })
            {
                int hr = tasks.ReadTask(TaskPlan.TaskPath(name), out TaskReadback? task);
                if (hr == 0 && task is not null)
                {
                    rows.Add(new TaskFact { Name = name, Present = true, State = TaskStateName(task.State) });
                }
                else if (hr == TaskSchedulerCom.HRESULT_ERROR_FILE_NOT_FOUND)
                {
                    rows.Add(new TaskFact { Name = name, Present = false, State = "" });
                }
                else
                {
                    throw new InvalidOperationException("Task " + name + " could not be read: " + NativeCodes.Name(hr) + ".");
                }
            }

            return rows;
        }).GetAwaiter().GetResult();
    }

    // The service states as the service control manager names them (SERVICE_STATUS dwCurrentState).
    // https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_status
    private static string ServiceStateName(uint? state) => state switch
    {
        1 => "Stopped",
        2 => "StartPending",
        3 => "StopPending",
        4 => "Running",
        5 => "ContinuePending",
        6 => "PausePending",
        7 => "Paused",
        _ => "",
    };

    private static ServiceQuery QueryService(string name)
    {
        ServiceQuery query = new WindowsServiceControl().Query(name);
        if (query.Presence == ServicePresence.Unknown)
        {
            throw new InvalidOperationException("Service " + name + " could not be queried: " + string.Join(" ", query.Steps.Where(s => !s.Ok).Select(s => s.Detail)));
        }

        return query;
    }

    private static ServiceFact ReadHandBackService()
    {
        ServiceQuery query = QueryService(ServicePlan.ServiceName);
        return new ServiceFact { Present = query.Presence == ServicePresence.Present, State = ServiceStateName(query.State) };
    }

    private static List<BtServiceFact> ReadBluetoothServices()
    {
        var rows = new List<BtServiceFact>();
        foreach (string name in BluetoothServiceNames)
        {
            ServiceQuery query = QueryService(name);
            rows.Add(new BtServiceFact { Name = name, State = query.Presence == ServicePresence.Present ? ServiceStateName(query.State) : "" });
        }

        return rows;
    }

    private static bool IsBluetoothNode(string id) =>
        id.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("BTHLE\\", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("BTH\\", StringComparison.OrdinalIgnoreCase);

    // Every Bluetooth node on Apple's vendor id, present or not. The node's name and container are read only so the report can
    // take them out of text.
    private static List<DeviceFact> ReadDevices()
    {
        var reader = new CfgMgr32NodeReader();
        uint listed = reader.ListDeviceIds(out string[] ids);
        if (listed != CfgMgr32.CR_SUCCESS)
        {
            throw new InvalidOperationException("The list of device nodes could not be read: " + NativeCodes.ConfigRet(listed) + ".");
        }

        var rows = new List<DeviceFact>();
        foreach (string id in ids)
        {
            if (!IsBluetoothNode(id) || AirPodsModels.ModelOf(id) is null)
            {
                continue;
            }

            bool present = reader.Locate(id, includeNonPresent: false, out uint devInst) == CfgMgr32.CR_SUCCESS;
            if (!present && reader.Locate(id, includeNonPresent: true, out devInst) != CfgMgr32.CR_SUCCESS)
            {
                continue;
            }

            int? problem = null;
            if (present && reader.GetStatus(devInst, out _, out uint problemCode) == CfgMgr32.CR_SUCCESS)
            {
                problem = (int)problemCode;
            }

            int? flags = reader.GetConfigFlags(devInst, out uint configFlags) == CfgMgr32.CR_SUCCESS ? (int)configFlags : null;
            string? name = reader.GetName(devInst, out string? friendly) == CfgMgr32.CR_SUCCESS ? friendly : null;
            Guid? container = reader.GetContainerId(devInst, out Guid containerId) == CfgMgr32.CR_SUCCESS ? containerId : null;
            string status = !present ? "Unknown" : problem is null or 0 ? "OK" : "Error";
            rows.Add(new DeviceFact
            {
                InstanceId = id,
                Name = name,
                Container = container?.ToString("D", CultureInfo.InvariantCulture),
                Status = status,
                Present = present,
                Problem = problem,
                ConfigFlags = flags,
            });
        }

        return rows;
    }

    // The Bluetooth radio is the present node of the Bluetooth class on the USB, PCI or ACPI bus; its driver's version is on the
    // driver key the node names.
    private static RadioFact ReadRadio()
    {
        var reader = new CfgMgr32NodeReader();
        foreach (string bus in new[] { "USB", "PCI", "ACPI" })
        {
            using RegistryKey? busKey = Registry.LocalMachine.OpenSubKey(EnumKey + "\\" + bus);
            if (busKey is null)
            {
                continue;
            }

            foreach (string device in busKey.GetSubKeyNames())
            {
                using RegistryKey? deviceKey = busKey.OpenSubKey(device);
                if (deviceKey is null)
                {
                    continue;
                }

                foreach (string instance in deviceKey.GetSubKeyNames())
                {
                    using RegistryKey? node = deviceKey.OpenSubKey(instance);
                    if (node is null || !string.Equals(node.GetValue("ClassGUID") as string, BluetoothClass, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string id = bus + "\\" + device + "\\" + instance;
                    if (reader.Locate(id, includeNonPresent: false, out _) != CfgMgr32.CR_SUCCESS)
                    {
                        continue;
                    }

                    string? version = null;
                    if (node.GetValue("Driver") is string driver)
                    {
                        using RegistryKey? driverKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driver);
                        version = driverKey?.GetValue("DriverVersion") as string;
                    }

                    return new RadioFact { Found = true, DriverVersion = version };
                }
            }
        }

        return new RadioFact { Found = false, DriverVersion = null };
    }

    // Lower and upper filter drivers on the AirPods' nodes, on the RFCOMM nodes and on the Bluetooth class, by name.
    private static List<FilterFact> ReadFilters(List<string> airpodsIds)
    {
        var rows = new List<FilterFact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Take(RegistryKey? key, string on)
        {
            if (key is null)
            {
                return;
            }

            foreach (string value in new[] { "LowerFilters", "UpperFilters" })
            {
                if (key.GetValue(value) is string[] names)
                {
                    foreach (string name in names.Where(n => !string.IsNullOrEmpty(n)))
                    {
                        if (seen.Add(on + "|" + name))
                        {
                            rows.Add(new FilterFact { Name = name, On = on });
                        }
                    }
                }
            }
        }

        foreach (string id in airpodsIds)
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(EnumKey + "\\" + id);
            Take(key, "airpods");
        }

        using (RegistryKey? rfcomm = Registry.LocalMachine.OpenSubKey(EnumKey + @"\BTH\MS_RFCOMM"))
        {
            if (rfcomm is not null)
            {
                foreach (string child in rfcomm.GetSubKeyNames())
                {
                    using RegistryKey? key = rfcomm.OpenSubKey(child);
                    Take(key, "rfcomm");
                }
            }
        }

        using (RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + BluetoothClass))
        {
            Take(classKey, "bluetooth class");
        }

        return rows;
    }

    private HistoryFact ReadHistory()
    {
        string path = Path.Combine(paths.LogFolder, "connect-history.jsonl");
        if (!File.Exists(path))
        {
            return new HistoryFact { Exists = false, Lines = 0, Last = [] };
        }

        List<string> all = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
        return new HistoryFact { Exists = true, Lines = all.Count, Last = all.TakeLast(5).ToList() };
    }
}
