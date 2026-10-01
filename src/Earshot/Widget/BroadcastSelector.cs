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
//   Continued: the chosen set came back under new addresses (they rotate), so its values stand.
//   Switched: another set took over from the chosen one.
internal readonly record struct SelectionObservation(
    BroadcastClass Class,
    bool NewChoice = false,
    bool Continued = false,
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
//   - The chosen set stays chosen. Another takes over only when its median is SwitchMarginDb above the chosen
//     set's for SwitchHold without a break; while the chosen set is silent its last median is held.
//   - Addresses rotate: a new set within one window of the chosen set's last message, with the same case level
//     and bud pair, is the chosen set under new addresses and is adopted at once.
//   - After ReleaseAfter with no message from the chosen set it and the colour are dropped.
// Pure and single threaded: time comes from the messages' own timestamps, nothing here reads a clock, and
// nothing is written to disk.
internal sealed class BroadcastSelector
{
    private readonly Dictionary<uint, List<SenderMessage>> _senders = new();
    private readonly HashSet<uint> _chosenTags = new();
    private readonly Dictionary<uint, DateTimeOffset> _challengerSince = new();

    private ushort? _model;
    private byte? _colour;
    private DateTimeOffset? _firstHeardAt;
    private DateTimeOffset _latest = DateTimeOffset.MinValue;
    private DateTimeOffset? _chosenLastAt;
    private ProximityMessage? _chosenLastMessage;
    private double? _chosenMedian;
    private long _sequence;
    private int _setsInRange;

    public ushort? PairedModel => _model;

    // The colour learned from the chosen set, or null before the first choice.
    public byte? HeldColour => _colour;

    public int SetsInRange => _setsInRange;

    public bool HasChosen => _chosenTags.Count > 0;

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
        _chosenTags.Clear();
        _challengerSince.Clear();
        _colour = null;
        _firstHeardAt = null;
        _chosenLastAt = null;
        _chosenLastMessage = null;
        _chosenMedian = null;
        _setsInRange = 0;
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

        BroadcastSet? ownSet = sets.FirstOrDefault(s => s.Tags.Contains(tag));
        if (!HasChosen)
        {
            return Choose(sets, tag, at);
        }

        return Continue(sets, ownSet, tag, at);
    }

    // A first choice, or a choice after a release.
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

        AdoptSet(best);
        _colour ??= best.Newest.Message.Colour;
        _chosenMedian = best.MedianRssi;
        _challengerSince.Clear();
        bool mine = _chosenTags.Contains(tag);
        return new SelectionObservation(
            mine ? BroadcastClass.Chosen : BroadcastClass.OtherSet, NewChoice: true, SetsInRange: _setsInRange);
    }

    private SelectionObservation Continue(List<BroadcastSet> sets, BroadcastSet? own, uint tag, DateTimeOffset at)
    {
        BroadcastSet? chosen = sets.FirstOrDefault(s => s.Tags.Any(_chosenTags.Contains));
        if (chosen is not null)
        {
            // A second sender of the set (the other bud) joins the chosen tags here.
            AdoptSet(chosen);
            if (chosen.MedianRssi is double median)
            {
                _chosenMedian = median;
            }
        }

        // The chosen set came back under new addresses: a set of senders none of which was chosen, within one
        // window of the chosen set's last message, saying exactly what that message said.
        bool continued = false;
        if (!_chosenTags.Contains(tag) && own is not null && !own.Tags.Any(_chosenTags.Contains) &&
            _chosenLastMessage is ProximityMessage last && _chosenLastAt is DateTimeOffset lastAt &&
            at - lastAt <= BroadcastRules.Window &&
            BroadcastSenderSets.SameFields(own.Newest.Message, last))
        {
            AdoptSet(own);
            chosen = own;
            if (own.MedianRssi is double median)
            {
                _chosenMedian = median;
            }

            _challengerSince.Clear();
            continued = true;
        }

        BroadcastSet? winner = null;
        bool switched = !continued && EvaluateChallengers(sets, chosen, at, out winner);
        if (switched && winner is not null)
        {
            // The values the old set gave were another pair's. The colour is held across the switch.
            AdoptSet(winner, replace: true);
            _chosenMedian = winner.MedianRssi;
            _challengerSince.Clear();
            return new SelectionObservation(
                _chosenTags.Contains(tag) ? BroadcastClass.Chosen : BroadcastClass.OtherSet,
                NewChoice: true, Switched: true, SetsInRange: _setsInRange);
        }

        if (_chosenTags.Contains(tag))
        {
            return new SelectionObservation(BroadcastClass.Chosen, Continued: continued, SetsInRange: _setsInRange);
        }

        return new SelectionObservation(BroadcastClass.OtherSet, SetsInRange: _setsInRange);
    }

    // Updates the challengers' timers from this evaluation and returns the one that has been past the margin for
    // the whole hold, if any. A timer resets on any evaluation where its set has too few messages for a median
    // or is below the margin. The chosen set's own median is the held one while it is silent.
    private bool EvaluateChallengers(List<BroadcastSet> sets, BroadcastSet? chosen, DateTimeOffset at, out BroadcastSet? winner)
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
            if (ReferenceEquals(set, chosen) || set.Tags.Any(_chosenTags.Contains) || set.Newest.Message.Colour != _colour)
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

    // Makes set the chosen one: its tags join (or, on replace, become) the chosen tags, and its newest message and
    // time are the chosen set's last.
    private void AdoptSet(BroadcastSet set, bool replace = false)
    {
        if (replace)
        {
            _chosenTags.Clear();
        }

        foreach (uint t in set.Tags)
        {
            _chosenTags.Add(t);
        }

        SenderMessage newest = set.Newest;
        if (_chosenLastAt is null || newest.At >= _chosenLastAt || replace)
        {
            _chosenLastAt = newest.At;
            _chosenLastMessage = newest.Message;
        }
    }

    private void ReleaseIfStale()
    {
        if (HasChosen && _chosenLastAt is DateTimeOffset last && _latest - last > BroadcastRules.ReleaseAfter)
        {
            _chosenTags.Clear();
            _challengerSince.Clear();
            _chosenLastAt = null;
            _chosenLastMessage = null;
            _chosenMedian = null;
            _colour = null;
            _firstHeardAt = null;
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
    }

    // The newest message each other sender of the chosen set sent within the given time of at, for the caller's
    // check that the two buds of a set agree on which side is which.
    public IReadOnlyList<SenderMessage> OtherChosenSenders(uint tag, DateTimeOffset at, TimeSpan within)
    {
        var result = new List<SenderMessage>();
        foreach (uint other in _chosenTags)
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
