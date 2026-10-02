using System.Runtime.InteropServices;
using Earshot.Widget;

namespace Earshot.Interop;

// The frame clock's display system (Earshot.Widget.IVBlankOutputs) over DXGI. It lives here, not under Earshot.Widget, because
// the widget may not reach a COM interface itself; the tray hands one to the clock.
// DXGI's outputs, listed by their monitors.
internal sealed class DxgiVBlankOutputs : IVBlankOutputs
{
    private readonly Dictionary<nint, IDXGIOutput> _outputs = [];

    public void ConfigureThread(Thread thread) => thread.SetApartmentState(ApartmentState.MTA);

    public nint MonitorFor(nint window) => Shell.MonitorFromWindow(window, Shell.MONITOR_DEFAULTTONEAREST);

    public bool Has(nint monitor) => _outputs.ContainsKey(monitor);

    public int WaitForVBlank(nint monitor) =>
        _outputs.TryGetValue(monitor, out IDXGIOutput? output) ? output.WaitForVBlank() : Dxgi.DXGI_ERROR_NOT_FOUND;

    public void Release()
    {
        foreach (IDXGIOutput output in _outputs.Values)
        {
            Marshal.ReleaseComObject(output);
        }

        _outputs.Clear();
    }

    // Every output of every adapter, by its monitor. Returns the first failing HRESULT, or 0.
    public int ListOutputs()
    {
        ReleaseOutputs();
        int hr = Dxgi.CreateFactory(out IDXGIFactory1? factory);
        if (hr < 0 || factory is null)
        {
            return hr < 0 ? hr : ComActivation.E_POINTER;
        }

        try
        {
            for (uint a = 0; ; a++)
            {
                hr = factory.EnumAdapters(a, out nint adapterPointer);
                if (hr == Dxgi.DXGI_ERROR_NOT_FOUND)
                {
                    return 0;
                }

                hr = ComActivation.TakeInterface(hr, adapterPointer, out IDXGIAdapter? adapter);
                if (hr < 0 || adapter is null)
                {
                    return hr < 0 ? hr : ComActivation.E_POINTER;
                }

                try
                {
                    for (uint o = 0; ; o++)
                    {
                        hr = adapter.EnumOutputs(o, out nint outputPointer);
                        if (hr == Dxgi.DXGI_ERROR_NOT_FOUND)
                        {
                            break;
                        }

                        hr = ComActivation.TakeInterface(hr, outputPointer, out IDXGIOutput? output);
                        if (hr < 0 || output is null)
                        {
                            return hr < 0 ? hr : ComActivation.E_POINTER;
                        }

                        hr = output.GetDesc(out DXGI_OUTPUT_DESC desc);
                        if (hr < 0 || !_outputs.TryAdd(desc.Monitor, output))
                        {
                            Marshal.ReleaseComObject(output);
                            if (hr < 0)
                            {
                                return hr;
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private void ReleaseOutputs()
    {
        foreach (IDXGIOutput output in _outputs.Values)
        {
            Marshal.ReleaseComObject(output);
        }

        _outputs.Clear();
    }
}
