using System.Globalization;
using System.Text.Json;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;

namespace Earshot.Repair;

internal static class FixRules
{
    // A person can see a dialog only when this program runs on the desktop that receives input. A private desktop is "user
    // interactive" to .NET, so the names are compared. Either name missing means the answer is no.
    // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openinputdesktop
    public static bool IsInteractive(DesktopReading reading) =>
        reading.UserInteractive &&
        !string.IsNullOrEmpty(reading.ThreadDesktop) &&
        !string.IsNullOrEmpty(reading.InputDesktop) &&
        string.Equals(reading.ThreadDesktop, reading.InputDesktop, StringComparison.OrdinalIgnoreCase);

    // The AirPods the identity rule finds whose container is not the one Earshot protects. A list only: nothing here adopts,
    // pins or blocks. The tray shows "not protected" for a non-empty list.
    public static IReadOnlyList<AirPodsCandidate> UnprotectedAirPods(AirPodsDiscoveryResult discovery, Guid pinnedContainer)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        return discovery.Candidates.Where(c => c.ContainerId != pinnedContainer).ToList();
    }

    // The normal block follows a Fix run that changed something when Earshot is installed (it holds a valid device record
    // and its settings) and Block at boot is on.
    public static bool ShouldEndWithBlock(GateRead<DeviceIdentity> device, GateRead<GateConfig> config)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(config);
        return device.IsOk && config.IsOk && config.Value is { BlockAtBoot: true };
    }
}

internal enum FixRecoveryState
{
    None,
    WindowOpen,
    Stale,
}

internal sealed record FixRecoveryRead(FixRecoveryState State, string Stage);

internal enum FixRecoveryOutcome
{
    NoMarker,
    WindowOpen,
    Blocked,
    BlockFailed,
    NothingToBlock,
}

// A small file in the user's folder that says a Fix run has changed things and not yet ended with the block. While the run that
// wrote it is alive and the window is young, the new link of a re-pair is not cut. Once the run is gone or the window is over,
// the marker is stale and the next start of the tray answers it with the normal block.
//
// The file is the user's to write, so it can only keep a window open for WindowLength, never longer, and a missing or damaged
// file reads as nothing or as stale, never as an open window.
internal sealed class FixRecovery
{
    // Long enough for the administrator prompt, the unpair, the scan and the pairing.
    public static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(15);

    private const int MaxBytes = 4096;

    private readonly string _file;
    private readonly TimeProvider _time;
    private readonly Func<int, bool> _processAlive;

    public FixRecovery(string file, TimeProvider time, Func<int, bool> processAlive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(processAlive);
        _file = file;
        _time = time;
        _processAlive = processAlive;
    }

    public StepOutcome Begin(string stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        DateTimeOffset now = _time.GetUtcNow();
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("Stage", stage);
            w.WriteString("OpenedUtc", now.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("ExpiresUtc", (now + WindowLength).ToString("O", CultureInfo.InvariantCulture));
            w.WriteNumber("ProcessId", Environment.ProcessId);
            w.WriteEndObject();
        }

        try
        {
            string? folder = Path.GetDirectoryName(_file);
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }

            string temp = _file + ".tmp";
            File.WriteAllBytes(temp, stream.ToArray());
            File.Move(temp, _file, overwrite: true);
            return new StepOutcome("fix-marker-write", true, 0, "ERROR_SUCCESS", stage);
        }
        catch (IOException ex)
        {
            return StepOutcomes.FromHResult("fix-marker-write", ex.HResult, ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StepOutcomes.FromHResult("fix-marker-write", ex.HResult, ex.GetType().Name);
        }
    }

    public StepOutcome Clear()
    {
        try
        {
            File.Delete(_file);
            return new StepOutcome("fix-marker-clear", true, 0, "ERROR_SUCCESS", null);
        }
        catch (IOException ex)
        {
            return StepOutcomes.FromHResult("fix-marker-clear", ex.HResult, ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StepOutcomes.FromHResult("fix-marker-clear", ex.HResult, ex.GetType().Name);
        }
    }

    public FixRecoveryRead Read()
    {
        FileInfo info = new(_file);
        try
        {
            if (!info.Exists)
            {
                return new FixRecoveryRead(FixRecoveryState.None, "");
            }

            if (info.Length > MaxBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new FixRecoveryRead(FixRecoveryState.Stale, "");
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(_file), new JsonDocumentOptions { MaxDepth = 4 });
            JsonElement root = document.RootElement;
            string stage = root.GetProperty("Stage").GetString() ?? "";
            DateTimeOffset expires = DateTimeOffset.Parse(root.GetProperty("ExpiresUtc").GetString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            int pid = root.GetProperty("ProcessId").GetInt32();

            // Never open for longer than the window, whatever the file claims.
            DateTimeOffset opened = DateTimeOffset.Parse(root.GetProperty("OpenedUtc").GetString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            DateTimeOffset now = _time.GetUtcNow();
            bool open = now < expires && now < opened + WindowLength && now >= opened - TimeSpan.FromMinutes(1) && _processAlive(pid);
            return new FixRecoveryRead(open ? FixRecoveryState.WindowOpen : FixRecoveryState.Stale, stage);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Unreadable counts as stale: a run may have changed things, and the block is the safe answer.
            return new FixRecoveryRead(FixRecoveryState.Stale, "unreadable: " + ex.GetType().Name);
        }
    }

    // At the tray's start: a stale marker is answered with the normal block (when Earshot is installed and Block at boot is
    // on) and cleared once that worked. An open window is left alone.
    public async Task<FixRecoveryOutcome> RecoverAsync(IFixRecords records, IFixGate gate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(gate);
        FixRecoveryRead read = Read();
        if (read.State == FixRecoveryState.None)
        {
            return FixRecoveryOutcome.NoMarker;
        }

        if (read.State == FixRecoveryState.WindowOpen)
        {
            return FixRecoveryOutcome.WindowOpen;
        }

        if (!FixRules.ShouldEndWithBlock(records.ReadDevice(), records.ReadConfig()))
        {
            Clear();
            return FixRecoveryOutcome.NothingToBlock;
        }

        FixGateResult block = await gate.BlockAsync(ct).ConfigureAwait(false);
        if (!block.Ok)
        {
            return FixRecoveryOutcome.BlockFailed;
        }

        Clear();
        return FixRecoveryOutcome.Blocked;
    }
}
