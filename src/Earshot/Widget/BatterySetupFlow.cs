using System.Globalization;
using Earshot.Contracts;

namespace Earshot.Widget;

// Step 1 of the battery set-up: listen for the window, find the one set of AirPods that is the owner's (the
// strongest by median signal, three messages at least, ten decibels clear of the next set), and set the signal
// threshold ten decibels under that set's weakest message. Only the documented form can be a set: the buds of one
// set are two senders and are merged (SetupSenderGroups), and a sender of any other form is never a candidate or a
// competitor, only evidence. Nothing is claimed here; the result is the evidence a set-up record keeps and the draft
// the claim is made from once the owner has answered step 2.
internal static class BatterySetupFlow
{
    private const int DocumentedValueCaptureLength = 9;

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

        var bySender = new Dictionary<uint, List<SetupSample>>();
        long sequence = 0;
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
                if (!bySender.TryGetValue(sample.SenderTag, out List<SetupSample>? list))
                {
                    list = [];
                    bySender[sample.SenderTag] = list;
                }

                list.Add(new SetupSample(
                    sequence++, sample.SenderTag, sample.Timestamp, sample.Rssi, parse.Status, value.Length > 0 ? value[0] : null, value.Length,
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
        List<(uint Tag, List<SetupSample> Samples)> senders;
        lock (gate)
        {
            // In the order each sender was first heard.
            senders = bySender.Select(p => (p.Key, p.Value.ToList())).OrderBy(p => p.Item2[0].Sequence).ToList();
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
        List<(uint Tag, List<SetupSample> Samples)> senders, long appleSections, long proximityItems,
        DateTimeOffset started, DateTimeOffset ended, ILog log)
    {
        // A documented-form sender is one that sent at least one message of the documented form; those messages are
        // what it contributes. Every other message, whoever sent it, is evidence of a form nobody decodes.
        var documented = senders
            .Select(s => (s.Tag, Samples: s.Samples.Where(x => x.Status == ProximityParseStatus.Ok).ToList()))
            .Where(s => s.Samples.Count > 0)
            .ToList();
        List<SetupSample> otherForm = senders.SelectMany(s => s.Samples).Where(x => x.Status != ProximityParseStatus.Ok).OrderBy(x => x.Sequence).ToList();
        int otherFormSenders = senders.Count(s => s.Samples.All(x => x.Status != ProximityParseStatus.Ok));

        List<SetupSenderGroup> groups = SetupSenderGroups.Merge(documented);
        var candidates = groups.Where(g => g.Samples.Count >= SetupRules.MinMessages)
            .Select(g => (Group: g, Median: Median(g.Samples)))
            .OrderByDescending(c => c.Median)
            .ToList();

        if (candidates.Count == 0)
        {
            log.Warn(
                "Battery set-up: no AirPods found in " + ((int)(ended - started).TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s (" +
                appleSections.ToString(CultureInfo.InvariantCulture) + " Apple sections, " + proximityItems.ToString(CultureInfo.InvariantCulture) +
                " proximity items, " + groups.Count.ToString(CultureInfo.InvariantCulture) + " senders below " +
                SetupRules.MinMessages.ToString(CultureInfo.InvariantCulture) + " messages, " +
                otherFormSenders.ToString(CultureInfo.InvariantCulture) + " other-form senders ignored).");
            return Failed(BatterySetupListenStatus.NotFound, WidgetCopy.SetupNotFound, started, ended, appleSections, proximityItems, Summaries(groups));
        }

        (SetupSenderGroup best, int bestMedian) = candidates[0];
        sbyte min = best.Samples.Min(s => s.Rssi);
        sbyte max = best.Samples.Max(s => s.Rssi);
        sbyte threshold = (sbyte)SetupRules.ThresholdFor(min);
        var others = groups.Where(g => !ReferenceEquals(g, best)).ToList();
        int? runnerUpMedian = candidates.Count > 1 ? candidates[1].Median : null;

        log.Info(
            "Battery set-up: " + senders.Count.ToString(CultureInfo.InvariantCulture) + " senders (" +
            documented.Count.ToString(CultureInfo.InvariantCulture) + " documented form in " + groups.Count.ToString(CultureInfo.InvariantCulture) + " sets, " +
            otherFormSenders.ToString(CultureInfo.InvariantCulture) + " other form ignored with " + otherForm.Count.ToString(CultureInfo.InvariantCulture) +
            " messages); candidate " + best.Samples.Count.ToString(CultureInfo.InvariantCulture) + " messages from " +
            best.Tags.Count.ToString(CultureInfo.InvariantCulture) + " senders, signal min/median/max " +
            min.ToString(CultureInfo.InvariantCulture) + "/" + bestMedian.ToString(CultureInfo.InvariantCulture) + "/" + max.ToString(CultureInfo.InvariantCulture) +
            " dBm, runner-up median " + (runnerUpMedian is int r ? r.ToString(CultureInfo.InvariantCulture) + " dBm" : "none") +
            "; threshold " + threshold.ToString(CultureInfo.InvariantCulture) + " dBm.");

        if (runnerUpMedian is int runner && bestMedian - runner < SetupRules.SeparationDb)
        {
            log.Warn(
                "Battery set-up: two senders too close in signal (medians " + bestMedian.ToString(CultureInfo.InvariantCulture) + " and " +
                runner.ToString(CultureInfo.InvariantCulture) + " dBm); nothing claimed.");
            return Failed(BatterySetupListenStatus.Ambiguous, WidgetCopy.SetupAmbiguous, started, ended, appleSections, proximityItems, Summaries(others));
        }

        // A stranger that would pass the run-time signal check right now makes the threshold useless: nothing
        // is claimed. Every other set counts here, however few messages it sent.
        if (others.Any(o => o.Samples.Max(s => s.Rssi) >= threshold))
        {
            log.Warn(
                "Battery set-up: a sender clears the threshold (" + threshold.ToString(CultureInfo.InvariantCulture) +
                " dBm) beside the candidate; nothing claimed.");
            return Failed(BatterySetupListenStatus.Ambiguous, WidgetCopy.SetupAmbiguous, started, ended, appleSections, proximityItems, Summaries(others));
        }

        ProximityMessage? lastOk = best.Samples[^1].Message;
        var captures = best.Samples.Select(Capture).ToList();
        var senderLast = new List<ProximityMessage>();
        foreach (uint tag in best.Tags)
        {
            SetupSample newest = best.Samples.Last(s => s.SenderTag == tag);
            senderLast.Add(newest.Message!.Value);
        }

        var shortForm = otherForm.Skip(Math.Max(0, otherForm.Count - SetupRules.MaxShortFormCaptures)).Select(Capture).ToList();
        var candidate = new BatterySetupCandidate(
            best.Samples.Count, best.Samples.Count, OtherFormMessages: 0, min, (sbyte)bestMedian, max, threshold, captures, lastOk,
            best.Tags.Select(t => t.ToString("X8", CultureInfo.InvariantCulture)).ToList(), senderLast, shortForm);

        return new BatterySetupListen(BatterySetupListenStatus.Found, string.Empty, candidate, Summaries(others), started, ended, appleSections, proximityItems);
    }

    private static BatterySetupCapture Capture(SetupSample s) =>
        new(s.At, s.Rssi, s.Prefix, s.Length, s.ValueHex, s.SenderTag.ToString("X8", CultureInfo.InvariantCulture));

    private static BatterySetupListen Failed(
        BatterySetupListenStatus status, string message, DateTimeOffset started, DateTimeOffset ended,
        long appleSections, long proximityItems, IReadOnlyList<BatterySetupSenderSummary> others) =>
        new(status, message, Candidate: null, others, started, ended, appleSections, proximityItems);

    private static List<BatterySetupSenderSummary> Summaries(IEnumerable<SetupSenderGroup> groups) =>
        groups.Select(g => new BatterySetupSenderSummary(
            g.Samples.Count, g.Samples.Count(x => x.Status == ProximityParseStatus.Ok), (sbyte)Median(g.Samples))).ToList();

    // The middle value of every sample's signal; the lower of the two for an even count. One spike from a
    // passing device cannot choose the candidate.
    private static int Median(IReadOnlyList<SetupSample> samples)
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
