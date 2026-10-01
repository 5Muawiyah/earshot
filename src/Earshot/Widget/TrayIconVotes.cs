namespace Earshot.Widget;

// One tray icon, several gauges. Each gauge's controller wants the icon hidden while its gauge is shown and visible
// when it is not, and with a gauge on every display those wishes disagree: the icon must stay out of the way while
// any gauge is on a taskbar, and come back only when none is. Every controller gets its own voter; the icon is
// visible only when every voter asks for it.
//
// A vote is forwarded on every change, with no check that the answer differs from the last one, so a tray with one
// gauge sets the icon exactly as often as a controller that held the icon itself would. UI thread only.
internal sealed class TrayIconVotes(ITrayIconVisibility icon)
{
    private readonly List<Voter> _voters = [];

    public Voter NewVoter()
    {
        var voter = new Voter(this);
        _voters.Add(voter);
        return voter;
    }

    private void Changed()
    {
        bool visible = true;
        foreach (Voter v in _voters)
        {
            visible &= v.Wants;
        }

        icon.Visible = visible;
    }

    private void Remove(Voter voter)
    {
        if (_voters.Remove(voter))
        {
            Changed();
        }
    }

    // One controller's vote. Starts as visible, as the controller's own first state does.
    internal sealed class Voter(TrayIconVotes owner) : ITrayIconVisibility, IDisposable
    {
        private bool _gone;

        public bool Wants { get; private set; } = true;

        public bool Visible
        {
            set
            {
                if (_gone)
                {
                    return;
                }

                Wants = value;
                owner.Changed();
            }
        }

        // The gauge is gone: its vote goes with it, and the icon follows whoever is left. A late vote from it counts for nothing.
        public void Dispose()
        {
            _gone = true;
            owner.Remove(this);
        }
    }
}
