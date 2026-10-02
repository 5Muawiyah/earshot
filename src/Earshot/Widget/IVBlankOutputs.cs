namespace Earshot.Widget;

// What the frame clock needs of the display system, so its thread logic can be tested without DXGI: which monitor a window is
// on, the outputs that can be waited on, and one wait for a blank. Every member but the first is called on the clock's own thread.
internal interface IVBlankOutputs
{
    // The thread the clock waits on is configured here (the real one needs a multithreaded COM apartment, which a fake need not).
    void ConfigureThread(Thread thread);

    // The monitor a window is on, or the nearest one for window 0.
    nint MonitorFor(nint window);

    // True when an output for the monitor is already listed.
    bool Has(nint monitor);

    // Lists every output again, forgetting the old ones. The first failing HRESULT, or 0.
    int ListOutputs();

    // Waits for the next vertical blank of the monitor's output. An HRESULT; negative when it could not wait.
    int WaitForVBlank(nint monitor);

    // Lets go of every listed output.
    void Release();
}

// The outputs of a clock with no display system behind it: none is ever listed, so each motion ends in one frame. What a
// presenter built without a real clock gets (design choice: the real DXGI outputs are composed at the tray, outside this namespace).
internal sealed class NoVBlankOutputs : IVBlankOutputs
{
    public void ConfigureThread(Thread thread)
    {
    }

    public nint MonitorFor(nint window) => 0;

    public bool Has(nint monitor) => false;

    public int ListOutputs() => unchecked((int)0x887A0002);

    public int WaitForVBlank(nint monitor) => unchecked((int)0x887A0002);

    public void Release()
    {
    }
}
