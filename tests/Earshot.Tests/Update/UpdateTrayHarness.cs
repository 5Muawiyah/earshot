using System.Windows.Forms;
using Earshot.App;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Hotkeys;
using Earshot.Infra;
using Earshot.Tests.Hotkeys;
using Earshot.Tests.Integration.Coordinator;
using Earshot.Tests.Phase1;
using Earshot.Tray;
using Earshot.Tests.Widget;
using Earshot.Update;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Earshot.Tests.Phase1.Phase1Fixtures;
using ManualTimeProvider = Earshot.Tests.Phase3.ManualTimeProvider;

namespace Earshot.Tests.Update;

// The real TrayContext on an STA thread, as TrayHarness builds it, with the update source and launcher faked so
// nothing reaches GitHub or starts a program. It keeps its own construction rather than editing the shared harness.
// Nothing here reaches a device, Bluetooth, Task Scheduler or HKCU, and no icon is added to the notification area.
internal sealed class UpdateTrayHarness : IDisposable
{
    // The installed program the tray hands over to. The tray under test claims to run from it (ExePath is the same
    // path), which is what an installed tray looks like; the file exists only as far as the fake FileExists says.
    public const string InstalledExe = @"C:\Program Files\Earshot\Earshot.exe";

    // A copy of the release unzipped in a download folder: what the tray runs from when it is not the installed one.
    public const string OtherExe = @"C:\Users\Someone\Downloads\Earshot-1.2.0\Earshot\Earshot.exe";

    private readonly TempFolder _folder = new();

    public UpdateTrayHarness(
        bool safeMode = false,
        Action<EarshotSettings>? settings = null,
        Action<FakeUpdateSource>? source = null,
        bool pinned = true,
        bool installedCopy = true,
        bool otherCopy = false,
        bool installProgramMissing = false,
        Action<FakeStartupRegistry>? startup = null,
        Func<string, Earshot.Boot.Gate.InstalledFilesReport>? checkFiles = null,
        Version? installedVersion = null,
        Func<string, Earshot.Boot.InstalledFile>? readInstalledFile = null,
        Func<string, IEnumerable<string>>? listFolder = null,
        Action<FakeBlockController>? block = null,
        string? installFolderSddl = null,
        Func<IUpdateSource>? sourceFactory = null,
        TimeProvider? time = null,
        UpdateOutcomeSource? outcomeSource = null,
        DeviceSnapshot? snapshot = null,
        TimeSpan? elevatedExitWait = null,
        bool elevated = false,
        TimeSpan? exitNoticeTime = null)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
        Ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(Ui);
        source?.Invoke(Source);
        block?.Invoke(Block);

        Monitor.Current = snapshot ?? NoDevice();
        Startup.Run[StartupRegistration.ValueName] = StartupRegistration.CommandFor(TrayHarness.ExePath);
        startup?.Invoke(Startup);
        SettingsPath = _folder.File("settings.json");
        Settings = new JsonSettingsStore(SettingsPath, Log);
        Settings.Update(s =>
        {
            if (pinned)
            {
                s.PinnedContainerId = AirPodsContainer;
                s.PinnedAddress = AirPodsAddress;
            }

            s.Widget = s.Widget with { Enabled = false, ShowOnTaskbar = false, LowBatteryAlert = false, CaseOpenCard = false, AutoPause = false };
            settings?.Invoke(s);
        });

        Registry = new ServiceRegistry(Log, Settings, action => Ui.Post(static state => ((Action)state!)(), action), safeMode)
        {
            Monitor = Monitor,
            Connection = Connection,
            Block = Block,
            Protection = Protection,
            Cards = Cards,
        };

        // installedCopy: the tray runs from the installed program. otherCopy: an install is there and the tray runs from
        // another folder (a download). Neither: nothing is installed.
        bool installPresent = installedCopy || otherCopy || installProgramMissing;
        bool programPresent = installPresent && !installProgramMissing;
        var options = new TrayStartOptions(false, Settings.LastLoadStatus, installedCopy && !otherCopy ? InstalledExe : otherCopy ? OtherExe : TrayHarness.ExePath, Startup)
        {
            InstalledExePath = InstalledExe,
            ShowIcon = false,
            ExitNoticeTime = exitNoticeTime ?? TimeSpan.FromMilliseconds(10),
            CursorPosition = () => TrayHarness.ClickPoint,
            TickCount = () => 0,
            DoubleClickTime = TimeSpan.FromMilliseconds(500),
            FileExists = path => programPresent && string.Equals(path, InstalledExe, StringComparison.OrdinalIgnoreCase),
            DirectoryExists = path => installPresent && string.Equals(path, Path.GetDirectoryName(InstalledExe), StringComparison.OrdinalIgnoreCase),
            InstallFolderSecurity = new FixedFolderSecurity(installFolderSddl ?? FixedFolderSecurity.AdministratorsOnly),
            CheckInstalledFiles = path =>
            {
                CheckedFolders.Add(path);
                return (checkFiles ?? (_ => MatchingFiles))(path);
            },
            ReadInstalledFile = path => readInstalledFile?.Invoke(path) ?? new Earshot.Boot.InstalledFile(
                true, installedVersion ?? RepairPlanner.RepairVerbSince.ToVersion(), new Earshot.Contracts.StepOutcome("read-file-version", true, 0, "S_OK", path)),
            ListFolder = folder => listFolder?.Invoke(folder) ?? (installPresent && string.Equals(folder, Path.GetDirectoryName(InstalledExe), StringComparison.OrdinalIgnoreCase)
                ? (programPresent ? new[] { InstalledExe } : new[] { Path.Combine(folder, "Earshot.dll") })
                : throw new DirectoryNotFoundException(folder)),
            NativeHotkeys = new FakeNativeHotkeys(),
            VoiceEngineFactory = () => new Earshot.Tests.Voice.FakeSpeechEngine(),
            StreamingPlatformFactory = _ => new Earshot.Tests.Streaming.FakeStreamingPlatform(),
            Time = time ?? UpdateClock,
            AdvertisementSourceFactory = () => new FakeAdvertisementSource(),
            TaskbarReaderFactory = () => new FakeTaskbarReader(),
            TrayIconVisibilityFactory = () => new FakeTrayIcon(),
            ForegroundChangeSourceFactory = () => new FakeForegroundChangeSource(),
            ShellWindowSourceFactory = () => new FakeShellWindowChangeSource(),
            GaugeCoverProbeFactory = () => new FakeCoverProbe(),
            UpdateSourceFactory = sourceFactory ?? (() => Source),
            UpdateLauncher = Launcher,
            UpdateOutcome = outcomeSource,
            ElevatedExitWait = elevatedExitWait ?? TimeSpan.FromSeconds(30),
            IsElevated = () => elevated,
        };

        Coordinator = new BlockCoordinator(Registry.Monitor, Registry.Connection, Registry.Block, Registry.Protection,
            Registry.Settings, Registry.Cards, Log, CoordinatorClock, new CoordinatorOptions(safeMode, StartedAtLogon: false));
        Context = new TrayContext(Registry, Coordinator, options);
        Coordinator.Start();
        PumpUntilIdle();
    }

    public WindowsFormsSynchronizationContext Ui { get; }

    public CapturingLog Log { get; } = new();

    public string SettingsPath { get; }

    public JsonSettingsStore Settings { get; }

    public FakeDeviceMonitor Monitor { get; } = new();

    public FakeConnectionController Connection { get; } = new();

    public FakeBlockController Block { get; } = new();

    public FakeAudioProtectionController Protection { get; } = new();

    public RecordingCards Cards { get; } = new();

    public FakeStartupRegistry Startup { get; } = new();

    public FakeUpdateSource Source { get; } = new();

    // What the check of the installed files was asked to read, and what it says unless a test says otherwise.
    public List<string> CheckedFolders { get; } = new();

    public static readonly Earshot.Boot.Gate.InstalledFilesReport MatchingFiles = new(true, false, [], []);

    public FakeUpdateLauncher Launcher { get; } = new();

    // The clock the tray's daily check waits on: it moves only when a test moves it.
    public ManualTimeProvider UpdateClock { get; } = new();

    public ManualTime CoordinatorClock { get; } = new();

    public ServiceRegistry Registry { get; }

    public BlockCoordinator Coordinator { get; }

    public TrayContext Context { get; }

    public System.Windows.Forms.ToolStripMenuItem MenuItem(string text) =>
        Context.Menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(i => i.Text == text);

    public void ClickMenu(string text)
    {
        Context.Menu.Refresh();
        System.Windows.Forms.ToolStripMenuItem item = MenuItem(text);
        Assert.IsTrue(item.Available && item.Enabled, text + " cannot be clicked.");
        item.PerformClick();
    }

    // Runs what was posted to the tray's thread during start-up, such as the card that says how the last update ended.
    public void Settle()
    {
        Application.DoEvents();
        PumpUntilIdle();
    }

    public void PumpUntilIdle() => TrayHarness.PumpUntil(() => !Context.IsWorking, "The tray was still busy after 10 seconds.");

    public static void PumpUntil(Func<bool> condition, string otherwise) => TrayHarness.PumpUntil(condition, otherwise);

    public void Dispose()
    {
        Context.Dispose();
        Coordinator.Dispose();
        SynchronizationContext.SetSynchronizationContext(null);
        Ui.Dispose();
        _folder.Dispose();
    }
}

// Answers the security of any folder with one descriptor, read only. Nothing is created or read from disk, so a test
// never looks at the owner's Program Files.
internal sealed class FixedFolderSecurity(string sddl) : Earshot.Boot.Gate.IFolderSecurity
{
    // The install folder as Windows leaves it: SYSTEM and Administrators full control, Users read and execute.
    public const string AdministratorsOnly = "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;0x1200a9;;;BU)(A;OICIIOID;GA;;;CO)";

    // A folder a standard user can change: Users have full control.
    public const string UsersCanWrite = "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;FA;;;BU)";

    public Earshot.Contracts.StepOutcome CreateHardened(string path) => throw new InvalidOperationException("A fixed security only reads.");

    public Earshot.Contracts.StepOutcome CreateWithSddl(string path, string sddl) => throw new InvalidOperationException("A fixed security only reads.");

    public Earshot.Contracts.StepOutcome ReadSddl(string path, out string? result)
    {
        result = sddl;
        return new Earshot.Contracts.StepOutcome("folder-acl-read", true, 0, "S_OK", path);
    }
}
