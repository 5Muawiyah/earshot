using Earshot.Contracts;

namespace Earshot.Widget;

public enum ClaimOutcomeStatus { Claimed, NoThreshold, WatcherNotStarted, NoSender, MultipleSenders }

// British English, plain, short, no em-dash: shown to the owner as it stands.
public sealed record ClaimOutcome(ClaimOutcomeStatus Status, string Message, WidgetClaim? Claim);

// One claiming run, started by the owner from the UI with his case open next to the PC. Collects every
// Ok-form message at or above the signal threshold for the window, grouped by the source's per-run sender
// tag, and claims the single sender if there is exactly one.
internal sealed class ClaimFlow
{
    private readonly ClaimStore _store;
    private readonly ILog _log;

    public ClaimFlow(ClaimStore store, ILog log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        _store = store;
        _log = log;
    }

    // Reads the phase 0 constants directly, so nothing here can claim before phase 0 has proved a signal
    // threshold: WidgetDefaults.SignalThresholdDbm ships null, and every claiming run refuses until it does not.
    public Task<ClaimOutcome> RunAsync(IAdvertisementSource source, TimeProvider timeProvider, TimeSpan window, CancellationToken ct) =>
        RunAsync(source, timeProvider, window, WidgetDefaults.SignalThresholdDbm, ProximityDecodeTable.Current, ct);

    // The same flow with the threshold and decode table supplied directly, so a test can exercise the
    // "phase 0 has proved a threshold" path without WidgetDefaults or ProximityDecodeTable.Current ever
    // holding anything but the null, unproved defaults they ship with.
    internal async Task<ClaimOutcome> RunAsync(
        IAdvertisementSource source, TimeProvider timeProvider, TimeSpan window, sbyte? signalThresholdDbm, ProximityDecodeTable table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(table);

        if (signalThresholdDbm is not sbyte threshold)
        {
            return Refuse(ClaimOutcomeStatus.NoThreshold, "Phase 0 has not set the signal threshold.");
        }

        if (source.State != AdvertisementSourceState.Started)
        {
            return Refuse(ClaimOutcomeStatus.WatcherNotStarted, "Bluetooth is off or the watcher stopped: see the log.");
        }

        var bySender = new Dictionary<uint, ProximityMessage>();

        void OnReceived(object? sender, AdvertisementSample sample)
        {
            if (sample.Rssi < threshold)
            {
                return;
            }

            ProximityParse parse = ProximityParser.Parse(sample.CompanyId, sample.Data);
            if (parse.Status != ProximityParseStatus.Ok || parse.Message is not ProximityMessage message)
            {
                return;
            }

            lock (bySender)
            {
                // The last message from a sender within the window is what the claim is made from.
                bySender[sample.SenderTag] = message;
            }
        }

        source.Received += OnReceived;
        try
        {
            await Task.Delay(window, timeProvider, ct).ConfigureAwait(false);
        }
        finally
        {
            source.Received -= OnReceived;
        }

        List<ProximityMessage> candidates;
        lock (bySender)
        {
            candidates = bySender.Values.Where(m => Qualifies(m, table)).ToList();
        }

        if (candidates.Count == 0)
        {
            return Refuse(ClaimOutcomeStatus.NoSender, "No AirPods seen. Open the case next to the PC and try again.");
        }

        if (candidates.Count > 1)
        {
            return Refuse(ClaimOutcomeStatus.MultipleSenders, "More than one set of AirPods is near. Try again away from others.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        ProximityMessage message2 = candidates[0];
        var claim = new WidgetClaim(
            SchemaVersion: 1,
            ModelHigh: message2.ModelHigh,
            ModelLow: message2.ModelLow,
            Colour: message2.Colour,
            SignalThresholdDbm: threshold,
            ClaimedAtUtc: now,
            Last: OwnedBattery.FromMessage(message2, table, previous: null, at: now));

        _store.Save(claim);
        _log.Info("AirPods claimed: model and colour recorded, signal threshold " + threshold + " dBm.");
        return new ClaimOutcome(ClaimOutcomeStatus.Claimed, "AirPods claimed.", claim);
    }

    // Once the paper's claim about the case nibble is proved, a candidate also needs a readable case
    // nibble, since the owner was told to open the lid: a closed stranger then cannot be claimed.
    private static bool Qualifies(ProximityMessage message, ProximityDecodeTable table)
    {
        if (table.CaseNibbleReadsOnlyWithLidOpen != true)
        {
            return true;
        }

        return BatteryNibble.ToPercent(message.BatteryB & 0x0F) is not null;
    }

    private ClaimOutcome Refuse(ClaimOutcomeStatus status, string message)
    {
        _log.Warn("Claim not made: " + message);
        return new ClaimOutcome(status, message, null);
    }
}
