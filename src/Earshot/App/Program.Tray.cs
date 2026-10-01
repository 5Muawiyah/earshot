using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Earshot.App;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Interop;
using Earshot.Tray;
using Earshot.Update;
using Earshot.Widget.Alert;

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
    internal const string TrayUsage = "Usage: Earshot.exe [--startup | --exit]";

    // Earshot.exe --exit asks the running tray to exit, by the menu's own Exit (which lets go of the AirPods in use and
    // blocks them when Hand back is on), and ends. The install script uses it to close the tray before it replaces the files,
    // without ending the process from outside. Any process of the same user in the same session can already end the tray, so
    // it grants nothing new.
    internal const string ExitArgument = "--exit";

    // The exit code of --exit when no tray is running (EX_TEMPFAIL): distinct from 0, which means the tray was asked.
    internal const int NoTrayRunning = 75;

    // Fixed name of the single-instance mutex; the show event adds ".show". Never change it, or an
    // older and a newer build could run side by side. No backslash: that character is reserved.
    internal const string TrayInstanceName = "Earshot.f8b6e6c2-c316-4c33-b73f-6a328bde4457";

    internal static readonly NamedWaitHandleOptions TrayInstanceOptions = new() { CurrentUserOnly = true, CurrentSessionOnly = true };

    // How long closing waits for the audio worker to stop. A driver call that stalls inside IKsControl.KsProperty
    // keeps the worker busy until it returns, and nothing documents a limit for that, so after this long the
    // worker, a background thread, is left to end with the process rather than keep a closed tray running with
    // its single-instance lock held. A waiting budget, not a measured figure.
    // https://learn.microsoft.com/en-us/dotnet/api/system.threading.thread.isbackground
    internal static readonly TimeSpan AudioWorkerStopLimit = TimeSpan.FromSeconds(10);

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

        // A second copy that only asks the running tray to exit never becomes a tray itself: it takes no lock.
        if (ctx.Args.Length == 1 && ctx.Args[0] == ExitArgument)
        {
            ctx.ExitCode = RequestTrayExit(log, TrayInstanceName) ? ExitCodes.Ok : NoTrayRunning;
            return;
        }

        bool startedAtLogon = ctx.Args.Length == 1 && ctx.Args[0] == StartupRegistration.StartupArgument;
        ctx.ExitCode = RunTray(paths, log, startedAtLogon);
    }

    // No arguments, only --startup (the Run value adds it), or only --exit.
    internal static bool IsTrayCommandLine(IReadOnlyList<string> args, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || (args.Count == 1 && (args[0] == StartupRegistration.StartupArgument || args[0] == ExitArgument)))
        {
            error = null;
            return true;
        }

        error = args[0] == StartupRegistration.StartupArgument || args[0] == ExitArgument
            ? args[0] + " takes no further arguments."
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
        string? startAfterExit = null;
        var startKind = StartAfterExitKind.Switch;
        try
        {
            return RunPrimaryTray(paths, log, startedAtLogon, out startAfterExit, out startKind);
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            _trayInstance = null;

            // The switch to the installed copy: started now that the lock is free, so it becomes the tray.
            if (startAfterExit is not null)
            {
                InstalledCopyStarter.Start(startAfterExit, log, kind: startKind);
            }
        }
    }

    // Stops the audio worker, waiting at most limit. True when it stopped; a worker that did not stop in time, or
    // stopped with an error, is logged by name and left to end with the process.
    internal static bool StopAudioWorker(IAudioWorker worker, ILog log, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(log);
        Task stopping;
        try
        {
            stopping = worker.DisposeAsync().AsTask();
        }
        catch (Exception ex)
        {
            log.Error("Closing: the audio worker could not be stopped.", ex);
            return false;
        }

        try
        {
            if (stopping.Wait(limit))
            {
                return true;
            }
        }
        catch (AggregateException ex)
        {
            log.Error("Closing: the audio worker stopped with an error.", ex.InnerException ?? ex);
            return false;
        }

        log.Warn("Closing: the audio worker did not stop within " + limit.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " s, so a call to the audio driver may still be running. It is a background thread and ends with the process.");
        return false;
    }

    // The name of the event a second copy sets to ask the running tray for its card.
    internal static string ShowEventName(string instanceName) => instanceName + ".show";

    // The name of the event a second copy started with --exit sets to ask the running tray to exit.
    internal static string ExitEventName(string instanceName) => instanceName + ".exit";

    // Sets the running tray's exit event. True when it was set, which says the tray was asked, not that it has ended. The
    // instance name is a parameter so tests can use their own and never signal a real tray.
    internal static bool RequestTrayExit(ILog log, string instanceName)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName(instanceName), TrayInstanceOptions, out EventWaitHandle? exitEvent))
            {
                using (exitEvent)
                {
                    exitEvent.Set();
                }

                log.Info("Earshot is running; it was asked to exit.");
                return true;
            }

            log.Warn("Earshot is not running, so there is nothing to ask to exit.");
            return false;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            log.Error("Earshot could not be asked to exit.", ex);
            return false;
        }
    }

    // The event a copy started with --exit sets, created beside the show event with the same owner and session. Null, logged,
    // when it cannot be created.
    private static EventWaitHandle? TryCreateExitEvent(ILog log, string instanceName)
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName(instanceName), TrayInstanceOptions, out _);
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            log.Error("The exit event could not be created, so a copy started with --exit cannot ask this tray to exit.", ex);
            return null;
        }
    }

    // Makes a request to exit (RequestTrayExit for the same instance name) end in the tray's own Exit: the event is created, and
    // each time it is set the tray's ExitFromSignal is posted on to the UI context. Null, logged, when the event could not be
    // created (the tray still runs; a copy started with --exit then finds no tray to ask). Dispose the result, waiting for a
    // callback that is already running, before the tray is closed. The instance name is a parameter so a test can use its own.
    internal static IDisposable? WireExitSignal(ILog log, string instanceName, SynchronizationContext ui, TrayContext tray)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(tray);
        EventWaitHandle? exitEvent = TryCreateExitEvent(log, instanceName);
        if (exitEvent is null)
        {
            return null;
        }

        RegisteredWaitHandle wait = RegisterExitWait(exitEvent, () => ui.Post(static state => ((TrayContext)state!).ExitFromSignal(), tray));
        return new ExitSignal(exitEvent, wait);
    }

    private sealed class ExitSignal(EventWaitHandle exitEvent, RegisteredWaitHandle wait) : IDisposable
    {
        public void Dispose()
        {
            using (var unregistered = new ManualResetEvent(false))
            {
                if (wait.Unregister(unregistered))
                {
                    unregistered.WaitOne();
                }
            }

            exitEvent.Dispose();
        }
    }

    // Runs onExit on a thread-pool thread each time the event is set; the caller posts it on to the UI thread. Unregister
    // the result, waiting for a callback that is already running, before what it uses is closed.
    internal static RegisteredWaitHandle RegisterExitWait(EventWaitHandle exitEvent, Action onExit)
    {
        ArgumentNullException.ThrowIfNull(exitEvent);
        ArgumentNullException.ThrowIfNull(onExit);
        return ThreadPool.RegisterWaitForSingleObject(
            exitEvent,
            (_, _) => onExit(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

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

    private static int RunPrimaryTray(Paths paths, ILog log, bool startedAtLogon, out string? startAfterExit, out StartAfterExitKind startKind)
    {
        startAfterExit = null;
        startKind = StartAfterExitKind.Switch;
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
            IDisposable? exitSignal = null;
            try
            {
                var settings = new JsonSettingsStore(paths.SettingsFile, log);
                registry = CompositionRoot.Build(log, settings, action => ui.Post(static state => ((Action)state!)(), action), paths.IsSafeMode);
                coordinator = new BlockCoordinator(
                    registry.Monitor, registry.Connection, registry.Block, registry.Protection, registry.Settings,
                    registry.Cards, log, TimeProvider.System,
                    new CoordinatorOptions(paths.IsSafeMode, startedAtLogon));

                // Before any UI is created (the tray's first window is TrayContext's own ShellMessageWindow,
                // constructed below): the toast route's AppUserModelID must be set before a window or a toast
                // exists. A failure here must not stop the tray; it is logged and the widget's low battery
                // alert simply falls back to the card, as ToastNotifier itself already does for any failure.
                int amuidHr = Shell.SetCurrentProcessExplicitAppUserModelID(NotificationRegistration.AppUserModelId);
                log.Write(
                    amuidHr >= 0 ? LogLevel.Info : LogLevel.Warn,
                    "SetCurrentProcessExplicitAppUserModelID: 0x" + amuidHr.ToString("X8", CultureInfo.InvariantCulture) + ".");

                // Run once at tray start, idempotent: writes or repairs the per-user Start menu shortcut the
                // toast route needs. Never in safe mode or against a redirected data root (WritesBlocked).
                // TryCreate, not the constructor directly: GetFolderPath returns an empty string rather than
                // throwing when the Programs folder does not exist, and a missing shortcut folder must not
                // stop the tray starting any more than SetCurrentProcessExplicitAppUserModelID's own failure
                // above does.
                NotificationRegistration.TryCreate(
                    new RealShellLinkWriter(), log, paths.IsSafeMode, paths.IsRedirected,
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.ProcessPath, paths.InstalledExe)
                    ?.Register();

                context = new TrayContext(registry, coordinator, new TrayStartOptions(
                    FirstRun: settings.LastLoadStatus == SettingsLoadStatus.CreatedDefaults,
                    SettingsStatus: settings.LastLoadStatus,
                    ExePath: Environment.ProcessPath,
                    StartupRegistry: new CurrentUserStartupRegistry())
                {
                    StartedAtLogon = startedAtLogon,
                    DataRootRedirected = paths.IsRedirected,
                    InstalledExePath = paths.InstalledExe,
                    UpdateOutcome = paths.IsSafeMode || paths.IsRedirected
                        ? null
                        : new UpdateOutcomeSource(paths.MachineFolder, Path.Combine(paths.LocalFolder, "update", "outcome-shown.txt")),
                });

                TrayContext shown = context;
                showWait = ThreadPool.RegisterWaitForSingleObject(
                    showEvent,
                    (_, _) => ui.Post(static state => ((TrayContext)state!).ShowStatusCard(), shown),
                    state: null,
                    Timeout.Infinite,
                    executeOnlyOnce: false);

                exitSignal = WireExitSignal(log, TrayInstanceName, ui, shown);

                coordinator.Start();
                registry.Monitor.Start();
                log.Info("Tray started" + (paths.IsSafeMode ? " in safe mode" : "") + ". Settings: " + settings.FilePath + " (" + settings.LastLoadStatus + ").");
                Application.Run(context);
                startAfterExit = context.StartAfterExit;
                startKind = context.StartAfterExitKind;

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

                exitSignal?.Dispose();

                // Nothing after this point may resume on the UI thread: its message loop has ended. The card
                // window and its timer belong to this thread, so the presenter is disposed here too.
                SynchronizationContext.SetSynchronizationContext(null);
                context?.Dispose();
                (registry?.Cards as IDisposable)?.Dispose();
                coordinator?.Dispose();
                registry?.Monitor.Dispose();
                if (registry?.Worker is { } worker)
                {
                    StopAudioWorker(worker, log, AudioWorkerStopLimit);
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
