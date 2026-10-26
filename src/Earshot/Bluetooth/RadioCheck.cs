using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Earshot.Contracts;
using Earshot.Interop;
using Earshot.Repair;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Earshot.Bluetooth;

// The radio check: is Bluetooth on this PC in a state where a connect or a pairing can work at all? Read-only.
//
// It reads the default adapter, whether it does Classic Bluetooth, the radio's state, and the radio's device node (status bits,
// problem number, driver version and date). Neither WinRT API is on the list of WinRT APIs a desktop app cannot use.
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothadapter.getdefaultasync
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.radios.radiostate
// https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-api-desktop-app-support
//
// What only a live run on a real radio proves: what GetDefaultAsync returns with no adapter (null reads as NoAdapter here; an
// exception, or a Windows too old to ask, is a read that did not answer and claims nothing), the Disabled state of a hardware or software switch, a real problem code, and the Unreachable line (it
// comes from a pairing result, never from these reads; see PairingSession).

internal enum RadioCheckState
{
    Ok,
    Off,
    SwitchedOff,
    Problem,
    NoAdapter,
    NoClassic,
    Unreachable,
    Unknown,
}

// The button a line offers. The tray opens ms-settings:bluetooth for BluetoothSettings.
internal enum RadioAction
{
    None,
    BluetoothSettings,
    RecordTrace,
}

// Windows.Devices.Radios.RadioState: Unknown 0, On 1, Off 2, Disabled 3.
internal static class RadioStateValues
{
    public const int Unknown = 0;
    public const int On = 1;
    public const int Off = 2;
    public const int Disabled = 3;
}

// What the adapter reads gave. RadioState is null when the radio could not be asked; InstanceId is null when the radio's node
// could not be named. Answered is false when the default adapter could not be asked at all (the call threw, or Windows is too
// old for it): then Found says nothing about whether there is an adapter.
internal sealed record AdapterReading(bool Found, bool ClassicSupported, int? RadioState, string? InstanceId, IReadOnlyList<StepOutcome> Steps, bool Answered = true);

internal interface IRadioApi
{
    Task<AdapterReading> ReadAsync(CancellationToken ct);
}

internal interface IDevNodeStatusReader
{
    DevNodeStatusInfo Read(string instanceId);
}

internal interface IRadioCheck
{
    Task<RadioCheckResult> CheckAsync(CancellationToken ct);
}

// Everything one check read, for the card, the connect history and the doctor. Line is null when nothing is claimed.
internal sealed record RadioCheckResult(
    RadioCheckState State,
    string? Line,
    RadioAction Action,
    bool? AdapterFound,
    bool? ClassicSupported,
    int? RadioState,
    uint? DevNodeStatus,
    int? ProblemCode,
    string? DriverVersion,
    string? DriverDate,
    IReadOnlyList<StepOutcome> ReadCodes,
    long ElapsedMs)
{
    // True when the state means a connect or a pairing cannot work: the card shows this line and its button instead of an offer.
    public bool BlocksRepair => State is RadioCheckState.Off or RadioCheckState.SwitchedOff or RadioCheckState.Problem or RadioCheckState.NoAdapter or RadioCheckState.NoClassic or RadioCheckState.Unreachable;

    // The number the history keeps for this state: 0 for Ok, the radio state for Off and SwitchedOff, the problem number for
    // Problem, the first failing read for NoAdapter, 0 for NoClassic, and the radio state (or the first failing read, or 0) for
    // Unknown.
    public int HistoryCode => State switch
    {
        RadioCheckState.Ok => 0,
        RadioCheckState.Off => RadioStateValues.Off,
        RadioCheckState.SwitchedOff => RadioStateValues.Disabled,
        RadioCheckState.Problem => ProblemCode ?? 0,
        RadioCheckState.NoAdapter => ReadCodes.FirstOrDefault(s => !s.Ok)?.Code ?? 0,
        RadioCheckState.NoClassic => 0,
        _ => RadioState ?? ReadCodes.FirstOrDefault(s => !s.Ok)?.Code ?? 0,
    };

    // A read that ran past this build's own budget is named as Earshot's, so the history does not take it for a Windows timeout.
    public StepOutcome ToStep() => new("radio-check", State == RadioCheckState.Ok, HistoryCode, TimedOut ? StepOutcomes.EarshotTimeoutName : State.ToString(), null);

    private bool TimedOut => State == RadioCheckState.Unknown && ReadCodes.Any(s => s.Step == "radio-read" && s.CodeName == StepOutcomes.EarshotTimeoutName);
}

internal static class RadioLines
{
    public const string Off = "Bluetooth is off.";
    // RadioState.Disabled: off by the firmware or a hardware switch, and Microsoft's remarks add system policy; it cannot be
    // changed from a program. https://learn.microsoft.com/en-us/uwp/api/windows.devices.radios.radiostate
    public const string SwitchedOff = "Bluetooth is turned off by a switch, the firmware or a policy on this PC.";
    public const string NoAdapter = "Windows finds no Bluetooth on this PC.";
    public const string NoClassic = "Bluetooth on this PC can't do Classic Bluetooth, which AirPods need for sound.";
    // Says what was seen and no more: Windows' status for the pairing (NotPaired, AuthenticationTimeout or Failed) does not say
    // the device answered, and does not say this PC failed to reach it either.
    public const string Unreachable = "This PC found the headphones, but the pairing didn't finish.";

    public static string UnreachableLine(int status) =>
        "This PC found the headphones, but the pairing didn't finish (Windows status " + status.ToString(CultureInfo.InvariantCulture) + ").";
    public const string BluetoothSettingsButton = "Bluetooth settings";
    public const string RecordTraceButton = "Record a Bluetooth trace";

    public static string ProblemLine(int code) =>
        "Bluetooth on this PC isn't working. Windows reports problem code " + code.ToString(CultureInfo.InvariantCulture) + ".";

    public static string? For(RadioCheckState state, int? problemCode) => state switch
    {
        RadioCheckState.Off => Off,
        RadioCheckState.SwitchedOff => SwitchedOff,
        RadioCheckState.Problem => ProblemLine(problemCode ?? 0),
        RadioCheckState.NoAdapter => NoAdapter,
        RadioCheckState.NoClassic => NoClassic,
        RadioCheckState.Unreachable => Unreachable,
        _ => null,
    };

    public static RadioAction ActionFor(RadioCheckState state) => state switch
    {
        RadioCheckState.Off or RadioCheckState.SwitchedOff or RadioCheckState.Problem or RadioCheckState.NoAdapter => RadioAction.BluetoothSettings,
        RadioCheckState.Unreachable => RadioAction.RecordTrace,
        _ => RadioAction.None,
    };
}

internal sealed class RadioCheck : IRadioCheck
{
    // One read of the radio is held to this long on the way to a failure card.
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(1);

    private readonly IRadioApi _api;
    private readonly IDevNodeStatusReader _nodes;
    private readonly TimeSpan _budget;
    private readonly ILog? _log;

    public RadioCheck(IRadioApi api, IDevNodeStatusReader nodes, TimeSpan? budget = null, ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(nodes);
        _api = api;
        _nodes = nodes;
        _budget = budget ?? DefaultBudget;
        _log = log;
    }

    public static RadioCheck CreateReal(ILog? log = null, TimeSpan? budget = null) => new(new WinRtRadioApi(), new CfgMgr32DevNodeStatusReader(), budget, log);

    public async Task<RadioCheckResult> CheckAsync(CancellationToken ct)
    {
        RadioCheckResult result = await CheckCoreAsync(ct).ConfigureAwait(false);
        _log?.Info("radio check: " + result.State + " in " + result.ElapsedMs.ToString(CultureInfo.InvariantCulture) + " ms." + StepOutcomes.DescribeFailures(result.ReadCodes));
        return result;
    }

    // The end of a read that ran past the budget: a fault with its type and HRESULT, or the steps of a late reading that
    // failed. The card has already said Unknown; this is for the log only.
    private void LogLate(Task<AdapterReading> read)
    {
        if (read.IsFaulted)
        {
            Exception ex = read.Exception!.GetBaseException();
            _log?.Warn("radio check: the read ended after the time limit with " + ex.GetType().Name + " (HRESULT 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture) + ").");
        }
        else if (read.IsCanceled)
        {
            _log?.Info("radio check: the read was cancelled after the time limit.");
        }
        else
        {
            IEnumerable<string> failed = read.Result.Steps.Where(s => !s.Ok).Select(s => s.Step + " " + s.CodeName + " (" + s.Code.ToString(CultureInfo.InvariantCulture) + ")");
            _log?.Info("radio check: the read ended after the time limit: adapter " + (read.Result.Found ? "found" : "not found") + "; failed steps: " + (failed.Any() ? string.Join(", ", failed) : "none") + ".");
        }
    }

    private async Task<RadioCheckResult> CheckCoreAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<AdapterReading> read = _api.ReadAsync(cts.Token);

        // The budget's timer is its own source, cancelled as soon as either side ends, so a read that wins leaves no timer behind.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task finished = await Task.WhenAny(read, Task.Delay(_budget, budget.Token)).ConfigureAwait(false);
        await budget.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (finished != read)
        {
            // The WinRT read is abandoned, not waited for: the card must not hang behind it. What it ends with is logged.
            await cts.CancelAsync().ConfigureAwait(false);
            _ = read.ContinueWith(LogLate, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            var late = new[] { StepOutcomes.EarshotTimeout("radio-read", "The radio did not answer within " + _budget.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms.") };
            _log?.Warn("radio check: the read did not finish within Earshot's " + _budget.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms (" + StepOutcomes.EarshotTimeoutName + ").");
            return Result(RadioCheckState.Unknown, null, null, late, watch);
        }

        AdapterReading adapter;
        try
        {
            adapter = await read.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A read that threw something its own catches did not take (E_ACCESSDENIED, say): Unknown, with its code, rather than
            // an exception that would lose the card after a failed connect.
            return Result(RadioCheckState.Unknown, null, null, [StepOutcomes.FromHResult("radio-read", ex.HResult, ex.GetType().Name, ok: false)], watch);
        }
        var codes = adapter.Steps.ToList();
        if (!adapter.Answered)
        {
            // The adapter could not be asked, which is not the same as no adapter: nothing is claimed, and the codes are kept.
            return Result(RadioCheckState.Unknown, adapter, null, codes, watch);
        }

        if (!adapter.Found)
        {
            return Result(RadioCheckState.NoAdapter, adapter, null, codes, watch);
        }

        // An adapter that does only Bluetooth Low Energy: the AirPods' sound needs Classic Bluetooth, and there is no setting to
        // change, so no button.
        if (!adapter.ClassicSupported)
        {
            return Result(RadioCheckState.NoClassic, adapter, null, codes, watch);
        }

        DevNodeStatusInfo? node = null;
        if (!string.IsNullOrEmpty(adapter.InstanceId))
        {
            node = _nodes.Read(adapter.InstanceId);
            codes.AddRange(node.Steps);
        }

        if (node is { HasProblem: true })
        {
            return Result(RadioCheckState.Problem, adapter, node, codes, watch);
        }

        RadioCheckState state = adapter.RadioState switch
        {
            RadioStateValues.Off => RadioCheckState.Off,
            RadioStateValues.Disabled => RadioCheckState.SwitchedOff,
            // On is Ok only when the node was read too: an unread node claims nothing.
            RadioStateValues.On when node is { Located: true } => RadioCheckState.Ok,
            _ => RadioCheckState.Unknown,
        };
        return Result(state, adapter, node, codes, watch);
    }

    private static RadioCheckResult Result(
        RadioCheckState state, AdapterReading? adapter, DevNodeStatusInfo? node, IReadOnlyList<StepOutcome> codes, Stopwatch watch)
    {
        int? problem = node is { Located: true } ? (int)node.ProblemCode : null;
        return new RadioCheckResult(
            state,
            RadioLines.For(state, problem),
            RadioLines.ActionFor(state),
            adapter is { Answered: true } ? adapter.Found : null,
            adapter is { Answered: true } ? adapter.ClassicSupported : null,
            adapter?.RadioState,
            node is { Located: true } ? node.Status : null,
            problem,
            node?.DriverVersion,
            node?.DriverDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            codes,
            watch.ElapsedMilliseconds);
    }
}

internal sealed class CfgMgr32DevNodeStatusReader : IDevNodeStatusReader
{
    public DevNodeStatusInfo Read(string instanceId) => CfgMgr32.ReadDevNodeStatus(instanceId);
}

// The real reads. Each WinRT call is its own step with its raw HRESULT; none of them changes anything.
internal sealed class WinRtRadioApi : IRadioApi
{
    private const string InstanceIdProperty = "System.Devices.DeviceInstanceId";

    public async Task<AdapterReading> ReadAsync(CancellationToken ct)
    {
        if (!FixPlatform.HasDeviceEnumeration)
        {
            return new AdapterReading(false, false, null, null, [StepOutcomes.NotAvailable("bluetooth-adapter", "Windows is older than 10.0.19041.")], Answered: false);
        }

        return await ReadGuardedAsync(ct).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static async Task<AdapterReading> ReadGuardedAsync(CancellationToken ct)
    {
        var steps = new List<StepOutcome>();
        BluetoothAdapter? adapter;
        try
        {
            adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(StepOutcomes.FromHResult("bluetooth-adapter", ex.HResult, ex.GetType().Name, ok: false));
            return new AdapterReading(false, false, null, null, steps, Answered: false);
        }

        if (adapter is null)
        {
            steps.Add(StepOutcomes.FromHResult("bluetooth-adapter", 0, "GetDefaultAsync returned no adapter.", ok: false));
            return new AdapterReading(false, false, null, null, steps);
        }

        // A read that fails claims nothing: the adapter is not called one without Classic Bluetooth, and the code is kept.
        bool classic = true;
        try
        {
            classic = adapter.IsClassicSupported;
            steps.Add(StepOutcomes.FromHResult("bluetooth-classic", 0, classic ? "supported" : "not supported", ok: classic));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(StepOutcomes.FromHResult("bluetooth-classic", ex.HResult, ex.GetType().Name, ok: false));
        }

        int? radioState = null;
        try
        {
            Radio? radio = await adapter.GetRadioAsync().AsTask(ct).ConfigureAwait(false);
            if (radio is null)
            {
                steps.Add(StepOutcomes.FromHResult("bluetooth-radio", 0, "GetRadioAsync returned no radio.", ok: false));
            }
            else
            {
                radioState = (int)radio.State;
                steps.Add(StepOutcomes.FromHResult("bluetooth-radio", 0, radio.State.ToString()));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(StepOutcomes.FromHResult("bluetooth-radio", ex.HResult, ex.GetType().Name, ok: false));
        }

        string? instanceId = null;
        try
        {
            DeviceInformation info = await DeviceInformation.CreateFromIdAsync(adapter.DeviceId, [InstanceIdProperty]).AsTask(ct).ConfigureAwait(false);
            instanceId = info.Properties.TryGetValue(InstanceIdProperty, out object? value) ? value as string : null;
            steps.Add(StepOutcomes.FromHResult("bluetooth-instance-id", 0, instanceId is null ? "no instance id" : "read", ok: instanceId is not null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            steps.Add(StepOutcomes.FromHResult("bluetooth-instance-id", ex.HResult, ex.GetType().Name, ok: false));
        }

        return new AdapterReading(true, classic, radioState, instanceId, steps);
    }
}
