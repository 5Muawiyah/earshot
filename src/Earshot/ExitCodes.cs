namespace Earshot;

// Process exit codes shared by the run modes. The non-zero values follow the BSD sysexits
// numbering so they stay clear of small codes a run mode defines for itself.
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 64;         // bad command line
    public const int Unavailable = 69;   // this build has no implementation for the request
    public const int Declined = 76;      // the person said no on the screen to Fix connection; nothing was changed
    public const int DeclinedAfterChanges = 79; // the person said no to Fix connection's second question after the first one's changes were made
    public const int PlanOnly = 75;      // Fix connection printed its plan and changed nothing, because nobody was at the screen to answer
    public const int Software = 70;      // an internal check failed
    public const int OsError = 71;       // a startup Win32 call failed
    public const int IoError = 74;       // output could not be opened
    public const int Refused = 77;       // refused on purpose, for example in safe mode
    public const int Config = 78;        // bad environment configuration
    public const int AlreadyRunning = 80; // another Fix connection or Pair AirPods run holds the lock; nothing was started
}
