namespace Earshot.Widget;

// The gauge's current screen rectangle and window handle, so the reader can tell whether
// WindowFromPoint at its own centre returns the gauge itself.
internal readonly record struct ShownGauge(Rectangle Bounds, nint Handle);

// What TaskbarWatcher reads each poll. UiaTaskbarReader is the real implementation, on the UIA worker
// thread; tests supply a fake. shownGauge is null while the gauge is not shown.
internal interface ITaskbarReader
{
    Result Read(ShownGauge? shownGauge);

    // The same read for the display the owner chose (GaugeDisplayChoice.MainDisplay, "", for the main one). A reader
    // that knows only one taskbar ignores the choice.
    Result Read(ShownGauge? shownGauge, string chosenDisplayId) => Read(shownGauge);

    // A read's outcome: exactly one of a layout or a failure.
    readonly record struct Result
    {
        public TaskbarLayout? Layout { get; }

        public TaskbarReadFailure? Failure { get; }

        private Result(TaskbarLayout? layout, TaskbarReadFailure? failure)
        {
            Layout = layout;
            Failure = failure;
        }

        public static Result Ok(TaskbarLayout layout) => new(layout, null);

        public static Result Fail(TaskbarReadFailure failure) => new(null, failure);
    }
}
