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

    private readonly TempFolder _folder = new();

    public UpdateTrayHarness(
        bool safeMode = false,
        Action<EarshotSettings>? settings = null,
        Action<FakeUpdateSource>? source = null,
        bool pinned = true,
        bool installedCopy = true,
        Func<IUpdateSource>? sourceFactory = null,
        TimeProvider? time = null)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException, threadScope: true);
        Ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(Ui);
        source?.Invoke(Source);

        Monitor.Current = NoDevice();
        Startup.Run[StartupRegistration.ValueName] = StartupRegistration.CommandFor(TrayHarness.ExePath);
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

        var options = new TrayStartOptions(false, Settings.LastLoadStatus, installedCopy ? InstalledExe : TrayHarness.ExePath, Startup)
        {
            InstalledExePath = InstalledExe,
            ShowIcon = false,
            ExitNoticeTime = TimeSpan.FromMilliseconds(10),
            CursorPosition = () => TrayHarness.ClickPoint,
            TickCount = () => 0,
            DoubleClickTime = TimeSpan.FromMilliseconds(500),
            FileExists = path => installedCopy && string.Equals(path, InstalledExe, StringComparison.OrdinalIgnoreCase),
            NativeHotkeys = new FakeNativeHotkeys(),
            VoiceEngineFactory = () => new Earshot.Tests.Voice.FakeSpeechEngine(),
            StreamingPlatformFactory = _ => new Earshot.Tests.Streaming.FakeStreamingPlatform(),
            Time = time ?? UpdateClock,
            AdvertisementSourceFactory = () => new FakeAdvertisementSource(),
            TaskbarReaderFactory = () => new FakeTaskbarReader(),
            TrayIconVisibilityFactory = () => new FakeTrayIcon(),
            ForegroundChangeSourceFactory = () => new FakeForegroundChangeSource(),
            GaugeCoverProbeFactory = () => new FakeCoverProbe(),
            UpdateSourceFactory = sourceFactory ?? (() => Source),
            UpdateLauncher = Launcher,
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
