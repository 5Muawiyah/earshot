using System.Globalization;
using Earshot.Contracts;

namespace Earshot.Widget;

// Step 1 of the battery set-up: listen for the window, find the one sender that is the owner's case (the
// strongest by median signal, three messages at least, ten decibels clear of the next), and set the signal
// threshold ten decibels under that sender's weakest message. Nothing is claimed here; the result is the
// evidence a set-up record keeps and the draft the claim is made from once the owner has answered step 2.
internal static class BatterySetupFlow
{
    private const int DocumentedValueCaptureLength = 9;

    private sealed class Sample(DateTimeOffset at, sbyte rssi, ProximityParseStatus status, byte? prefix, int length, string valueHex, ProximityMessage? message)
    {
        public DateTimeOffset At { get; } = at;

        public sbyte Rssi { get; } = rssi;

        public ProximityParseStatus Status { get; } = status;

        public byte? Prefix { get; } = prefix;

        public int Length { get; } = length;

        public string ValueHex { get; } = valueHex;

        public ProximityMessage? Message { get; } = message;
    }

    public static async Task<BatterySetupListen> ListenAsync(
        IAdvertisementSource source, TimeProvider time, TimeSpan window, ILog log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        DateTimeOffset started = time.GetUtcNow();
        if (source.State != AdvertisementSourceState.Started)
        {
            log.Warn("Battery set-up: the watcher is not running, so nothing was listened for.");
            return Failed(BatterySetupListenStatus.WatcherNotStarted, WidgetCopy.SetupBluetoothOff, started, started, 0, 0, []);
        }

        var bySender = new Dictionary<uint, List<Sample>>();
        long appleSections = 0;
        long proximityItems = 0;
        var gate = new Lock();

        void OnReceived(object? sender, AdvertisementSample sample)
        {
            if (sample.CompanyId != ProximityParser.AppleCompanyId)
            {
                return;
            }

            ProximityParse parse = ProximityParser.Parse(sample.CompanyId, sample.Data);
            bool holdsItem = TryFirstProximityValue(sample.Data, out ReadOnlySpan<byte> value);
            lock (gate)
            {
                appleSections++;
                proximityItems += parse.ProximityItems;
                if (!holdsItem)
                {
                    return;
                }

                // The documented form keeps its first nine bytes only (prefix to reserved): the sixteen
                // encrypted bytes are never copied. Any other form is kept whole, as read.
                ReadOnlySpan<byte> kept = parse.Status == ProximityParseStatus.Ok ? value[..DocumentedValueCaptureLength] : value;
                if (!bySender.TryGetValue(sample.SenderTag, out List<Sample>? list))
                {
                    list = [];
                    bySender[sample.SenderTag] = list;
                }

                list.Add(new Sample(
                    sample.Timestamp, sample.Rssi, parse.Status, value.Length > 0 ? value[0] : null, value.Length,
                    Convert.ToHexString(kept), parse.Message));
            }
        }

        // The watcher is watched too: Bluetooth going off, or the machine sleeping, stops it part way through the
        // window, and a window that heard nothing because it was deaf is not "Couldn't find your AirPods".
        using var stoppedWindow = CancellationTokenSource.CreateLinkedTokenSource(ct);
        AdvertisementSourceStopped? stopped = null;

        void OnStopped(object? sender, AdvertisementSourceStopped e)
        {
            stopped = e;
            stoppedWindow.Cancel();
        }

        log.Info("Battery set-up: listening for " + ((int)window.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s.");
        source.Received += OnReceived;
        source.Stopped += OnStopped;
        try
        {
            await Task.Delay(window, time, stoppedWindow.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && stopped is not null)
        {
            return WatcherStoppedWhileListening(stopped, started, time.GetUtcNow(), log);
        }
        catch (OperationCanceledException)
        {
            return Failed(BatterySetupListenStatus.Cancelled, string.Empty, started, time.GetUtcNow(), 0, 0, []);
        }
        finally
        {
            source.Received -= OnReceived;
            source.Stopped -= OnStopped;
        }

        // A watcher that ended without saying so (a state read is all that is left to go on) heard nothing for
        // some part of the window too.
        if (source.State != AdvertisementSourceState.Started)
        {
            return WatcherStoppedWhileListening(null, started, time.GetUtcNow(), log);
        }

        DateTimeOffset ended = time.GetUtcNow();
        Dictionary<uint, List<Sample>> senders;
        lock (gate)
        {
            senders = bySender.ToDictionary(p => p.Key, p => p.Value.ToList());
        }

        return Decide(senders, appleSections, proximityItems, started, ended, log);
    }

    // The window ended because the watcher stopped, with the raw code in the log.
    private static BatterySetupListen WatcherStoppedWhileListening(AdvertisementSourceStopped? stopped, DateTimeOffset started, DateTimeOffset ended, ILog log)
    {
        log.Warn(
            "Battery set-up: the watcher stopped while listening" +
            (stopped is null ? " (its state is no longer Started)" : ": " + stopped.ErrorName + " (" + stopped.ErrorCode.ToString(CultureInfo.InvariantCulture) + ")") +
            ", so nothing was claimed.");
        return Failed(BatterySetupListenStatus.WatcherNotStarted, WidgetCopy.SetupBluetoothOff, started, ended, 0, 0, []);
    }

    private static BatterySetupListen Decide(
        Dictionary<uint, List<Sample>> senders, long appleSections, long proximityItems,
        DateTimeOffset started, DateTimeOffset ended, ILog log)
    {
        var candidates = senders.Where(p => p.Value.Count >= SetupRules.MinMessages)
            .Select(p => (Tag: p.Key, Samples: p.Value, Median: Median(p.Value)))
            .OrderByDescending(c => c.Median)
            .ToList();

        if (candidates.Count == 0)
        {
            int below = senders.Count;
            log.Warn(
                "Battery set-up: no AirPods found in " + ((int)(ended - started).TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s (" +
                appleSections.ToString(CultureInfo.InvariantCulture) + " Apple sections, " + proximityItems.ToString(CultureInfo.InvariantCulture) +
                " proximity items, " + below.ToString(CultureInfo.InvariantCulture) + " senders below " +
                SetupRules.MinMessages.ToString(CultureInfo.InvariantCulture) + " messages).");
            return Failed(BatterySetupListenStatus.NotFound, WidgetCopy.SetupNotFound, started, ended, appleSections, proximityItems, Summaries(senders.Values));
        }

        var best = candidates[0];
        sbyte min = best.Samples.Min(s => s.Rssi);
        sbyte max = best.Samples.Max(s => s.Rssi);
        sbyte threshold = (sbyte)SetupRules.ThresholdFor(min);
        int okCount = best.Samples.Count(s => s.Status == ProximityParseStatus.Ok);
        var others = senders.Where(p => p.Key != best.Tag).Select(p => p.Value).ToList();
        int? runnerUpMedian = candidates.Count > 1 ? candidates[1].Median : null;

        log.Info(
            "Battery set-up: " + senders.Count.ToString(CultureInfo.InvariantCulture) + " senders; candidate " +
            best.Samples.Count.ToString(CultureInfo.InvariantCulture) + " messages (ok " + okCount.ToString(CultureInfo.InvariantCulture) +
            ", other " + (best.Samples.Count - okCount).ToString(CultureInfo.InvariantCulture) + "), signal min/median/max " +
            min.ToString(CultureInfo.InvariantCulture) + "/" + best.Median.ToString(CultureInfo.InvariantCulture) + "/" + max.ToString(CultureInfo.InvariantCulture) +
            " dBm, runner-up median " + (runnerUpMedian is int r ? r.ToString(CultureInfo.InvariantCulture) + " dBm" : "none") +
            "; threshold " + threshold.ToString(CultureInfo.InvariantCulture) + " dBm.");

        if (runnerUpMedian is int runner && best.Median - runner < SetupRules.SeparationDb)
        {
            log.Warn(
                "Battery set-up: two senders too close in signal (medians " + best.Median.ToString(CultureInfo.InvariantCulture) + " and " +
                runner.ToString(CultureInfo.InvariantCulture) + " dBm); nothing claimed.");
            return Failed(BatterySetupListenStatus.Ambiguous, WidgetCopy.SetupAmbiguous, started, ended, appleSections, proximityItems, Summaries(others));
        }

        // A stranger that would pass the run-time signal check right now makes the threshold useless: nothing
        // is claimed. Every other sender counts here, however few messages it sent.
        if (others.Any(o => o.Max(s => s.Rssi) >= threshold))
        {
            log.Warn(
                "Battery set-up: a sender clears the threshold (" + threshold.ToString(CultureInfo.InvariantCulture) +
                " dBm) beside the candidate; nothing claimed.");
            return Failed(BatterySetupListenStatus.Ambiguous, WidgetCopy.SetupAmbiguous, started, ended, appleSections, proximityItems, Summaries(others));
        }

        ProximityMessage? lastOk = best.Samples.LastOrDefault(s => s.Status == ProximityParseStatus.Ok)?.Message;
        var captures = best.Samples.Select(s => new BatterySetupCapture(s.At, s.Rssi, s.Prefix, s.Length, s.ValueHex)).ToList();
        var candidate = new BatterySetupCandidate(
            best.Samples.Count, okCount, best.Samples.Count - okCount, min, (sbyte)best.Median, max, threshold, captures, lastOk);
        IReadOnlyList<BatterySetupSenderSummary> summaries = Summaries(others);

        if (lastOk is null)
        {
            Sample first = best.Samples[0];
            log.Warn(
                "Battery set-up: the candidate sent only forms the parser does not read (prefix " +
                (first.Prefix is byte p ? p.ToString("X2", CultureInfo.InvariantCulture) : "none") + " length " + first.Length.ToString(CultureInfo.InvariantCulture) +
                " x" + best.Samples.Count.ToString(CultureInfo.InvariantCulture) + "); captures kept, nothing claimed.");
            return new BatterySetupListen(BatterySetupListenStatus.ShortFormOnly, string.Empty, candidate, summaries, started, ended, appleSections, proximityItems);
        }

        return new BatterySetupListen(BatterySetupListenStatus.Found, string.Empty, candidate, summaries, started, ended, appleSections, proximityItems);
    }

    private static BatterySetupListen Failed(
        BatterySetupListenStatus status, string message, DateTimeOffset started, DateTimeOffset ended,
        long appleSections, long proximityItems, IReadOnlyList<BatterySetupSenderSummary> others) =>
        new(status, message, Candidate: null, others, started, ended, appleSections, proximityItems);

    private static List<BatterySetupSenderSummary> Summaries(IEnumerable<List<Sample>> senders) =>
        senders.Select(s => new BatterySetupSenderSummary(
            s.Count, s.Count(x => x.Status == ProximityParseStatus.Ok), (sbyte)Median(s))).ToList();

    // The middle value of every sample's signal; the lower of the two for an even count. One spike from a
    // passing device cannot choose the candidate.
    private static int Median(List<Sample> samples)
    {
        sbyte[] sorted = samples.Select(s => s.Rssi).Order().ToArray();
        return sorted[(sorted.Length - 1) / 2];
    }

    // The first 0x07 item's value in a manufacturer-data section: whatever is there when the item runs past
    // the end of the data, since the record keeps what was read. False when no item of that type starts.
    private static bool TryFirstProximityValue(byte[] data, out ReadOnlySpan<byte> value)
    {
        int offset = 0;
        while (data.Length - offset >= 2)
        {
            byte type = data[offset];
            int length = data[offset + 1];
            int start = offset + 2;
            if (type == ProximityParser.ProximityType)
            {
                value = data.AsSpan(start, Math.Min(length, data.Length - start));
                return true;
            }

            offset = start + length;
        }

        value = default;
        return false;
    }
}
