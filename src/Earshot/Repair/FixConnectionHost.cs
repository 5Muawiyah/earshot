using System.Text.Json;
using Earshot.Bluetooth;
using Earshot.Boot;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Diagnostics;
using Earshot.Infra;
using Earshot.Service;

namespace Earshot.Repair;

// The seams of Fix connection. Each is one boundary with the machine, and each has a fake in the tests.

internal interface IFixPlanFiles
{
    FixPlanFileInfo Write(FixPlan plan);

    StepOutcome Delete(FixPlanFileInfo info);
}

// Starts the elevated mode with one administrator prompt and waits for it. It takes the consent of the first question, so no
// caller can reach it without one.
internal interface IFixElevator
{
    Task<ElevatedRun> ApplyAsync(FixConsent consent, string planPath, string planSha256, CancellationToken ct);
}

internal sealed record FixGateResult(bool Ok, string Summary, IReadOnlyList<StepOutcome> Steps, int? ExitCode);

// Requests through the gate task that Earshot's install registered. Only an installed Earshot has one.
internal interface IFixGate
{
    // The normal block of the AirPods the records pin. Ok only when the nodes read back as blocked.
    Task<FixGateResult> BlockAsync(CancellationToken ct);

    // Pins the AirPods with this address and blocks them, in one elevated gate run (the adopt-device verb).
    Task<FixGateResult> AdoptDeviceAsync(string address12, CancellationToken ct);
}

// What the re-pair shows the person while it runs.
internal interface IFixRePairUi
{
    void ShowInstruction(string text);

    // Several or one: always asked, so a device that is not the AirPods is never paired without the person choosing it.
    Task<UnpairedDevice?> PickAsync(IReadOnlyList<UnpairedDevice> candidates);

    Task<bool> ConfirmPinAsync(FixPairKind kind, string? pin);

    // The run's result. offerTrace adds a Record a Bluetooth trace button; true when the person pressed it.
    bool ShowResult(string text, bool offerTrace);

    void Close();
}

internal sealed class FixOptions
{
    // How long the watcher may list what is already known before the prompt is shown.
    public TimeSpan BaselineWait { get; init; } = TimeSpan.FromSeconds(15);

    // How long to wait for a device to appear after the prompt (a design choice: 60 s).
    public TimeSpan ScanTimeout { get; init; } = TimeSpan.FromSeconds(60);

    // How long PairAsync may run before the pairing is cancelled (a design choice: 60 s).
    public TimeSpan PairTimeout { get; init; } = TimeSpan.FromSeconds(60);

    // After the first candidate appears, how long to wait for others before the person is asked.
    public TimeSpan SettleWait { get; init; } = TimeSpan.FromSeconds(3);

    // How long to wait for the new device's nodes to appear after pairing.
    public TimeSpan PostPairWait { get; init; } = TimeSpan.FromSeconds(20);

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = static (span, ct) => Task.Delay(span, ct);
}

internal sealed class FixHost
{
    public required INodeReader Nodes { get; init; }

    public required IFixRecords Records { get; init; }

    public required IFixLeftovers Leftovers { get; init; }

    public required IInputDesktop Desktop { get; init; }

    public required IFixPrompter Prompter { get; init; }

    public required IFixPlanFiles PlanFiles { get; init; }

    public required IFixElevator Elevator { get; init; }

    public required IPairingApi Pairing { get; init; }

    public required IFixRePairUi RePairUi { get; init; }

    public required IFixGate Gate { get; init; }

    public required FixRecovery Recovery { get; init; }

    public required TextWriter Output { get; init; }

    public required ILog Log { get; init; }

    public FixOptions Options { get; init; } = new();

    // The radio check before anything is removed. Null in a host that has none (a test, or a build with no WinRT radio).
    public IRadioCheck? Radio { get; init; }

    // Where the run's one pairing entry goes. Null when the run has no history file to write to.
    public ConnectHistory? History { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    // The machine folder, where the elevated run leaves its step codes. Null when there is none to read.
    public string? MachineFolder { get; init; }

    // Asks the tray to start Record a Bluetooth trace (TraceRequestSignal); true when a tray heard it. Null when there is no tray
    // to ask, and the result then offers no trace button.
    public Func<bool>? RequestTrace { get; init; }
}

// ---- real implementations that sit on machine state ----

// Reads Earshot's footprint on this PC: what is installed and what the machine folder holds. Plain sentences for the plan.
internal sealed class MachineLeftovers : IFixLeftovers
{
    private readonly string _installFolder;
    private readonly string _machineFolder;
    private readonly Func<bool> _serviceFound;

    public MachineLeftovers(string installFolder, string machineFolder, Func<bool> serviceFound)
    {
        ArgumentNullException.ThrowIfNull(installFolder);
        ArgumentNullException.ThrowIfNull(machineFolder);
        ArgumentNullException.ThrowIfNull(serviceFound);
        _installFolder = installFolder;
        _machineFolder = machineFolder;
        _serviceFound = serviceFound;
    }

    public IReadOnlyList<string> Read()
    {
        var items = new List<string>();
        if (File.Exists(Path.Combine(_installFolder, "Earshot.exe")))
        {
            items.Add("Earshot is still installed. Fix connection does not remove it.");
        }

        if (Directory.Exists(_machineFolder))
        {
            items.Add("Earshot's records are still on this PC.");
        }

        if (_serviceFound())
        {
            items.Add("The hand-back service is still installed.");
        }

        return items;
    }

    // The service is found when the control manager lists it. A read that fails counts as not found and is logged by the
    // caller's step; Query records its own raw codes.
    public static MachineLeftovers For(Paths paths, IServiceControl service)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(service);
        return new MachineLeftovers(paths.InstallFolder, paths.MachineFolder, () => service.Query(ServicePlan.ServiceName).Presence == ServicePresence.Present);
    }
}

// The real footprint test: a settings file or an installed program, beside the records.
internal static class FixFootprint
{
    public static Func<bool> For(Paths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return () => File.Exists(paths.SettingsFile) || File.Exists(paths.InstalledExe);
    }
}

// What the elevated run leaves in the machine folder for the tray to read, when that folder exists and passed its security
// check. A strict, small JSON file; the tray reads it only to show the raw codes, never to decide anything.
internal sealed record FixResultRead(bool Ok, string Problem, int ExitCode, IReadOnlyList<StepOutcome> Steps);

internal static class FixResultFile
{
    public const int MaxBytes = 64 * 1024;
    public const int MaxSteps = 64;

    // One file, replaced by each run; the plan hash inside says which run it describes.
    public static string PathFor(string machineFolder, string planSha256) =>
        System.IO.Path.Combine(machineFolder, "fix-result.json");

    public static StepOutcome Write(string machineFolder, string planSha256, FixApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(machineFolder);
        ArgumentNullException.ThrowIfNull(result);
        string path = PathFor(machineFolder, planSha256);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("SchemaVersion", 1);
            w.WriteString("Plan", planSha256.ToUpperInvariant());
            w.WriteNumber("ExitCode", (int)result.Outcome);
            w.WriteString("Result", GateExitCodes.ResultName(result.Outcome));
            w.WriteStartArray("Steps");
            foreach (StepOutcome step in result.Steps.Take(MaxSteps))
            {
                w.WriteStartObject();
                w.WriteString("Step", step.Step);
                w.WriteBoolean("Ok", step.Ok);
                w.WriteNumber("Code", step.Code);
                w.WriteString("CodeName", step.CodeName);
                w.WriteString("Detail", step.Detail ?? "");
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        string temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, stream.ToArray());
            File.Move(temp, path, overwrite: true);
            return new StepOutcome("fix-result-write", true, 0, "ERROR_SUCCESS", path);
        }
        catch (IOException ex)
        {
            return StepOutcomes.FromHResult("fix-result-write", ex.HResult, ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StepOutcomes.FromHResult("fix-result-write", ex.HResult, ex.GetType().Name);
        }
    }

    public static FixResultRead Read(string machineFolder, string planSha256)
    {
        ArgumentNullException.ThrowIfNull(machineFolder);
        string path = PathFor(machineFolder, planSha256);
        var steps = new List<StepOutcome>();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaxBytes)
            {
                return new FixResultRead(false, "There is no result file.", -1, steps);
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (!string.Equals(root.GetProperty("Plan").GetString(), planSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new FixResultRead(false, "The result is for another plan.", -1, steps);
            }

            foreach (JsonElement item in root.GetProperty("Steps").EnumerateArray().Take(MaxSteps))
            {
                steps.Add(new StepOutcome(
                    item.GetProperty("Step").GetString() ?? "", item.GetProperty("Ok").GetBoolean(), item.GetProperty("Code").GetInt32(),
                    item.GetProperty("CodeName").GetString() ?? "", item.GetProperty("Detail").GetString()));
            }

            return new FixResultRead(true, "", root.GetProperty("ExitCode").GetInt32(), steps);
        }
        catch (JsonException ex)
        {
            return new FixResultRead(false, "The result file is not valid: " + ex.GetType().Name + ".", -1, steps);
        }
        catch (InvalidOperationException ex)
        {
            return new FixResultRead(false, "The result file is not valid: " + ex.GetType().Name + ".", -1, steps);
        }
        catch (IOException ex)
        {
            return new FixResultRead(false, "The result file could not be read: HRESULT 0x" + ex.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ".", -1, steps);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new FixResultRead(false, "The result file could not be read: " + ex.GetType().Name + ".", -1, steps);
        }
    }
}
