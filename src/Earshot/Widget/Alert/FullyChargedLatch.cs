namespace Earshot.Widget.Alert;

// What one look at a part says about the fully charged notice.
internal enum FullyChargedStep { None, Live, Estimated }

// One latch per part (left, right, case), pure. A part that reaches 100, as a live reading or as an estimate (BatteryFreshness
// says which: the shown part's kind), notifies once and is then spent. A spent part is armed again only by a live reading
// below 100, which is what "no longer fully charged" is. Design choice: the notice is per charge, so a part that stays at 100
// notifies once however many readings say so, and one that comes off 100 and charges to 100 again notifies again. A live
// reading of 100 that is not charging does not arm it: a full part may well report not charging, and arming it then would
// notify again at once. An estimate or an old reading never arms it, since neither says what the part is doing now.
//
// An estimate that reaches 100 and a live 100 after it are one charge, so one notice: the estimate spends the latch, and the
// live 100 finds it spent. A last reading of 100 (saved, not heard by this run) spends it without a notice: the part was
// already full when this run began, so it is not news. The caller feeds it always and gates only the notification on the
// setting, as the low battery latch is fed, so a part that filled while the notice was off does not notify when it is turned on.
internal sealed class FullyChargedLatch
{
    private readonly bool[] _spent = new bool[3];

    // Whether the part is spent, read only, for tests.
    public bool IsSpent(ChargeComponent component) => _spent[(int)component];

    // Feeds the part as it is shown; returns whether this look is the moment to notify, and of what kind.
    public FullyChargedStep Apply(ChargeComponent component, ShownPart part)
    {
        if (!part.HasValue || part.Percent is not int percent)
        {
            return FullyChargedStep.None;
        }

        int index = (int)component;
        if (percent < 100)
        {
            if (part.Kind == ReadingKind.Live)
            {
                _spent[index] = false;
            }

            return FullyChargedStep.None;
        }

        if (_spent[index])
        {
            return FullyChargedStep.None;
        }

        _spent[index] = true;
        return part.Kind switch
        {
            ReadingKind.Live => FullyChargedStep.Live,
            ReadingKind.Estimated => FullyChargedStep.Estimated,
            _ => FullyChargedStep.None,
        };
    }
}
