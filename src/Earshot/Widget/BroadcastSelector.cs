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
//   - The chosen set stays chosen, and at each message its tags are exactly the tags of the one set that holds the
//     longest-standing chosen sender. A sender that merged into the set for a moment (equal fields heard together)
//     and then differs is its own set again, and a set of its own is held to the next rule.
//   - Another set takes over only when its median is SwitchMarginDb above the chosen set's for SwitchHold without a
//     break; while the chosen set is silent its last median is held. Nothing else makes a different set the chosen
//     one while any chosen sender has sent inside the window, however equal its fields.
//   - Addresses rotate, and the case closes: when every chosen sender has been silent for longer than the window the
//     selection is acquired again by the first-choice rule among the sets of the paired model and the held colour.
//   - After ReleaseAfter with no message from the chosen set it and the colour are dropped.
// Pure and single threaded: time comes from the messages' own timestamps, nothing here reads a clock, and
// nothing is written to disk.
internal sealed class BroadcastSelector
{
    private readonly Dictionary<uint, List<SenderMessage>> _senders = new();

    // The chosen tags, each with the order it became a chosen tag in: the lowest order among the tags that sent
    // inside the window anchors which set is the chosen one when a sender that had merged splits off again.
    private readonly Dictionary<uint, long> _chosenOrder = new();
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

    public bool HasChosen => _chosenOrder.Count > 0;

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
        _chosenOrder.Clear();
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

        return chosen is null ? Choose(sets, tag, at) : Follow(sets, chosen, tag, at);
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
            _chosenOrder.ContainsKey(tag) ? BroadcastClass.Chosen : BroadcastClass.OtherSet,
            NewChoice: !again, Reacquired: again, SetsInRange: _setsInRange);
    }

    private SelectionObservation Follow(List<BroadcastSet> sets, BroadcastSet chosen, uint tag, DateTimeOffset at)
    {
        // The chosen tags are the tags of the chosen set and no others: a second sender of the set (the other bud)
        // joins here, and one that merged for a moment and then differs is dropped.
        AdoptSet(chosen);
        if (chosen.MedianRssi is double median)
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
                _chosenOrder.ContainsKey(tag) ? BroadcastClass.Chosen : BroadcastClass.OtherSet,
                NewChoice: true, Switched: true, SetsInRange: _setsInRange);
        }

        return new SelectionObservation(
            _chosenOrder.ContainsKey(tag) ? BroadcastClass.Chosen : BroadcastClass.OtherSet, SetsInRange: _setsInRange);
    }

    // The set that holds the longest-standing chosen tag among the senders that sent inside the window, or null when
    // none of them did.
    private BroadcastSet? ChosenSetOf(List<BroadcastSet> sets)
    {
        BroadcastSet? found = null;
        long foundOrder = long.MaxValue;
        foreach (BroadcastSet set in sets)
        {
            foreach (uint t in set.Tags)
            {
                if (_chosenOrder.TryGetValue(t, out long order) && order < foundOrder)
                {
                    found = set;
                    foundOrder = order;
                }
            }
        }

        return found;
    }

    private void BeginReacquire(DateTimeOffset at)
    {
        _chosenOrder.Clear();
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

    // Makes set the chosen one: its tags are the chosen tags (a tag already chosen keeps its order, a new one takes
    // the next, one that is not in the set is dropped), and the time of its newest message is the chosen set's last.
    private void AdoptSet(BroadcastSet set, bool replace = false)
    {
        if (replace)
        {
            _chosenOrder.Clear();
        }
        else
        {
            foreach (uint t in _chosenOrder.Keys.Where(k => !set.Tags.Contains(k)).ToList())
            {
                _chosenOrder.Remove(t);
            }
        }

        foreach (uint t in set.Tags)
        {
            if (!_chosenOrder.ContainsKey(t))
            {
                _chosenOrder[t] = ++_order;
            }
        }

        DateTimeOffset newest = set.Newest.At;
        if (_chosenLastAt is null || newest >= _chosenLastAt || replace)
        {
            _chosenLastAt = newest;
        }
    }

    private void ReleaseIfStale()
    {
        if (_chosenLastAt is DateTimeOffset last && _latest - last > BroadcastRules.ReleaseAfter)
        {
            _chosenOrder.Clear();
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
        foreach (uint other in _chosenOrder.Keys)
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
