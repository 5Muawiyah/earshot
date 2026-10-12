using System.Globalization;

namespace Earshot.Setup;

// Every sentence Setup shows. British English, plain and short.
internal static class SetupText
{
    public const string WindowTitle = "Earshot Setup";

    public const string NoPayloadInstruction = "This Setup has no Earshot in it";
    public const string NoPayloadContent = "Download Setup again from the Earshot releases page.";

    public const string DamagedInstruction = "The installer is damaged";
    public const string DamagedContent = "Download it again.";

    public const string NotElevatedInstruction = "Setup needs administrator rights";
    public const string NotElevatedContent = "Run Setup again and choose Yes when Windows asks.";

    public const string NotThePersonInstruction = "Setup must run as the person signed in";
    public const string NotThePersonContent = "Another account answered the administrator prompt. Sign in as an administrator and run Setup again, or use the one-line installer from the README.";

    public const string StagingInstruction = "Setup could not make a safe working folder";

    public const string UnpackInstruction = "Setup could not unpack Earshot";

    public const string ReadInstruction = "Setup could not look at this PC";
    public const string ReadContent = "Try again. If it keeps happening, restart the PC first.";

    public const string NoAirPodsInstruction = "No AirPods are paired";
    public const string NoAirPodsContent = "Pair your AirPods in Windows under Settings, Bluetooth and devices, then run Setup again.";

    public const string WhichAirPodsInstruction = "Which AirPods?";
    public const string WhichAirPodsContent = "More than one pair of AirPods is paired with this PC.";

    public const string DowngradeInstruction = "A newer Earshot is installed";

    public const string InstallingInstruction = "Installing Earshot";
    public const string RepairingInstruction = "Repairing Earshot";
    public const string UninstallingInstruction = "Removing Earshot";
    public const string FixingInstruction = "Fixing the connection";
    public const string WorkContent = "This takes a minute. Please wait.";

    public const string ChoiceInstall = "Install Earshot";
    public const string ChoiceRepair = "Repair Earshot";
    public const string ChoiceRepairNote = "Put back any file or setting that is damaged.";
    public const string ChoiceFix = "Fix connection";
    public const string ChoiceFixNote = "Sort out AirPods that stick on Connecting or will not connect.";
    public const string ChoiceUninstall = "Uninstall Earshot";
    public const string ChoiceUninstallNote = "Remove Earshot and let Windows treat the AirPods as normal.";

    // Said under every first dialog. The checksum is inside the file it checks.
    public const string CheckFooter =
        "Setup checked that this file arrived whole. That catches damage. It does not show the file is genuine, or that " +
        "nobody swapped it, so get Earshot only from its releases page on GitHub.";

    public const string InstalledAppsNote = "Earshot is also listed under Settings, Apps, Installed apps.";

    public const string InstalledInstruction = "Earshot is installed";
    public const string FixDeclinedInstruction = "Fix connection stopped at your answer";
    public const string FirstChangesMade = "The changes you agreed to first were made. The AirPods were not removed or paired again.";

    public const string NothingChanged = "Nothing was changed.";
    public const string InstalledContent = "Open Earshot from the Start menu.";
    public const string RepairedInstruction = "Earshot is repaired";
    public const string RepairedContent = "Nothing else needs doing.";
    public const string UninstalledInstruction = "Earshot is removed";
    public const string UninstalledContent =
        "Windows treats the AirPods as normal again. If Earshot still starts with Windows, turn it off in Task Manager under Startup apps.";
    public const string FixedInstruction = "Fix connection has finished";
    public const string FixedContent = "Try connecting the AirPods now.";

    public const string PartialInstruction = "Earshot is installed, with a problem";
    public const string PartialContent = "Part of the set-up did not finish. Run Setup again and choose Repair.";
    public const string BusyInstruction = "Another set-up is running";
    public const string BusyContent = "Wait for it to finish, then run Setup again.";
    public const string AlreadyInstalledInstruction = "Earshot is already installed";
    public const string AlreadyInstalledContent = "Run Setup again and choose Repair.";
    public const string StillRunningInstruction = "Setup is still finishing";
    public const string StillRunningContent = "Wait a minute, then open Earshot from the Start menu.";
    public const string FailedInstruction = "Setup did not finish";
    public const string FailedContent = "Nothing was left half done that Windows needs. Try again, or restart the PC first.";

    public const string CleanupNote = "Setup could not remove its working folder: ";

    public static string InstallContent(string version) =>
        "Version " + version + ". Earshot connects your AirPods to this PC and stops Windows paging them at start-up. " +
        "Your AirPods must be paired with this PC first.";

    public static string InstallInstruction(string version) => "Install Earshot " + version;

    public static string InstalledChoiceInstruction(string installedVersion) =>
        "Earshot " + (installedVersion.Length == 0 ? "" : installedVersion + " ") + "is installed";

    public static string InstalledChoiceContent => "Choose what to do.";

    public static string Downgrade(string installed, string payload) =>
        "Setup will not replace Earshot " + installed + " with the older " + payload + ". Choose Repair to put the installed one right.";

    public static string Code(int code) => "Code " + code.ToString(CultureInfo.InvariantCulture) + ".";

    public static string AirPodsChoice(string name, int index) =>
        string.IsNullOrWhiteSpace(name) ? "AirPods " + (index + 1).ToString(CultureInfo.InvariantCulture) : name;
}
