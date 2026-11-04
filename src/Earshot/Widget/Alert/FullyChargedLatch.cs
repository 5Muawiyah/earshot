namespace Earshot.Widget.Alert;

// What one look at a part says about the fully charged notice.
internal enum FullyChargedStep { None, Live }

// One latch per part (left, right, case), pure. A part that reaches 100 as a live reading notifies once and is then spent. A
// spent part is armed again only by a live reading below 100, which is what "no longer fully charged" is. Design choice: the
// notice is per charge, so a part that stays at 100 notifies once however many readings say so, and one that comes off 100
// and charges to 100 again notifies again. A live reading of 100 that is not charging does not arm it: a full part may well
// report not charging, and arming it then would notify again at once. An old reading never arms it, since it says nothing of
// what the part is doing now.
//
// A last reading of 100 (saved, not heard by this run) spends the latch without a notice: the part was already full when this
// run began, so it is not news. The caller feeds it always and gates only the notification on the setting, as the low battery
// latch is fed, so a part that filled while the notice was off does not notify when it is turned on.
internal sealed class FullyChargedLatch
{
    private readonly bool[] _spent = new bool[3];

    // Whether the part is spent, read only, for tests.
    public bool IsSpent(ChargeComponent component) => _spent[(int)component];

    // Marks the part spent without a notice: what a restart does for a part saved as spent (SpentMark).
    public void Spend(ChargeComponent component) => _spent[(int)component] = true;

    // Feeds the part as it is shown; returns whether this look is the moment to notify.
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
        return part.Kind == ReadingKind.Live ? FullyChargedStep.Live : FullyChargedStep.None;
    }
}
