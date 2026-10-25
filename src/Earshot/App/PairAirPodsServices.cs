using Earshot.Bluetooth;
using Earshot.Boot;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Pairing;
using Earshot.Repair;

namespace Earshot.App;

// What the tray's Pair AirPods runs on: the pairing API, the node reader, the on-screen question, the window it shows while it
// runs, the radio check and the lock that keeps a Fix and a pairing from overlapping. The real ones come from Program.Tray; a
// test gives fakes, so a tray-level test never pairs, scans or shows a window. A tray started without them (a test, nothing
// else) says Pair AirPods is not available.
internal sealed class PairAirPodsServices
{
    public required IPairingApi Api { get; init; }

    public required INodeReader Nodes { get; init; }

    // The Look for AirPods question. The pick and the pairing numbers are asked by the window's own dialogs.
    public required IFixPrompter Prompter { get; init; }

    // The window for one run; the cancel source is cancelled when the person presses Cancel in it.
    public required Func<CancellationTokenSource, IFixRePairUi> UiFactory { get; init; }

    public IRadioCheck? Radio { get; init; }

    public FixOptions Options { get; init; } = new();

    // The named lock of FixRunLock; a test gives a name of its own.
    public required string LockName { get; init; }

    public static PairAirPodsServices CreateReal(Paths paths, ILog log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        return new PairAirPodsServices
        {
            Api = new WinRtPairingApi(),
            Nodes = new CfgMgr32NodeReader(),
            Prompter = new TaskDialogPrompter(),
            UiFactory = cancel => new FixRePairWindow(cancel, PairAirPodsText.Title),
            Radio = RadioCheck.CreateReal(log),
            LockName = FixRunLock.NameFor(paths),
        };
    }
}

// The words of the Pair AirPods screens that are not the pairing session's own (PairingText).
internal static class PairAirPodsText
{
    public const string Title = "Pair AirPods";

    public const string LookButton = "Look for AirPods";

    public const string NotAvailable = "Pair AirPods isn't available here.";

    public const string PairedAndUsing = "Paired. Earshot now uses these AirPods.";

    public const string PairedNotAdopted = "Paired, but Earshot could not switch to these AirPods. Choose them from the menu.";

    public const string NoAirPodsPaired = "No AirPods are paired with this PC.";

    public const string ChooseButton = "Choose AirPods";

    public const string StaleChosen = "Your AirPods were removed in Windows. Choose which pair Earshot should use.";

    // The first question, before anything is scanned: three lines, each its own step.
    public static string FirstQuestionBody =>
        PairingText.TurnOffIPhoneBluetooth + Environment.NewLine + Environment.NewLine +
        PairingText.CaseFront + Environment.NewLine + Environment.NewLine +
        PairingText.CaseBack;
}
