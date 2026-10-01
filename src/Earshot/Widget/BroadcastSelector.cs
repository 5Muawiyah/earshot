namespace Earshot.Widget;

// What a documented-form message turned out to be, as far as the selection can tell.
internal enum BroadcastClass
{
    NoPairedModel,   // no paired AirPods model is known, so nothing can be selected
    ModelMismatch,   // another model than the paired AirPods'
    ColourMismatch,  // the paired model, but not the colour of the set already chosen
    Choosing,        // the paired model and colour, heard while the first choice is still waiting
    Chosen,          // a message of the chosen set: its values are the ones shown
    OtherSet,        // the paired model and colour, from a set that is not the chosen one
}

public enum BroadcastSelectionState { NoPairedModel, Listening, Chosen }

// What one message did to the selection.
//   NewChoice: the chosen set is not the set it was (a first choice, a switch, or a choice after a release), so
//   whatever values were kept belonged to another pair and are to be dropped.
//   Reacquired: every chosen sender had been silent for longer than the window (the case was closed, or the
//   addresses rotated), and a set was chosen again by the first-choice rule. The values on show are kept, greyed
//   as they age, until that set's own messages replace them; nothing is carried over as current.
//   Switched: another set took over from the chosen one.
internal readonly record struct SelectionObservation(
    BroadcastClass Class,
    bool NewChoice = false,
    bool Reacquired = false,
    bool Switched = false,
    int SetsInRange = 0)
{
    public bool IsChosenSet => Class == BroadcastClass.Chosen;
}

// Picks the owner's AirPods out of the documented-form messages in range, with no step from the owner:
//   - Candidates are messages of the paired AirPods' model (read from the paired device, never guessed) and, once
//     a colour is held, of that colour.
//   - One set can broadcast from two addresses, one per bud: BroadcastSenderSets merges them.
//   - The first choice is the set with the highest median signal over the window, once the model has been heard
//     for FirstChoiceAfter and the set has MinMessages in the window. Its colour is learned and held.
//   - The selection stands on its anchors: the tags that were in the set when the first-choice rule or a switch made
//     it the chosen one. The chosen set is the one set that holds the longest-standing anchor that sent inside the
//     window. A sender that merely merged into it (it said exactly what the set said, near it in time) is not an
//     anchor: it becomes one only after it has matched anchor messages for AnchorMatches messages over AnchorSpan, and
//     one message of it counts as the chosen set's only when that message matches an anchor's message within
//     SameSetWithin. A merged sender that then differs is another set, held to the next rule.
//   - Another set takes over only when its median is SwitchMarginDb above the chosen set's (taken over the anchors'
//     messages) for SwitchHold without a break; while the chosen set is silent its last median is held. Nothing else
//     makes a different set the chosen one while any anchor has sent inside the window, however equal its fields.
//   - Addresses rotate, and the case closes: when every anchor has been silent for longer than the window the
//     selection is acquired again by the first-choice rule among the sets of the paired model and the held colour.
//   - After ReleaseAfter with no message from an anchor the chosen set and the colour are dropped.
// Pure and single threaded: time comes from the messages' own timestamps, nothing here reads a clock, and
// nothing is written to disk.
internal sealed class BroadcastSelector
{
    private readonly Dictionary<uint, List<SenderMessage>> _senders = new();

    // The anchors, each with the order it became one in: the lowest order among the anchors that sent inside the
    // window says which set is the chosen one when a sender that had merged splits off again.
    private readonly Dictionary<uint, long> _anchors = new();

    // The senders that are in the chosen set's group without being anchors, and how long they have matched.
    private readonly Dictionary<uint, MergeProgress> _merged = new();
    private readonly Dictionary<uint, DateTimeOffset> _challengerSince = new();

    private ushort? _model;
    private byte? _colour;
    private DateTimeOffset? _firstHeardAt;
    private DateTimeOffset _latest = DateTimeOffset.MinValue;
    private DateTimeOffset? _chosenLastAt;
    private double? _chosenMedian;
    private long _sequence;
    private long _order;
    private int _setsInRange;
    private bool _reacquiring;

    public ushort? PairedModel => _model;

    // The colour learned from the chosen set, or null before the first choice.
    public byte? HeldColour => _colour;

    public int SetsInRange => _setsInRange;

    public bool HasChosen => _anchors.Count > 0;

    public BroadcastSelectionState StateAt(DateTimeOffset now)
    {
        if (_model is null)
        {
            return BroadcastSelectionState.NoPairedModel;
        }

        bool released = _chosenLastAt is { } last && now - last > BroadcastRules.ReleaseAfter;
        return HasChosen && !released ? BroadcastSelectionState.Chosen : BroadcastSelectionState.Listening;
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
        Reset();
        return true;
    }

    private void Reset()
    {
        _senders.Clear();
        _anchors.Clear();
        _merged.Clear();
        _challengerSince.Clear();
        _colour = null;
        _firstHeardAt = null;
        _chosenLastAt = null;
        _chosenMedian = null;
        _setsInRange = 0;
        _reacquiring = false;
    }

    public SelectionObservation Observe(ProximityMessage message, uint tag, sbyte rssi, DateTimeOffset at)
    {
        if (at > _latest)
        {
            _latest = at;
        }

        ReleaseIfStale();

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

        // Senders that went quiet leave the window first, so a model heard once and then not for a long while is
        // not "heard for two seconds" when it comes back.
        Prune();
        _firstHeardAt ??= at;
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

        BroadcastSet? chosen = null;
        if (HasChosen)
        {
            chosen = ChosenSetOf(sets);
            if (chosen is null)
            {
                // Every chosen sender has been silent for longer than the window: the case was closed, or the
                // addresses rotated. The values on show stay (they grey as they age); what is chosen is chosen again.
                BeginReacquire(at);
            }
        }

        return chosen is null ? Choose(sets, tag, at) : Follow(sets, chosen, own[^1], at);
    }

    // A first choice, a choice after a release, or a choice again after every chosen sender fell silent.
    private SelectionObservation Choose(List<BroadcastSet> sets, uint tag, DateTimeOffset at)
    {
        if (_firstHeardAt is not { } first || at - first < BroadcastRules.FirstChoiceAfter)
        {
            return new SelectionObservation(BroadcastClass.Choosing, SetsInRange: _setsInRange);
        }

        BroadcastSet? best = null;
        foreach (BroadcastSet set in sets)
        {
            if (set.MedianRssi is not double median)
            {
                continue;
            }

            if (best is null || median > best.MedianRssi || (median == best.MedianRssi && set.Messages.Count > best.Messages.Count))
            {
                best = set;
            }
        }

        if (best is null)
        {
            return new SelectionObservation(BroadcastClass.Choosing, SetsInRange: _setsInRange);
        }

        bool again = _reacquiring;
        _reacquiring = false;
        AdoptSet(best, replace: true);
        _colour ??= best.Newest.Message.Colour;
        _chosenMedian = best.MedianRssi;
        _challengerSince.Clear();
        return new SelectionObservation(
            _anchors.ContainsKey(tag) ? BroadcastClass.Chosen : BroadcastClass.OtherSet,
            NewChoice: !again, Reacquired: again, SetsInRange: _setsInRange);
    }

    private SelectionObservation Follow(List<BroadcastSet> sets, BroadcastSet chosen, SenderMessage message, DateTimeOffset at)
    {
        // The anchors are the chosen set's tags that were anchors already and no others: one that merged for a moment
        // and then differs is dropped, and a sender that joined the set is watched until it has matched long enough.
        FollowSet(chosen);
        bool chosenMessage = ClassifyMessage(message, at);
        if (AnchorMedian(chosen) is double median)
        {
            _chosenMedian = median;
        }

        if (EvaluateChallengers(sets, chosen, at, out BroadcastSet? winner) && winner is not null)
        {
            // The values the old set gave were another pair's. The colour is held across the switch.
            AdoptSet(winner, replace: true);
            _chosenMedian = winner.MedianRssi;
            _challengerSince.Clear();
            return new SelectionObservation(
                _anchors.ContainsKey(message.Tag) ? BroadcastClass.Chosen : BroadcastClass.OtherSet,
                NewChoice: true, Switched: true, SetsInRange: _setsInRange);
        }

        return new SelectionObservation(
            chosenMessage ? BroadcastClass.Chosen : BroadcastClass.OtherSet, SetsInRange: _setsInRange);
    }

    // Whether one message is the chosen set's. An anchor's always is. A sender that is only in the group is judged by
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

        // A sender outside the chosen set's group is not the chosen set's, whatever it says.
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
    // a sender that only merged does not move what the chosen set is compared with.
    private double? AnchorMedian(BroadcastSet set)
    {
        double[] sorted = set.Messages.Where(m => _anchors.ContainsKey(m.Tag)).Select(m => (double)m.Rssi).Order().ToArray();
        if (sorted.Length < BroadcastRules.MinMessages)
        {
            return null;
        }

        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private void BeginReacquire(DateTimeOffset at)
    {
        _anchors.Clear();
        _merged.Clear();
        _challengerSince.Clear();
        _chosenMedian = null;
        _firstHeardAt = at;
        _reacquiring = true;
    }

    // Updates the challengers' timers from this evaluation and returns the one that has been past the margin for
    // the whole hold, if any. A timer resets on any evaluation where its set has too few messages for a median
    // or is below the margin. The chosen set's own median is the held one while it is silent.
    private bool EvaluateChallengers(List<BroadcastSet> sets, BroadcastSet chosen, DateTimeOffset at, out BroadcastSet? winner)
    {
        winner = null;
        if (_chosenMedian is not double chosenMedian)
        {
            return false;
        }

        var qualifying = new List<(BroadcastSet Set, DateTimeOffset Since)>();
        var keep = new HashSet<uint>();
        foreach (BroadcastSet set in sets)
        {
            // Messages of another colour are not admitted once the colour is held, so one that is still in the window
            // from before leaves it within a window, long before a hold can end: no colour test is needed here.
            if (ReferenceEquals(set, chosen))
            {
                continue;
            }

            if (set.MedianRssi is double median && median - chosenMedian >= BroadcastRules.SwitchMarginDb)
            {
                DateTimeOffset since = at;
                foreach (uint t in set.Tags)
                {
                    if (_challengerSince.TryGetValue(t, out DateTimeOffset existing) && existing < since)
                    {
                        since = existing;
                    }
                }

                foreach (uint t in set.Tags)
                {
                    _challengerSince[t] = since;
                    keep.Add(t);
                }

                qualifying.Add((set, since));
            }
        }

        foreach (uint t in _challengerSince.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _challengerSince.Remove(t);
        }

        foreach ((BroadcastSet set, DateTimeOffset since) in qualifying.OrderByDescending(q => q.Set.MedianRssi))
        {
            if (at - since >= BroadcastRules.SwitchHold)
            {
                winner = set;
                return true;
            }
        }

        return false;
    }

    // Makes set the chosen one by the first-choice rule or a switch: all of its tags are anchors, in the order they
    // were first heard, and nothing of an earlier choice is kept.
    private void AdoptSet(BroadcastSet set, bool replace)
    {
        if (replace)
        {
            _anchors.Clear();
            _merged.Clear();
        }

        foreach (uint t in set.Tags)
        {
            if (!_anchors.ContainsKey(t))
            {
                _anchors[t] = ++_order;
            }
        }

        _chosenLastAt = set.Newest.At;
    }

    // The chosen set at a later message: the anchors that are still in the set stay, one that is not (it said something
    // else, so it is its own set now) is dropped, and a tag that is in the set and is no anchor is a merged sender, its
    // run of matches kept. The time of the newest anchor message is the chosen set's last.
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

    // How long a sender that is in the chosen set's group without being an anchor has matched an anchor's messages: how
    // many messages did, and when the first of the run did.
    private readonly record struct MergeProgress(int Matches, DateTimeOffset FirstAt);

    private void ReleaseIfStale()
    {
        if (_chosenLastAt is DateTimeOffset last && _latest - last > BroadcastRules.ReleaseAfter)
        {
            _anchors.Clear();
            _merged.Clear();
            _challengerSince.Clear();
            _chosenLastAt = null;
            _chosenMedian = null;
            _colour = null;
            _firstHeardAt = null;
            _reacquiring = false;
            _senders.Clear();
        }
    }

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

        if (_senders.Count == 0)
        {
            _firstHeardAt = null;
        }
    }

    // The newest message each other sender of the chosen set sent within the given time of at, for the caller's
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
