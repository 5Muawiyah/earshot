using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Streaming;

// Where the streaming code is allowed to reach, read from its source. This is a scan, so it closes nothing by itself:
// what the feature does is proved by the tests that run it (TrayStreamingTests drives a whole session and sees no
// call on the block controller, the connection controller or the protection controller). What a scan does add is a
// loud failure on the day someone gives this folder a way to reach them at all.
[TestClass]
public sealed class StreamingBoundaryTests
{
    // Nothing under Streaming may name the AirPods side of Earshot, a way to elevate, a way to pair, or a WinRT
    // namespace the design rules out.
    private static readonly string[] Forbidden =
    [
        "Earshot.Boot", "Earshot.Interop", "Earshot.Protection", "Earshot.Audio",
        "IBlockController", "IConnectionController", "IAudioProtectionController", "BlockCoordinator",
        "AllowAsync", "BlockAsync", "RunSetupAsync", "TaskScheduler", "CfgMgr32", "BluetoothSetServiceState",
        "Process.Start", "runas", "requireAdministrator",
        "PairAsync", "DeviceInformationPairing", "ms-settings",
        "Windows.Devices.Bluetooth", "Windows.Media.Control", "Windows.Media.Devices",
        "CreateWatcher", "DeviceWatcher",
        ".Result", ".Wait(", "GetAwaiter().GetResult", "Thread.Sleep",
    ];

    [TestMethod]
    public void TheStreamingFolderReachesNothingOnTheAirPodsSideAndNeverBlocks()
    {
        string[] files = SourceFiles();
        Assert.IsTrue(files.Length >= 10, "Only " + files.Length + " files were read, so a clean result would prove nothing.");

        var found = new List<string>();
        foreach (string file in files)
        {
            string code = WithoutComments(File.ReadAllText(file));
            foreach (string word in Forbidden)
            {
                // StreamingCoordinator.Dispose asks the semaphore for a turn with a zero wait, which returns at once.
                if (word == ".Wait(" && Path.GetFileName(file) == "StreamingCoordinator.cs")
                {
                    code = code.Replace("_oneAtATime.Wait(0)", "", StringComparison.Ordinal);
                }

                if (code.Contains(word, StringComparison.Ordinal))
                {
                    found.Add(Path.GetFileName(file) + ": " + word);
                }
            }
        }

        Assert.AreEqual(0, found.Count, string.Join(Environment.NewLine, found));
    }

    // One file calls WinRT, and one more names the two WinRT enums it maps. Everything else is plain .NET, which is
    // what lets every other test run with no radio, no phone and no Windows version to speak of.
    [TestMethod]
    public void OnlyThePlatformAndTheStatusMapNameAWindowsType()
    {
        string[] naming = SourceFiles()
            .Where(f => WithoutComments(File.ReadAllText(f)).Contains("using Windows.", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Sequence.Of("StreamingStatusMap.cs", "WindowsStreamingPlatform.cs"), naming);
    }

    // The selector is always the one Windows supplies. A device interface GUID or a query written out in the source
    // would be both wrong and a piece of one machine left in the code.
    [TestMethod]
    public void NoSelectorOrInterfaceGuidIsWrittenOut()
    {
        foreach (string file in SourceFiles())
        {
            string code = WithoutComments(File.ReadAllText(file));
            Assert.IsFalse(code.Contains("System.Devices.DevObjectType", StringComparison.Ordinal), Path.GetFileName(file));
            Assert.IsFalse(code.Contains("InterfaceClassGuid", StringComparison.Ordinal), Path.GetFileName(file));
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(code, "[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}"),
                Path.GetFileName(file) + " holds a GUID.");
        }
    }

    private static string[] SourceFiles()
    {
        string start = AppContext.BaseDirectory;
        for (DirectoryInfo? folder = new(start); folder is not null; folder = folder.Parent)
        {
            string streaming = Path.Combine(folder.FullName, "src", "Earshot", "Streaming");
            if (Directory.Exists(streaming))
            {
                return Directory.GetFiles(streaming, "*.cs", SearchOption.AllDirectories);
            }
        }

        throw new AssertFailedException("src\\Earshot\\Streaming was not found above " + start + ".");
    }

    // Line comments are prose: they say what the code must never do, in the very words this test looks for.
    private static string WithoutComments(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "(?m)^\\s*//.*$|(?<=\\s)//(?!/).*$", "");
}
