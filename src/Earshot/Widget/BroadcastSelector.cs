namespace Earshot.Widget;

// What a documented-form message turned out to be, as far as the selection can tell.
internal enum BroadcastClass
{
    NoPairedModel,   // no paired AirPods model is known, so nothing can be linked
    ModelMismatch,   // another model than the paired AirPods'
    ColourMismatch,  // the paired model, but not the colour of the set that is linked
    Choosing,        // the paired model and colour, heard while no set is linked
    Chosen,          // a message of the linked set: its values are the ones shown
    OtherSet,        // the paired model and colour, from a set that is not the linked one
}

public enum BroadcastSelectionState { NoPairedModel, Listening, Chosen }

// What one message did to the selection.
//   NewChoice: the linked set is not the set it was (a link made on a case open, a link moved to another pair that
//   opened its case nearer), so whatever values were kept belonged to another pair and are to be dropped.
//   Reacquired: every sender of the linked set had been silent for longer than the window (the addresses rotated), and
//   a set whose fields continue the linked set's last was followed. It is the same set under other addresses, so the
//   values on show stay.
//   Switched: another set took the link from the linked one (it comes with NewChoice).
//   Dropped: the linked set had been lost for longer than BroadcastRules.LostLimit before this message, so the link was
//   dropped and whatever was kept is to be cleared. The message itself is then judged as one with no link.
internal readonly record struct SelectionObservation(
    BroadcastClass Class,
    bool NewChoice = false,
    bool Reacquired = false,
    bool Switched = false,
    int SetsInRange = 0,
    bool Dropped = false)
{
    public bool IsChosenSet => Class == BroadcastClass.Chosen;
}

// Links the owner's AirPods to the broadcast, with no step from the owner beyond opening the case:
//   - Candidates are messages of the paired AirPods' model (read from the paired device, never guessed) and, once a
//     colour is held, of that colour.
//   - One set can broadcast from two addresses, one per bud: BroadcastSenderSets merges them.
//   - A set is linked when it opens its case near the PC: at least LinkMessages messages with a known case level inside
//     LinkWindow, the first and last LinkSpan apart, their median at least LinkThresholdDbm, and of the sets that
//     qualify the strongest. A pair that is worn, with the case level unknown, is never linked. The colour of the set is
//     learned and held.
//   - The link stands on its anchors: the tags that were in the set when it was linked. The linked set is the one set
//     that holds the longest-standing anchor that sent inside the window. A sender that merely merged into it (it said
//     exactly what the set said, near it in time) is not an anchor: it becomes one only after it has matched anchor
//     messages for AnchorMatches messages over AnchorSpan, and one message of it counts as the linked set's only when
//     that message matches an anchor's message within SameSetWithin. A merged sender that then differs is another set.
//   - Nothing but a case open makes a different set the linked one while any anchor has sent inside the window: a set that
//     opens its case at least SwitchMarginDb nearer than the linked set's median takes the link, which is how the owner
//     corrects a link made to somebody else's pair.
//   - Addresses rotate: when every anchor has been silent for longer than the window, a set whose fields continue the
//     linked set's last (BroadcastSenderSets.SameFields: model, colour, case level and the two bud levels as an unordered
//     pair), heard for ContinueAfter with MinMessages in the window and within ContinueWithin of the linked set's last
//     message, is followed under its new addresses. A set with other fields is never followed, however near. A case open
//     during the loss links as it does with no link at all.
//   - After LostLimit with no message from an anchor the link and the colour are dropped, and nothing is linked until the
//     next case open.
// Pure and single threaded: time comes from the messages' own timestamps and from the time handed to Expire, nothing here
// reads a clock, and nothing is written to disk: the link lives in memory only.
internal sealed class BroadcastSelector
{
    private readonly Dictionary<uint, List<SenderMessage>> _senders = new();

    // The anchors, each with the order it became one in: the lowest order among the anchors that sent inside the
    // window says which set is the linked one when a sender that had merged splits off again.
    private readonly Dictionary<uint, long> _anchors = new();

    // The senders that are in the linked set's group without being anchors, and how long they have matched.
    private readonly Dictionary<uint, MergeProgress> _merged = new();

    private ushort? _model;
    private byte? _colour;
    private DateTimeOffset _latest = DateTimeOffset.MinValue;
    private DateTimeOffset? _chosenLastAt;
    private ProximityMessage? _lastMessage;
    private double? _chosenMedian;
    private long _sequence;
    private long _order;
    private int _setsInRange;

    public ushort? PairedModel => _model;

    // The colour learned from the linked set, or null while nothing is linked.
    public byte? HeldColour => _colour;

    public int SetsInRange => _setsInRange;

    public bool HasChosen => _anchors.Count > 0;

    public BroadcastSelectionState StateAt(DateTimeOffset now)
    {
        if (_model is null)
        {
            return BroadcastSelectionState.NoPairedModel;
        }

        return HasChosen && !LostTooLong(now) ? BroadcastSelectionState.Chosen : BroadcastSelectionState.Listening;
    }

    private bool LostTooLong(DateTimeOffset now) => _chosenLastAt is { } last && now - last > BroadcastRules.LostLimit;

    // Drops the link when the linked set has not been heard under any address for longer than BroadcastRules.LostLimit as
    // of now. Returns true when it did, so the caller clears the values it kept. Called with each message's own time and
    // with the clock's by whatever watches for silence, since silence brings no message to notice it with.
    public bool Expire(DateTimeOffset now)
    {
        if (!HasChosen || !LostTooLong(now))
        {
            return false;
        }

        Unlink();
        return true;
    }

    // The paired AirPods' model, or null when it is not known. A different model starts the selection over; the
    // same one changes nothing. Returns true when the selection was reset, so the caller drops the values it kept.
    public bool SetPairedModel(ushort? model)
    {
        if (model == _model)
        {
            return false;
        }

        _model = model;
        Unlink();
        return true;
    }

    private void Unlink()
    {
        _senders.Clear();
        _anchors.Clear();
        _merged.Clear();
        _colour = null;
        _chosenLastAt = null;
        _lastMessage = null;
        _chosenMedian = null;
        _setsInRange = 0;
    }

    public SelectionObservation Observe(ProximityMessage message, uint tag, sbyte rssi, DateTimeOffset at)
    {
        if (at > _latest)
        {
            _latest = at;
        }

        bool dropped = Expire(_latest);
        SelectionObservation seen = Judge(message, tag, rssi, at);
        return dropped ? seen with { Dropped = true } : seen;
    }

    private SelectionObservation Judge(ProximityMessage message, uint tag, sbyte rssi, DateTimeOffset at)
    {
        if (_model is not ushort model)
        {
            return new SelectionObservation(BroadcastClass.NoPairedModel);
        }

        if (message.Model != model)
        {
            return new SelectionObservation(BroadcastClass.ModelMismatch);
        }

        if (_colour is byte held && message.Colour != held)
        {
            return new SelectionObservation(BroadcastClass.ColourMismatch);
        }

        Prune();
        if (!_senders.TryGetValue(tag, out List<SenderMessage>? own))
        {
            if (_senders.Count >= BroadcastRules.MaxSenders)
            {
                return new SelectionObservation(BroadcastClass.OtherSet, SetsInRange: _setsInRange);
            }

            own = new List<SenderMessage>();
            _senders[tag] = own;
        }

        own.Add(new SenderMessage(++_sequence, tag, at, rssi, message));
        if (own.Count > BroadcastRules.MaxMessagesPerSender)
        {
            own.RemoveAt(0);
        }

        Prune();
        List<BroadcastSet> sets = BroadcastSenderSets.Compute(_senders.Select(s => (s.Key, s.Value)).ToList());
        _setsInRange = sets.Count;

        if (!HasChosen)
        {
            return TryLink(sets, tag, message, at, exclude: null, overMedian: null)
                ?? new SelectionObservation(BroadcastClass.Choosing, SetsInRange: _setsInRange);
        }

        BroadcastSet? chosen = ChosenSetOf(sets);
        if (chosen is not null)
        {
            return Follow(sets, chosen, own[^1], at);
        }

        // Every sender of the linked set has been silent for longer than the window: the addresses rotated, or the
        // case closed. The values on show stay (they grey as they age). A set that continues the last fields is the same
        // set under new addresses; a case open links as it would with no link at all; anything else is not the owner's.
        return TryContinue(sets, tag, at)
            ?? TryLink(sets, tag, message, at, exclude: null, overMedian: null)
            ?? new SelectionObservation(BroadcastClass.OtherSet, SetsInRange: _setsInRange);
    }

    // A link made on a case open: of the sets that opened their case near the PC, the strongest. A message with no case
    // level cannot complete a link, so only one that carries a level does the work. overMedian, when given, is the median a
    // set has to beat by BroadcastRules.SwitchMarginDb to take the link from the set that holds it.
    private SelectionObservation? TryLink(
        List<BroadcastSet> sets, uint tag, ProximityMessage message, DateTimeOffset at, BroadcastSet? exclude, double? overMedian)
    {
        if (!CaseKnown(message))
        {
            return null;
        }

        (BroadcastSet Set, double Median)? best = null;
        foreach (BroadcastSet set in sets)
        {
            if (ReferenceEquals(set, exclude) || LinkMedian(set, at) is not double median)
            {
                continue;
            }

            if (best is null || median > best.Value.Median || (median == best.Value.Median && set.Messages.Count > best.Value.Set.Messages.Count))
            {
                best = (set, median);
            }
        }

        if (best is not { } winner || (overMedian is double held && winner.Median - held < BroadcastRules.SwitchMarginDb))
        {
            return null;
        }

        bool switched = overMedian is not null;
        AdoptSet(winner.Set);
        _colour = winner.Set.Newest.Message.Colour;
        _chosenMedian = winner.Median;
        return new SelectionObservation(
            _anchors.ContainsKey(tag) ? BroadcastClass.Chosen : BroadcastClass.OtherSet,
            NewChoice: true, Switched: switched, SetsInRange: _setsInRange);
    }

    // The median signal over a set's case-known messages in the link window, or null when the set has not opened its
    // case near the PC: fewer than BroadcastRules.LinkMessages of them, spanning less than BroadcastRules.LinkSpan, or a
    // median under BroadcastRules.LinkThresholdDbm.
    private static double? LinkMedian(BroadcastSet set, DateTimeOffset at)
    {
        var rssi = new List<double>();
        DateTimeOffset first = DateTimeOffset.MaxValue;
        DateTimeOffset last = DateTimeOffset.MinValue;
        foreach (SenderMessage m in set.Messages)
        {
            if (at - m.At > BroadcastRules.LinkWindow || !CaseKnown(m.Message))
            {
                continue;
            }

            rssi.Add(m.Rssi);
            first = m.At < first ? m.At : first;
            last = m.At > last ? m.At : last;
        }

        if (rssi.Count < BroadcastRules.LinkMessages || last - first < BroadcastRules.LinkSpan)
        {
            return null;
        }

        double median = Median(rssi);
        return median >= BroadcastRules.LinkThresholdDbm ? median : null;
    }

    // A message whose case nibble is a level. 0xF, and 11 to 14, are not.
    private static bool CaseKnown(ProximityMessage message) => BatteryNibble.ToPercent(message.BatteryB & 0x0F) is not null;

    private static double Median(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    // The linked set under new addresses: a set that says what the linked set last said, has been heard for a little and
    // is heard within ContinueWithin of the linked set's last message. The strongest if more than one. The values stay.
    private SelectionObservation? TryContinue(List<BroadcastSet> sets, uint tag, DateTimeOffset at)
    {
        if (_chosenLastAt is not { } last || _lastMessage is not { } fields || at - last > BroadcastRules.ContinueWithin)
        {
            return null;
        }

        BroadcastSet? best = null;
        foreach (BroadcastSet set in sets)
        {
            if (set.Messages.Count < BroadcastRules.MinMessages ||
                set.Newest.At - set.Messages[0].At < BroadcastRules.ContinueAfter ||
                !set.Messages.Any(m => BroadcastSenderSets.SameFields(m.Message, fields)))
            {
                continue;
            }

            if (best is null || set.MedianRssi > best.MedianRssi || (set.MedianRssi == best.MedianRssi && set.Messages.Count > best.Messages.Count))
            {
                best = set;
            }
        }

        if (best is null)
        {
            return null;
        }

        AdoptSet(best);
        _chosenMedian = best.MedianRssi;
        return new SelectionObservation(
            _anchors.ContainsKey(tag) ? BroadcastClass.Chosen : BroadcastClass.OtherSet,
            Reacquired: true, SetsInRange: _setsInRange);
    }

    private SelectionObservation Follow(List<BroadcastSet> sets, BroadcastSet chosen, SenderMessage message, DateTimeOffset at)
    {
        // The anchors are the linked set's tags that were anchors already and no others: one that merged for a moment
        // and then differs is dropped, and a sender that joined the set is watched until it has matched long enough.
        FollowSet(chosen);
        bool chosenMessage = ClassifyMessage(message, at);
        if (chosenMessage)
        {
            _lastMessage = message.Message;
        }

        if (AnchorMedian(chosen) is double median)
        {
            _chosenMedian = median;
        }

        // Another pair that opens its case clearly nearer takes the link. Nothing else does, however near and however
        // equal its fields.
        if (TryLink(sets, message.Tag, message.Message, at, exclude: chosen, overMedian: _chosenMedian ?? double.NegativeInfinity) is { } switched)
        {
            return switched;
        }

        return new SelectionObservation(
            chosenMessage ? BroadcastClass.Chosen : BroadcastClass.OtherSet, SetsInRange: _setsInRange);
    }

    // Whether one message is the linked set's. An anchor's always is. A sender that is only in the group is judged by
    // the message itself: it counts when it says what an anchor said within SameSetWithin of it, and the senders that
    // go on matching become anchors. A message that does not match resets that sender's run of matches.
    private bool ClassifyMessage(SenderMessage message, DateTimeOffset at)
    {
        if (_anchors.ContainsKey(message.Tag))
        {
            return true;
        }

        bool matches = false;
        foreach (uint anchor in _anchors.Keys)
        {
            if (!_senders.TryGetValue(anchor, out List<SenderMessage>? list))
            {
                continue;
            }

            foreach (SenderMessage seen in list)
            {
                if (BroadcastSenderSets.SameSet(message, seen))
                {
                    matches = true;
                    break;
                }
            }

            if (matches)
            {
                break;
            }
        }

        // A sender outside the linked set's group is not the linked set's, whatever it says.
        if (!_merged.TryGetValue(message.Tag, out MergeProgress progress))
        {
            return false;
        }

        if (!matches)
        {
            _merged[message.Tag] = default;
            return false;
        }

        progress = progress.Matches == 0 ? new MergeProgress(1, at) : progress with { Matches = progress.Matches + 1 };
        if (progress.Matches >= BroadcastRules.AnchorMatches && at - progress.FirstAt >= BroadcastRules.AnchorSpan)
        {
            _anchors[message.Tag] = ++_order;
            _merged.Remove(message.Tag);
        }
        else
        {
            _merged[message.Tag] = progress;
        }

        return true;
    }

    // The set that holds the longest-standing anchor among those that sent inside the window, or null when none
    // of them did.
    private BroadcastSet? ChosenSetOf(List<BroadcastSet> sets)
    {
        BroadcastSet? found = null;
        long foundOrder = long.MaxValue;
        foreach (BroadcastSet set in sets)
        {
            foreach (uint t in set.Tags)
            {
                if (_anchors.TryGetValue(t, out long order) && order < foundOrder)
                {
                    found = set;
                    foundOrder = order;
                }
            }
        }

        return found;
    }

    // The median signal over the anchors' own messages in the set, or null with fewer than BroadcastRules.MinMessages:
    // a sender that only merged does not move what the linked set is compared with.
    private double? AnchorMedian(BroadcastSet set)
    {
        var anchored = set.Messages.Where(m => _anchors.ContainsKey(m.Tag)).Select(m => (double)m.Rssi).ToList();
        return anchored.Count < BroadcastRules.MinMessages ? null : Median(anchored);
    }

    // Makes set the linked one: all of its tags are anchors, in the order they were first heard, and nothing of an
    // earlier link is kept.
    private void AdoptSet(BroadcastSet set)
    {
        _anchors.Clear();
        _merged.Clear();
        foreach (uint t in set.Tags)
        {
            _anchors[t] = ++_order;
        }

        _chosenLastAt = set.Newest.At;
        _lastMessage = set.Newest.Message;
    }

    // The linked set at a later message: the anchors that are still in the set stay, one that is not (it said something
    // else, so it is its own set now) is dropped, and a tag that is in the set and is no anchor is a merged sender, its
    // run of matches kept. The time of the newest anchor message is the linked set's last.
    private void FollowSet(BroadcastSet set)
    {
        foreach (uint t in _anchors.Keys.Where(k => !set.Tags.Contains(k)).ToList())
        {
            _anchors.Remove(t);
        }

        foreach (uint t in _merged.Keys.Where(k => !set.Tags.Contains(k)).ToList())
        {
            _merged.Remove(t);
        }

        foreach (uint t in set.Tags)
        {
            if (!_anchors.ContainsKey(t) && !_merged.ContainsKey(t))
            {
                _merged[t] = default;
            }
        }

        DateTimeOffset? newest = null;
        foreach (SenderMessage m in set.Messages)
        {
            if (_anchors.ContainsKey(m.Tag) && (newest is null || m.At > newest))
            {
                newest = m.At;
            }
        }

        if (newest is DateTimeOffset at && (_chosenLastAt is null || at >= _chosenLastAt))
        {
            _chosenLastAt = at;
        }
    }

    // How long a sender that is in the linked set's group without being an anchor has matched an anchor's messages: how
    // many messages did, and when the first of the run did.
    private readonly record struct MergeProgress(int Matches, DateTimeOffset FirstAt);

    private void Prune()
    {
        DateTimeOffset cutoff = _latest - BroadcastRules.Window;
        foreach (uint tag in _senders.Keys.ToList())
        {
            List<SenderMessage> list = _senders[tag];
            list.RemoveAll(m => m.At < cutoff);
            if (list.Count == 0)
            {
                _senders.Remove(tag);
            }
        }
    }

    // The newest message each other sender of the linked set sent within the given time of at, for the caller's
    // check that the two buds of a set agree on which side is which.
    public IReadOnlyList<SenderMessage> OtherChosenSenders(uint tag, DateTimeOffset at, TimeSpan within)
    {
        var result = new List<SenderMessage>();
        foreach (uint other in _anchors.Keys)
        {
            if (other == tag || !_senders.TryGetValue(other, out List<SenderMessage>? list) || list.Count == 0)
            {
                continue;
            }

            SenderMessage newest = list[^1];
            if ((at - newest.At).Duration() <= within)
            {
                result.Add(newest);
            }
        }

        return result;
    }
}
