using System.Runtime.ExceptionServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Interop;

// Runs COM work on one dedicated MTA thread, the way the audio worker does, so every RCW is created and
// used in the same apartment. Exceptions (including an inconclusive result) are rethrown on the caller.
internal static class MtaThread
{
    public static void Run(Action work, TimeSpan timeout)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Earshot test MTA",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        if (!thread.Join(timeout))
        {
            throw new AssertFailedException("The MTA work did not finish within " + timeout + ".");
        }

        failure?.Throw();
    }
}
