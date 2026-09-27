namespace Earshot.Widget;

// What TaskbarWatcher reads each poll. UiaTaskbarReader is the real implementation, on the UIA worker
// thread; tests supply a fake. shownGauge is the gauge's current screen rectangle, or null while it is
// not shown, so the reader can also answer GaugeCentreIsGauge (section 4.1, 4.6).
internal interface ITaskbarReader
{
    Result Read(Rectangle? shownGauge);

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
