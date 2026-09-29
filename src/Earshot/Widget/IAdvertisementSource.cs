using Earshot.Contracts;

namespace Earshot.Widget;

public readonly record struct AdvertisementSample(
    ushort CompanyId, byte[] Data, sbyte Rssi, DateTimeOffset Timestamp, uint SenderTag);

public enum AdvertisementSourceState { Created, Started, Stopped, Aborted }

// Generation is whatever the caller last passed into Start(), for whichever run is currently live: it lets
// a caller tell a Stopped event that belongs to the run it currently believes is live apart from a late one
// that still belongs to a run already superseded by a later Start (Stop then an immediate Start, most
// often, where the native watcher's own Stopped(Success) for the old run can arrive after the new one is
// already under way). The source never invents this number itself, so a fresh instance (built after the
// setting goes off then on) never has to coordinate its own counting with any earlier instance's.
public sealed record AdvertisementSourceStopped(int ErrorCode, string ErrorName, StepOutcome Step, int Generation);

// What a source's Start reports when Bluetooth is switched off. The real watcher does not always raise
// Stopped with RadioNotAvailable for this: with the radio off, Start itself throws a COMException whose
// HRESULT is HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_AVAILABLE), seen on Windows 11 with the radio off. The
// step carries that raw code so the log keeps it, and the service shows the same state it shows for
// RadioNotAvailable (Stopped, that error code and name, the same retry schedule).
// https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--4000-5999-
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetootherror
internal static class AdvertisementSourceCodes
{
    public const int RadioOffHResult = unchecked((int)0x800710DF);

    // Named as winerror.h names it: ERROR_DEVICE_NOT_AVAILABLE, 4319.
    public const string RadioOffCodeName = "ERROR_DEVICE_NOT_AVAILABLE";

    // BluetoothError.RadioNotAvailable, the value and name a Stopped event carries when the radio is off, so
    // the state shown for a Start that threw is the state shown for a Stopped that arrived.
    public const int RadioNotAvailableCode = 1;
    public const string RadioNotAvailableName = "RadioNotAvailable";

    public static bool IsRadioOff(StepOutcome step) => !step.Ok && step.Code == RadioOffHResult;

    // The step for a Start that failed because Bluetooth is off; the raw code is in the detail as well as
    // the step, so every log line that prints only the detail still carries it.
    public static StepOutcome RadioOff(string step) =>
        new(step, Ok: false, RadioOffHResult, RadioOffCodeName, "Bluetooth is off, raw code 0x800710DF");
}

// The watcher, seen from the rest of the widget: passive, unfiltered, never connects, pairs or touches a
// device node. The real implementation is WinRtAdvertisementSource; tests use a fake.
internal interface IAdvertisementSource : IDisposable
{
    AdvertisementSourceState State { get; }

    // generation is the caller's own label for the run this call is starting, echoed back on Stopped: see
    // AdvertisementSourceStopped's comment. Never throws for anything Windows did; the step carries the raw
    // code.
    StepOutcome Start(int generation);

    StepOutcome Stop();

    event EventHandler<AdvertisementSample>? Received;   // whatever thread Windows calls back on

    event EventHandler<AdvertisementSourceStopped>? Stopped;
}
