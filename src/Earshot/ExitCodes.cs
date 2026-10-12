namespace Earshot;

// Process exit codes shared by the run modes. The non-zero values follow the BSD sysexits
// numbering so they stay clear of small codes a run mode defines for itself.
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 64;         // bad command line
    public const int Unavailable = 69;   // this build has no implementation for the request
    public const int Declined = 76;      // the person said no on the screen to Fix connection; nothing more was changed
    public const int PlanOnly = 75;      // Fix connection printed its plan and changed nothing, because nobody was at the screen to answer
    public const int Software = 70;      // an internal check failed
    public const int OsError = 71;       // a startup Win32 call failed
    public const int IoError = 74;       // output could not be opened
    public const int Refused = 77;       // refused on purpose, for example in safe mode
    public const int Config = 78;        // bad environment configuration
}
