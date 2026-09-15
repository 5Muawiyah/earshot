using Earshot.Contracts;
using Earshot.Contracts.Null;
using Earshot.Infra;

namespace Earshot.Composition;

// Builds the ServiceRegistry. Every feature starts as a null object; each feature adds its own
// CompositionRoot.<Feature>.cs implementing one Configure hook. A hook that is not implemented is
// removed by the compiler, so any subset of features builds and runs.
internal static partial class CompositionRoot
{
    // Reads EARSHOT_SAFE_MODE from the environment.
    public static ServiceRegistry Build(ILog log, ISettingsStore settings, Action<Action> uiPost) =>
        Build(log, settings, uiPost, Paths.Current.IsSafeMode);

    public static ServiceRegistry Build(ILog log, ISettingsStore settings, Action<Action> uiPost, bool safeMode)
    {
        var r = new ServiceRegistry(log, settings, uiPost, safeMode)
        {
            Monitor    = new NullDeviceMonitor(),
            Connection = new NullConnectionController(),
            Block      = new NullBlockController(),
            Protection = new NullAudioProtectionController(),
            Battery    = new NoBatterySource(),     // the only real impl in v1
            Cards      = new NullCardPresenter(log),
        };
        ConfigureAudio(r); ConfigureConnect(r); ConfigureBoot(r);
        ConfigureProtection(r); ConfigurePopup(r);

        if (safeMode)
        {
            // The registry already wraps on every assignment; this pass states the guarantee
            // here as well, and Wrap leaves an already wrapped controller as it is.
            r.Connection = SafeDecorators.Wrap(r.Connection, log);
            r.Block = SafeDecorators.Wrap(r.Block, log);
            r.Protection = SafeDecorators.Wrap(r.Protection, log);
            log.Warn("Safe mode is on. " + SafeDecorators.Message);
        }

        return r;
    }

    static partial void ConfigureAudio(ServiceRegistry r);
    static partial void ConfigureConnect(ServiceRegistry r);
    static partial void ConfigureBoot(ServiceRegistry r);
    static partial void ConfigureProtection(ServiceRegistry r);
    static partial void ConfigurePopup(ServiceRegistry r);
}
