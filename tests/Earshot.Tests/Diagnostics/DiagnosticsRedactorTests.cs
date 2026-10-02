using Earshot.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Diagnostics;

// What "Copy diagnostics" may and may not let through. Each kind of private detail is planted in a fake log and a fake
// frame log; the text built from them must hold none of it and must still hold the harmless lines around it.
[TestClass]
public sealed class DiagnosticsRedactorTests
{
    private const string User = "jbloggs";
    private const string DeviceName = "Jo\u2019s AirPods Pro";
    private const string WindowTitle = "Quarterly figures - Excel";

    private const string AddressColon = "A1:B2:C3:D4:E5:F6";
    private const string AddressDash = "a1-b2-c3-d4-e5-f6";
    private const string AddressDigits = "A1B2C3D4E5F6";
    private const string AddressPrefixed = "0xA1B2C3D4E5F6";
    private const string Container = "6f1c2a3e-9b7d-4c10-8a55-0123456789ab";
    private const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string InstancePath = @"BTHENUM\DEV_A1B2C3D4E5F6\7&2d6c5a1b&0&BluetoothDevice_A1B2C3D4E5F6";
    private const string LePath = @"BTHLE\DEV_A1B2C3D4E5F6\8&1f2e3d4c&0&A1B2C3D4E5F6";
    private const string SwdPath = @"SWD\MMDEVAPI\{0.0.0.00000000}.{6f1c2a3e-9b7d-4c10-8a55-0123456789ab}";
    private const string InterfacePath = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{6f1c2a3e-9b7d-4c10-8a55-0123456789ab}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string GuidHashPath = "{e6327cad-dcec-4949-ae8a-991e976a79d2}#something";

    private static readonly string[] Harmless =
    [
        "2026-10-02T09:15:30.123Z INFO Hotkey: toggle connection.",
        "2026-10-02T09:15:31.000Z DEBUG RegisterHotKey id=0x4A00 modifiers=0x4007 vk=0x41 result=ok",
        "2026-10-02T09:15:32.500Z WARN Connect failed: Windows reported error 1460.",
        "Battery left 80% right 78% case 100%",
    ];

    private static string[] FakeLog() =>
    [
        Harmless[0],
        "2026-10-02T09:15:30.200Z INFO Device chosen: " + DeviceName + " (" + AddressColon + ", container " + Container + ").",
        "2026-10-02T09:15:30.300Z INFO Gate address " + AddressDigits + " and " + AddressPrefixed + " and " + AddressDash,
        Harmless[1],
        "2026-10-02T09:15:30.400Z INFO install for " + Sid + ".",
        "2026-10-02T09:15:30.500Z WARN Instance " + InstancePath + " and " + LePath + " and " + SwdPath,
        "2026-10-02T09:15:30.600Z INFO Interface " + InterfacePath + " also " + GuidHashPath,
        @"2026-10-02T09:15:30.700Z WARN Settings at C:\Users\" + User + @"\AppData\Roaming\Earshot\settings.json",
        @"2026-10-02T09:15:30.800Z WARN Staging at \\?\C:\Users\" + User + @"\AppData\Local\Earshot\staging",
        "2026-10-02T09:15:30.900Z WARN Folder C:/Users/" + User + "/AppData/Local/Earshot/logs and %USERPROFILE%\\Downloads\\Earshot.zip",
        "2026-10-02T09:15:31.000Z INFO Foreground window was \"" + WindowTitle + "\"",
        Harmless[2],
        Harmless[3],
    ];

    private static string[] FakeFrameLog() =>
    [
        "t+0.1s frame from " + AddressColon + " model 0x200E battery 80/78/100 container " + Container,
        "t+0.2s " + DeviceName.ToUpperInvariant() + " lid open " + AddressDash,
        "t+0.3s window " + WindowTitle + " user " + User,
    ];

    private sealed class FakeFrames(string[] lines) : IFrameLogSource
    {
        public IReadOnlyList<string> Lines() => lines;
    }

    private static string Build() => DiagnosticsText.Build(
        "1.4.0", "Microsoft Windows 11 Pro 10.0.26100", FakeLog(), new FakeFrames(FakeFrameLog()),
        knownNames: [DeviceName, WindowTitle], userName: User);

    [TestMethod]
    public void NothingPrivateReachesTheCopiedText()
    {
        string text = Build();

        string[] secrets =
        [
            AddressColon, AddressDash, AddressDigits, AddressPrefixed, "A1B2C3D4E5F6", "a1b2c3d4e5f6",
            Container, "6f1c2a3e", "0123456789ab",
            Sid, "1004336348", "S-1-5-21",
            "Jo\u2019s", "Jo's", "AirPods Pro", DeviceName.ToUpperInvariant(),
            WindowTitle, "Quarterly figures",
            User, @"C:\Users", "C:/Users", "%USERPROFILE%", "Downloads", "AppData",
            "BTHENUM", "BTHLE", "MMDEVAPI", "e6327cad", "7&2d6c5a1b",
        ];
        foreach (string secret in secrets)
        {
            Assert.IsFalse(text.Contains(secret, StringComparison.OrdinalIgnoreCase), "\"" + secret + "\" reached the copied text:\n" + text);
        }
    }

    [TestMethod]
    public void HarmlessLinesSurviveWhole()
    {
        string text = Build();

        foreach (string line in Harmless)
        {
            StringAssert.Contains(text, line);
        }

        StringAssert.Contains(text, "Version: 1.4.0");
        StringAssert.Contains(text, "Windows: Microsoft Windows 11 Pro 10.0.26100");
        StringAssert.Contains(text, "Frame log");
    }

    [TestMethod]
    public void EachKindIsReplacedByItsOwnTag()
    {
        string text = Build();

        foreach (string tag in new[] { DiagnosticsRedactor.AddressTag, DiagnosticsRedactor.IdTag, DiagnosticsRedactor.SidTag, DiagnosticsRedactor.DeviceTag, DiagnosticsRedactor.PathTag, DiagnosticsRedactor.NameTag })
        {
            StringAssert.Contains(text, tag);
        }

        Assert.AreEqual("Device chosen: <name> (<address>, container <id>).", DiagnosticsRedactor.Redact("Device chosen: " + DeviceName + " (" + AddressColon + ", container " + Container + ").", [DeviceName], User));
    }

    [TestMethod]
    public void AddressesAreTakenInEveryFormTheLogMayPrint()
    {
        foreach (string address in new[] { AddressColon, AddressDash, AddressDigits, AddressPrefixed, "0Xa1b2c3d4e5f6", "a1b2c3d4e5f6" })
        {
            Assert.AreEqual("addr <address> end", DiagnosticsRedactor.Redact("addr " + address + " end", null, User), address);
        }
    }

    // Not an address: too short or too long a run, a larger number, and the hot key ids the log prints.
    [TestMethod]
    public void NumbersThatAreNotAddressesAreKept()
    {
        foreach (string text in new[] { "id=0x4A00", "vk=0x41", "error 1460", "1 2 3 4 5", "A1B2C3D4E5F", "A1B2C3D4E5F67", "ab:cd:ef:01:02", "10.0.26100.1" })
        {
            Assert.AreEqual(text, DiagnosticsRedactor.Redact(text, null, User), text);
        }
    }

    [TestMethod]
    public void AUserNameWithASpaceInAProfilePathIsTakenWhole()
    {
        string result = DiagnosticsRedactor.Redact(@"saved to C:\Users\Jo Bloggs\Documents\x.txt and done", null, "Jo Bloggs");

        Assert.AreEqual("saved to <path> and done", result);
    }

    [TestMethod]
    public void TheUserNameIsRemovedAnywhereAsAWholeWordOnly()
    {
        Assert.AreEqual("hello <name>, <name>.", DiagnosticsRedactor.Redact("hello Alexander, alexander.", null, "Alexander"));
        Assert.AreEqual("alexanderplatz", DiagnosticsRedactor.Redact("alexanderplatz", null, "Alexander"));
    }

    [TestMethod]
    public void WhenNoUserNameIsGivenTheEnvironmentsIsUsed()
    {
        string name = Environment.UserName;
        if (name.Length < 2)
        {
            Assert.Inconclusive("The test runner's user name is too short to be removed by name.");
        }

        string text = DiagnosticsRedactor.Redact("run by " + name + " today");

        Assert.IsFalse(text.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ANameThatSpellsATagWordDoesNotDamageTheTags()
    {
        string once = DiagnosticsRedactor.Redact("to " + AddressColon + " from Address", ["address"], User);

        Assert.AreEqual("to <address> from <name>", once);
        Assert.AreEqual(once, DiagnosticsRedactor.Redact(once, ["address"], User));
    }

    [TestMethod]
    public void RedactIsIdempotent()
    {
        string all = string.Join("\n", FakeLog().Concat(FakeFrameLog()));
        string[] names = [DeviceName, WindowTitle, "name", "path", "id"];

        string once = DiagnosticsRedactor.Redact(all, names, User);
        string twice = DiagnosticsRedactor.Redact(once, names, User);

        Assert.AreEqual(once, twice);
        Assert.AreEqual(once, DiagnosticsRedactor.Redact(DiagnosticsRedactor.Redact(twice, names, User), names, User));
    }

    [TestMethod]
    public void NullAndEmptyTextGiveAnEmptyString()
    {
        Assert.AreEqual(string.Empty, DiagnosticsRedactor.Redact(null));
        Assert.AreEqual(string.Empty, DiagnosticsRedactor.Redact(string.Empty, ["x"], User));
    }

    [TestMethod]
    public void OnlyTheLastLinesOfALongLogAreKeptAndNoFrameLogSaysSo()
    {
        string[] lines = Enumerable.Range(1, 500).Select(i => "line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        string text = DiagnosticsText.Build("1.4.0", "Windows", lines, NullFrameLogSource.Instance, maxLogLines: 3, userName: User);

        StringAssert.Contains(text, "line 500\n");
        StringAssert.Contains(text, "line 498\n");
        Assert.IsFalse(text.Contains("line 497\n", StringComparison.Ordinal));
        StringAssert.Contains(text, "Frame log\n" + DiagnosticsText.NoFrameLog);
    }

    [TestMethod]
    public void ALogFileIsReadFromItsEndWhileTheLogCouldStillBeWritingToIt()
    {
        using var temp = new TempFolder();
        string path = temp.File("earshot.log");
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(1, 5).Select(i => "entry " + i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "\n");

        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        IReadOnlyList<string> lines = DiagnosticsText.ReadLogLines(path, out string? failure);

        Assert.IsNull(failure);
        Assert.HasCount(5, lines);
        Assert.AreEqual("entry 1", lines[0]);
        Assert.AreEqual("entry 5", lines[4]);
        Assert.IsEmpty(DiagnosticsText.ReadLogLines(temp.File("nothing.log"), out string? none));
        Assert.IsNull(none);
    }
}
