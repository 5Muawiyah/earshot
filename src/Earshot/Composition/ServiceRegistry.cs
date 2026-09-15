using Earshot.Contracts;
using Earshot.Contracts.Null;

namespace Earshot.Composition;

// The services one run of Earshot uses. Built by CompositionRoot.Build; each feature's
// Configure hook replaces the null object it owns.
//
// In safe mode every assignment to Connection, Block or Protection is wrapped in its Safe
// decorator, so no code path can install a live controller around the switch.
internal sealed class ServiceRegistry
{
    private IConnectionController _connection = new NullConnectionController();
    private IBlockController _block = new NullBlockController();
    private IAudioProtectionController _protection = new NullAudioProtectionController();

    public ServiceRegistry(ILog log, ISettingsStore settings, Action<Action> uiPost, bool safeMode)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(uiPost);

        Log = log;
        Settings = settings;
        UiPost = uiPost;
        SafeMode = safeMode;
        Cards = new NullCardPresenter(log);
        if (safeMode)
        {
            _connection = SafeDecorators.Wrap(_connection, log);
            _block = SafeDecorators.Wrap(_block, log);
            _protection = SafeDecorators.Wrap(_protection, log);
        }
    }

    public ILog Log { get; }

    public ISettingsStore Settings { get; }

    // Runs an action on the UI thread. It must queue the action and return at once
    // (SynchronizationContext.Post or Control.BeginInvoke) and never block waiting for it to run
    // (SynchronizationContext.Send, Control.Invoke): the audio worker calls it, and a blocking post deadlocks
    // as soon as the UI thread waits on work queued to that worker.
    public Action<Action> UiPost { get; }

    // True when EARSHOT_SAFE_MODE is set: live device actions are refused and nothing is
    // written to HKCU.
    public bool SafeMode { get; }

    // The single MTA apartment for Core Audio work, created by the audio hook.
    public IAudioWorker? Worker { get; set; }

    // The background thread for Task Scheduler runs and CfgMgr32 reads, for the block and protection
    // controllers to share so their gate requests never overlap. Null until a hook creates it.
    public Earshot.Boot.ISystemWorker? SystemWorker { get; set; }

    public IDeviceMonitor Monitor { get; set; } = new NullDeviceMonitor();

    public IConnectionController Connection
    {
        get => _connection;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _connection = SafeMode ? SafeDecorators.Wrap(value, Log) : value;
        }
    }

    public IBlockController Block
    {
        get => _block;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _block = SafeMode ? SafeDecorators.Wrap(value, Log) : value;
        }
    }

    public IAudioProtectionController Protection
    {
        get => _protection;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _protection = SafeMode ? SafeDecorators.Wrap(value, Log) : value;
        }
    }

    public IBatteryProvider Battery { get; set; } = new NoBatterySource();

    public ICardPresenter Cards { get; set; }
}
