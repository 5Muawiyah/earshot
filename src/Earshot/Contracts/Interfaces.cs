namespace Earshot.Contracts;

public interface ILog
{
    void Write(LogLevel level, string message, Exception? ex = null);
}

public static class LogExtensions
{
    public static void Info (this ILog l, string m)               => l.Write(LogLevel.Info,  m);
    public static void Warn (this ILog l, string m, Exception? e = null) => l.Write(LogLevel.Warn, m, e);
    public static void Error(this ILog l, string m, Exception? e = null) => l.Write(LogLevel.Error, m, e);
}

// One dedicated MTA apartment; all Core Audio / DeviceTopology / IKsControl work runs here.
public interface IAudioWorker : IAsyncDisposable
{
    Task<T> RunAsync<T>(Func<CancellationToken, T> work, CancellationToken ct = default);
    Task    RunAsync(Action<CancellationToken> work, CancellationToken ct = default);
}

public interface IDeviceMonitor : IDisposable
{
    DeviceSnapshot Current { get; }
    event EventHandler<DeviceSnapshotEventArgs>? SnapshotChanged; // raised on the UI sync context
    void Start();
    Task<DeviceSnapshot> RefreshAsync(CancellationToken ct = default);
}

public interface IConnectionController                            // pure Core Audio / IKsControl; no admin
{
    Task<ConnectResult> ConnectAsync(Guid containerId, CancellationToken ct = default);
    Task<ConnectResult> DisconnectAsync(Guid containerId, CancellationToken ct = default);
}

public interface IBlockController
{
    bool IsSetUp { get; }                                          // \Earshot\Gate present + DACL verified
    Task<BootBlockStatus> GetStatusAsync(CancellationToken ct = default);   // read-only, non-admin
    Task<ControllerResult> BlockAsync(CancellationToken ct = default);      // RunEx "block"
    Task<ControllerResult> AllowAsync(CancellationToken ct = default);      // RunEx "allow"
    // The parameter is blockAtBoot rather than on: CA1716 rejects a parameter of an interface member
    // named after a language keyword, and On is a Visual Basic keyword.
    // https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1716
    Task<ControllerResult> SetBlockAtBootAsync(bool blockAtBoot, CancellationToken ct = default);
    Task<ControllerResult> SetDeviceAsync(string address12, CancellationToken ct = default); // RunEx "set-device"
    Task<ControllerResult> RunSetupAsync(CancellationToken ct = default);   // ShellExecute runas (one UAC)
    Task<ControllerResult> UninstallAsync(CancellationToken ct = default);
}

public interface IAudioProtectionController
{
    Task<AudioProtectionSnapshot> GetStatusAsync(CancellationToken ct = default); // read-only, non-admin
    Task<ControllerResult> ApplyAsync(bool protect, CancellationToken ct = default); // via SYSTEM gate only
}

// Phase 0 resolved: no battery source on this hardware. Kept so a future AACP/WinRT source
// can be added without touching the UI contract. HasSource is false in v1; the UI omits the element.
public interface IBatteryProvider
{
    bool HasSource { get; }
    BatteryReading Read(Guid containerId);
}

// Card surface. The popup card implements it; everything else only calls Show and Hide.
public enum CardAnchor { NearCursor, NearTray }
public sealed record CardContent(string Title, string Status);   // Title = device name; no battery field in v1
public interface ICardPresenter
{
    void Show(CardContent content, CardAnchor anchor);

    // A card that follows a click at clickPoint, the cursor position in physical pixels read when the click
    // happened. A presenter that cannot place a card at a point shows it as Show(content, anchor) does.
    void Show(CardContent content, CardAnchor anchor, System.Drawing.Point clickPoint) => Show(content, anchor);

    void Hide();
}
