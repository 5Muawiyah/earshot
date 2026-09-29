using System.Diagnostics;
using System.Runtime.InteropServices;
using Earshot.Interop;

namespace Earshot.Service;

// One status report to the control manager (SERVICE_STATUS without the service type, which is always its own process).
internal readonly record struct ServiceStatusReport(
    uint State,
    uint ControlsAccepted,
    uint Win32ExitCode,
    uint ServiceSpecificExitCode,
    uint CheckPoint,
    uint WaitHint);

// What the service process needs from the control manager, behind an interface so the service is tested against a
// fake and the real one is exercised only where it can be without the manager (RunDispatcher from a console).
internal interface IServiceHost
{
    // Hands this thread to the control manager. Returns 0 only after every service in the process has stopped, and
    // the Win32 error otherwise: ERROR_FAILED_SERVICE_CONTROLLER_CONNECT (1063) when the process was not started by
    // the control manager, for instance when it is run from a console.
    uint RunDispatcher(string serviceName, Action<string[]> serviceMain);

    // Registers the control handler. Zero, with the Win32 error, when it failed. Must come before the first status
    // report.
    nint RegisterHandler(string serviceName, Func<uint, uint, nint, uint> handler, out uint error);

    bool SetStatus(nint handle, in ServiceStatusReport report, out uint error);
}

// The real dispatcher. A service process holds one service, so the two callbacks the control manager calls are
// static methods reaching the managed ones through static fields. They return to native code, so an exception must
// never leave them: it is traced and handed to onFault, and the control manager gets the "not implemented" answer.
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicectrldispatcherw
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-registerservicectrlhandlerexw
// https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-setservicestatus
internal sealed unsafe class WindowsServiceHost : IServiceHost
{
    private static Action<string[]>? s_serviceMain;
    private static Func<uint, uint, nint, uint>? s_handler;
    private static Action<Exception>? s_onFault;

    private readonly Action<Exception>? _onFault;

    public WindowsServiceHost(Action<Exception>? onFault = null)
    {
        _onFault = onFault;
    }

    public uint RunDispatcher(string serviceName, Action<string[]> serviceMain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(serviceMain);
        s_serviceMain = serviceMain;
        s_onFault = _onFault;
        char* name = (char*)Marshal.StringToHGlobalUni(serviceName);
        try
        {
            // The table ends with a zeroed entry. For a service that has its own process the name is ignored.
            SERVICE_TABLE_ENTRYW* table = stackalloc SERVICE_TABLE_ENTRYW[2];
            table[0].lpServiceName = name;
            table[0].lpServiceProc = &ServiceMainThunk;
            table[1].lpServiceName = null;
            table[1].lpServiceProc = null;
            return AdvApi32.StartServiceCtrlDispatcher(table) ? 0 : LastError();
        }
        finally
        {
            Marshal.FreeHGlobal((nint)name);
        }
    }

    public nint RegisterHandler(string serviceName, Func<uint, uint, nint, uint> handler, out uint error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(handler);
        s_handler = handler;
        nint status = AdvApi32.RegisterServiceCtrlHandlerEx(serviceName, &HandlerThunk, 0);
        error = status == 0 ? LastError() : 0;
        return status;
    }

    public bool SetStatus(nint handle, in ServiceStatusReport report, out uint error)
    {
        var native = new SERVICE_STATUS
        {
            dwServiceType = AdvApi32.SERVICE_WIN32_OWN_PROCESS,
            dwCurrentState = report.State,
            dwControlsAccepted = report.ControlsAccepted,
            dwWin32ExitCode = report.Win32ExitCode,
            dwServiceSpecificExitCode = report.ServiceSpecificExitCode,
            dwCheckPoint = report.CheckPoint,
            dwWaitHint = report.WaitHint,
        };
        bool ok = AdvApi32.SetServiceStatus(handle, native);
        error = ok ? 0 : LastError();
        return ok;
    }

    private static uint LastError() => unchecked((uint)Marshal.GetLastPInvokeError());

    [UnmanagedCallersOnly]
    private static void ServiceMainThunk(uint argc, char** argv)
    {
        try
        {
            var args = new string[argc];
            for (uint i = 0; i < argc; i++)
            {
                args[i] = Marshal.PtrToStringUni((nint)argv[i]) ?? "";
            }

            s_serviceMain?.Invoke(args);
        }
        catch (Exception ex)
        {
            Fault("ServiceMain", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static uint HandlerThunk(uint control, uint eventType, nint eventData, nint context)
    {
        try
        {
            return s_handler?.Invoke(control, eventType, eventData) ?? AdvApi32.ERROR_CALL_NOT_IMPLEMENTED;
        }
        catch (Exception ex)
        {
            Fault("control handler", ex);
            return AdvApi32.ERROR_CALL_NOT_IMPLEMENTED;
        }
    }

    // An exception that reached a native callback. The trace is for a debugger; the fault hook is the service's log.
    private static void Fault(string where, Exception ex)
    {
        Trace.WriteLine("Earshot service: " + where + " stopped with " + ex.GetType().Name + ": " + ex.Message);
        try
        {
            s_onFault?.Invoke(ex);
        }
        catch (Exception hookFailure)
        {
            // The hook writes a log line. Nothing more can be done from inside a native callback.
            Trace.WriteLine("Earshot service: the fault hook stopped with " + hookFailure.GetType().Name + ": " + hookFailure.Message);
        }
    }
}
