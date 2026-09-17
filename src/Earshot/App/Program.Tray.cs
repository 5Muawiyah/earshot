using System.Diagnostics.CodeAnalysis;
using Earshot.App;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tray;

namespace Earshot;

// Earshot.exe [--startup]
//
// The tray. Program.Main has already dispatched, so install, gate and probe never reach this file and
// never meet the single-instance lock.
//
// Order:
//   1. Single-instance mutex, per user and per session. A second copy signals the running one, which
//      shows its status card, and exits with 0. No SetForegroundWindow.
//      https://learn.microsoft.com/en-us/dotnet/api/system.threading.namedwaithandleoptions
//   2. ApplicationConfiguration.Initialize (DPI mode, visual styles), then the unhandled exception mode,
//      both before any window exists.
//   3. A WindowsFormsSynchronizationContext, installed before anything subscribes to SystemEvents, so
//      those callbacks and every await in the tray come back to the UI thread.
//      https://learn.microsoft.com/en-us/dotnet/api/microsoft.win32.systemevents.userpreferencechanged
//   4. Settings (writable store), then the ServiceRegistry, the BlockCoordinator and the TrayContext, then
//      the coordinator and the monitor are started, so both are subscribed before the first snapshot can
//      arrive.
//   5. Application.Run. Exit cancels the actions in flight and waits for them, with a limit, while the
//      loop still runs, so their continuations can complete. Then an orderly shutdown, on this thread: the
//      tray (icon and message window) and the card window, then the coordinator, the monitor, the audio
//      worker, the system worker, the show event and the mutex.
internal static partial class Program
{
    internal const string TrayUsage = "Usage: Earshot.exe [--startup]";

    // Fixed name of the single-instance mutex; the show event adds ".show". Never change it, or an
    // older and a newer build could run side by side. No backslash: that character is reserved.
    internal const string TrayInstanceName = "Earshot.f8b6e6c2-c316-4c33-b73f-6a328bde4457";

    internal static readonly NamedWaitHandleOptions TrayInstanceOptions = new() { CurrentUserOnly = true, CurrentSessionOnly = true };

    // Held for the life of the tray.
    private static Mutex? _trayInstance;

    // True while this process is the running tray.
    internal static bool IsPrimaryTray => _trayInstance is not null;

    static partial void TryRunTray(RunContext ctx)
    {
        Paths paths = Paths.Current;
        var log = new FileLog(paths.LogFolder);

        if (!IsTrayCommandLine(ctx.Args, out string? error))
        {
            ctx.ExitCode = ReportUsageError(log, "tray", error, TrayUsage);
            return;
        }

        bool startedAtLogon = ctx.Args.Length == 1 && ctx.Args[0] == StartupRegistration.StartupArgument;
        ctx.ExitCode = RunTray(paths, log, startedAtLogon);
    }

    // No arguments, or only --startup (the Run value adds it).
    internal static bool IsTrayCommandLine(IReadOnlyList<string> args, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || (args.Count == 1 && args[0] == StartupRegistration.StartupArgument))
        {
            error = null;
            return true;
        }

        error = args[0] == StartupRegistration.StartupArgument
            ? "--startup takes no further arguments."
            : "Unknown command: " + args[0];
        return false;
    }

    private static int RunTray(Paths paths, ILog log, bool startedAtLogon)
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, TrayInstanceName, TrayInstanceOptions, out createdNew);
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            log.Error("The single-instance lock could not be created, so the tray does not start.", ex);
            return ExitCodes.OsError;
        }

        if (!createdNew)
        {
            mutex.Dispose();
            SignalRunningTray(log, TrayInstanceName);
            return ExitCodes.Ok;
        }

        _trayInstance = mutex;
        try
        {
            return RunPrimaryTray(paths, log, startedAtLogon);
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            _trayInstance = null;
        }
    }

    // The name of the event a second copy sets to ask the running tray for its card.
    internal static string ShowEventName(string instanceName) => instanceName + ".show";

    // Sets the running tray's show event. Returns true when it was set. The instance name is a parameter
    // so tests can use their own and never signal a real tray.
    internal static bool SignalRunningTray(ILog log, string instanceName)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName(instanceName), TrayInstanceOptions, out EventWaitHandle? showEvent))
            {
                using (showEvent)
                {
                    showEvent.Set();
                }

                log.Info("Earshot is already running; it was asked to show its card.");
                return true;
            }

            log.Warn("Earshot is already running but has no show event yet, so it was not signalled.");
            return false;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            log.Error("Earshot is already running but could not be signalled.", ex);
            return false;
        }
    }

    private static int RunPrimaryTray(Paths paths, ILog log, bool startedAtLogon)
    {
        EventWaitHandle showEvent;
        try
        {
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName(TrayInstanceName), TrayInstanceOptions, out _);
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            log.Error("The show event could not be created, so the tray does not start.", ex);
            return ExitCodes.OsError;
        }

        using (showEvent)
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            using var ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);

            TrayContext? context = null;
            ThreadExceptionEventHandler onThreadException = (_, e) =>
            {
                if (context is not null)
                {
                    context.ReportUnexpected("tray", e.Exception, CardPlace.NearTray);
                }
                else
                {
                    log.Error("tray: unexpected error before the tray was ready.", e.Exception);
                }
            };
            UnhandledExceptionEventHandler onUnhandled = (_, e) =>
                log.Error("Unhandled error; Earshot is closing.", e.ExceptionObject as Exception);
            EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
                log.Error("A background task failed and nothing observed it.", e.Exception);

            Application.ThreadException += onThreadException;
            AppDomain.CurrentDomain.UnhandledException += onUnhandled;
            TaskScheduler.UnobservedTaskException += onUnobserved;

            ServiceRegistry? registry = null;
            BlockCoordinator? coordinator = null;
            RegisteredWaitHandle? showWait = null;
            try
            {
                var settings = new JsonSettingsStore(paths.SettingsFile, log);
                registry = CompositionRoot.Build(log, settings, action => ui.Post(static state => ((Action)state!)(), action), paths.IsSafeMode);
                coordinator = new BlockCoordinator(
                    registry.Monitor, registry.Connection, registry.Block, registry.Protection, registry.Settings,
                    registry.Cards, log, TimeProvider.System,
                    new CoordinatorOptions(paths.IsSafeMode, startedAtLogon));
                context = new TrayContext(registry, coordinator, new TrayStartOptions(
                    FirstRun: settings.LastLoadStatus == SettingsLoadStatus.CreatedDefaults,
                    SettingsStatus: settings.LastLoadStatus,
                    ExePath: Environment.ProcessPath,
                    StartupRegistry: new CurrentUserStartupRegistry())
                {
                    StartedAtLogon = startedAtLogon,
                    DataRootRedirected = paths.IsRedirected,
                    InstalledExePath = paths.InstalledExe,
                });

                TrayContext shown = context;
                showWait = ThreadPool.RegisterWaitForSingleObject(
                    showEvent,
                    (_, _) => ui.Post(static state => ((TrayContext)state!).ShowStatusCard(), shown),
                    state: null,
                    Timeout.Infinite,
                    executeOnlyOnce: false);

                coordinator.Start();
                registry.Monitor.Start();
                log.Info("Tray started" + (paths.IsSafeMode ? " in safe mode" : "") + ". Settings: " + settings.FilePath + " (" + settings.LastLoadStatus + ").");
                Application.Run(context);

                // Exit waits for actions in flight before it ends the loop; anything left here outlived that wait.
                log.Info("Tray message loop ended." + (context.PendingActions > 0
                    ? " " + context.PendingActions.ToString(System.Globalization.CultureInfo.InvariantCulture) + " action(s) were still in flight and are abandoned."
                    : ""));
                return ExitCodes.Ok;
            }
            catch (Exception ex)
            {
                log.Error("The tray stopped after an unexpected error.", ex);
                return ExitCodes.Software;
            }
            finally
            {
                if (showWait is not null)
                {
                    // Waits for a show callback that is already running, so nothing posts to a closed tray.
                    using var unregistered = new ManualResetEvent(false);
                    if (showWait.Unregister(unregistered))
                    {
                        unregistered.WaitOne();
                    }
                }

                // Nothing after this point may resume on the UI thread: its message loop has ended. The card
                // window and its timer belong to this thread, so the presenter is disposed here too.
                SynchronizationContext.SetSynchronizationContext(null);
                context?.Dispose();
                (registry?.Cards as IDisposable)?.Dispose();
                coordinator?.Dispose();
                registry?.Monitor.Dispose();
                if (registry?.Worker is { } worker)
                {
                    worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                (registry?.SystemWorker as IDisposable)?.Dispose();

                Application.ThreadException -= onThreadException;
                AppDomain.CurrentDomain.UnhandledException -= onUnhandled;
                TaskScheduler.UnobservedTaskException -= onUnobserved;
                log.Info("Tray stopped.");
            }
        }
    }
}
