using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Phase2;

// Discovery only reads. This scans the discovery sources (src\Earshot\Audio except the Connect folder, and the
// audio composition hook) for anything that could change a device, a devnode, a Bluetooth service or a task.
// The connect path lives in Audio\Connect and is checked by its own tests.
[TestClass]
public sealed class DiscoverySourceGuardTests
{
    private static readonly string[] Forbidden =
    [
        "KSPROPSETID_BtAudio",
        "KSPROPERTY_ONESHOT",
        "KSPROPERTY_TYPE_SET",
        "KsMethod",
        "KsEvent",
        "CM_Disable_DevNode",
        "CM_Enable_DevNode",
        "BluetoothSetServiceState",
        "RunEx",
        ".SetValue(",
        ".Commit(",
        ".ConnectTo(",
        ".Disconnect(",
    ];

    [TestMethod]
    public void DiscoveryCodeContainsNoDeviceChangingCall()
    {
        List<string> files = DiscoveryFiles();
        var offenders = new List<string>();
        foreach (string file in files)
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (string token in Forbidden)
                {
                    if (lines[i].Contains(token, StringComparison.Ordinal))
                    {
                        offenders.Add(Path.GetFileName(file) + ":" + (i + 1) + ": " + token);
                    }
                }
            }
        }

        Assert.IsGreaterThan(5, files.Count, "The discovery sources were not found.");
        Assert.IsEmpty(offenders, string.Join(Environment.NewLine, offenders));
    }

    // The single kernel streaming request in discovery is the KSPROPSETID_Pin Get in TopologyWalk.ReadPinCount.
    [TestMethod]
    public void TheOnlyKernelStreamingRequestIsThePinCountRead()
    {
        var calls = new List<string>();
        foreach (string file in DiscoveryFiles())
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(".KsProperty(", StringComparison.Ordinal))
                {
                    calls.Add(Path.GetFileName(file) + ":" + (i + 1));
                }
            }
        }

        Assert.HasCount(1, calls, string.Join(Environment.NewLine, calls));
        StringAssert.StartsWith(calls[0], "TopologyWalk.cs:");

        string walk = File.ReadAllText(DiscoveryFiles().Single(f => Path.GetFileName(f) == "TopologyWalk.cs"));
        StringAssert.Contains(walk, "new KSPROPERTY(KsControl.KSPROPSETID_Pin, KsControl.KSPROPERTY_PIN_CTYPES, KsControl.KSPROPERTY_TYPE_GET)");
    }

    private static List<string> DiscoveryFiles()
    {
        string root = RepositoryRoot();
        string audio = Path.Combine(root, "src", "Earshot", "Audio");
        string connect = Path.Combine(audio, "Connect") + Path.DirectorySeparatorChar;
        List<string> files = Directory.EnumerateFiles(audio, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(connect, StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) && !f.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
            .ToList();
        files.Add(Path.Combine(root, "src", "Earshot", "Composition", "CompositionRoot.Audio.cs"));
        return files;
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Earshot.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new AssertFailedException("Earshot.slnx was not found above " + AppContext.BaseDirectory + ".");
    }
}
