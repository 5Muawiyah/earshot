namespace Earshot.Widget.Alert;

public enum LatchState { Armed, Fired }

// One latch per part (left, right, case, and Windows' own figure for the headset), pure. Armed and a known percent at or below the threshold fires
// once; Fired and a known percent at least one 10% step above the threshold re-arms; an unknown percent
// changes nothing. A threshold change arms every latch, so the next reading at or below the new threshold
// fires once and a value already above it re-arms by the ordinary rule. Only what the card shows as current feeds
// this: the caller passes a part's percent only while it is fresh, and Windows' figure only while it is the one shown.
internal sealed class LowBatteryLatch
{
    public const int DefaultThresholdPercent = 20;

    private LatchState _left = LatchState.Armed;
    private LatchState _right = LatchState.Armed;
    private LatchState _case = LatchState.Armed;
    private LatchState _headset = LatchState.Armed;

    public LowBatteryLatch(int thresholdPercent = DefaultThresholdPercent)
    {
        ThresholdPercent = thresholdPercent;
    }

    public int ThresholdPercent { get; private set; }

    public LatchState Left => _left;

    public LatchState Right => _right;

    public LatchState Case => _case;

    public LatchState Headset => _headset;

    // Returns true exactly when this call fired the latch (Armed to Fired): the moment to show an alert.
    public bool ApplyLeft(int? percent) => Apply(ref _left, percent);

    public bool ApplyRight(int? percent) => Apply(ref _right, percent);

    public bool ApplyCase(int? percent) => Apply(ref _case, percent);

    public bool ApplyHeadset(int? percent) => Apply(ref _headset, percent);

    public void SetThreshold(int thresholdPercent)
    {
        if (thresholdPercent == ThresholdPercent)
        {
            return; // nothing about the threshold changed: an already-fired latch must not re-arm
        }

        ThresholdPercent = thresholdPercent;
        _left = LatchState.Armed;
        _right = LatchState.Armed;
        _case = LatchState.Armed;
        _headset = LatchState.Armed;
    }

    private bool Apply(ref LatchState state, int? percent)
    {
        if (percent is not int p)
        {
            return false;
        }

        if (state == LatchState.Armed && p <= ThresholdPercent)
        {
            state = LatchState.Fired;
            return true;
        }

        if (state == LatchState.Fired && p >= ThresholdPercent + 10)
        {
            state = LatchState.Armed;
        }

        return false;
    }
}
