using System.Diagnostics;
using System.Text.Json;
using Earshot.App;
using Earshot.Boot.Gate;
using Earshot.Contracts;
using Earshot.Infra;
using Earshot.Tests.Phase4;
using Earshot.Tray;
using Earshot.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// probe setup-values: which device an install would be given, and what is installed. The choice is a pure function
// over three inputs and is tested with fakes; the readers run against temporary folders; and the real program is run once
// (read-only, with safe mode and a temporary data root set), because a fake at a boundary proves everything except the
// boundary.
[TestClass]
public sealed class ProbeSetupValuesTests
{
    private static readonly Guid PodsContainer = new("5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13");
    private static readonly Guid OtherContainer = new("aaaaaaaa-bbbb-4ccc-8ddd-000000000001");

    private static readonly string[] SetupValuesTarget = ["setup-values"];
    private static readonly string[] NotReadyReasons = ["not-paired", "several", "unreadable"];
    private static readonly string[] Sources = ["machine", "settings", "paired", ""];
    private static readonly string[] InstallStates = ["nothing", "usable", "unusable"];

    private const string PodsAddress = "0A1B2C3D4E8C";
    private const string OtherAddress = "0A1B2C3D4E90";

    private static PairedDevice Device(string name, string address, Guid container) =>
        new(@"BTHENUM\DEV_" + address + @"\b&1a2b3c4d&0&BLUETOOTHDEVICE_" + address, name, address, container, IsPresent: true);

    private static EarshotSettings Settings(string match = "AirPods", string address = "", Guid? container = null) =>
        new() { DeviceMatch = match, PinnedAddress = address, PinnedContainerId = container ?? Guid.Empty };

    private static DeviceIdentity Identity(string address, Guid container) => new() { Address = address, ContainerId = container };

    // ----- the command line -----

    [TestMethod]
    public void TheTargetIsNamedOnlyAndItsReportIsAlwaysJson()
    {
        Assert.IsTrue(Program.TryParseProbeArgs(["probe", "setup-values", "--out", @"C:\x\setup.json"], out Program.ProbeRequest? request, out string? error), error);

        CollectionAssert.AreEqual(SetupValuesTarget, request.Targets.ToArray());
        Assert.IsTrue(request.Json, "A script reads it, so there is no text form with a heading above it.");
        Assert.AreEqual(@"C:\x\setup.json", request.OutPath);

        Assert.IsTrue(Program.TryParseProbeArgs(["probe", "all"], out Program.ProbeRequest? all, out _));
        CollectionAssert.DoesNotContain(all.Targets.ToArray(), "setup-values");
        Assert.IsFalse(Program.TryParseProbeArgs(["probe", "setup-values", "battery"], out _, out _), "One target at a time.");
        StringAssert.Contains(Program.ProbeUsage, "probe setup-values");
    }

    // ----- the choice -----

    [TestMethod]
    public void TheMachinesDeviceFileWinsWhileItNamesAPairedDevice()
    {
        PairedDevice[] paired = [Device("AirPods Pro", PodsAddress, PodsContainer), Device("AirPods Max", OtherAddress, OtherContainer)];

        SetupDeviceChoice choice = SetupValues.Select(Identity(OtherAddress, OtherContainer), Settings(address: PodsAddress, container: PodsContainer), paired, pairedUnreadable: false);

        Assert.IsTrue(choice.Ready);
        Assert.AreEqual("machine", choice.Source);
        Assert.AreEqual(OtherAddress, choice.Address);
        Assert.AreEqual(OtherContainer, choice.ContainerId);
    }

    [TestMethod]
    public void AMachineFileForADeviceThatIsNoLongerPairedFallsToTheSettingsPin()
    {
        PairedDevice[] paired = [Device("AirPods Pro", PodsAddress, PodsContainer)];

        SetupDeviceChoice choice = SetupValues.Select(Identity(OtherAddress, OtherContainer), Settings(address: PodsAddress, container: PodsContainer), paired, false);

        Assert.AreEqual("settings", choice.Source);
        Assert.AreEqual(PodsAddress, choice.Address);
    }

    [TestMethod]
    public void AMachineFileWhoseContainerDoesNotMatchTheAddressIsNotUsed()
    {
        PairedDevice[] paired = [Device("AirPods Pro", PodsAddress, PodsContainer)];

        SetupDeviceChoice choice = SetupValues.Select(Identity(PodsAddress, OtherContainer), Settings(), paired, false);

        Assert.AreEqual("paired", choice.Source, "The address is paired but in another container, so the file is not evidence.");
        Assert.AreEqual(PodsContainer, choice.ContainerId);
    }

    [TestMethod]
    public void ASettingsPinThatOutlivedItsDeviceIsNotUsed()
    {
        SetupDeviceChoice choice = SetupValues.Select(null, Settings(address: PodsAddress, container: PodsContainer), [], false);

        Assert.IsFalse(choice.Ready);
        Assert.AreEqual("not-paired", choice.Reason);
        Assert.AreEqual("", choice.Address);
        Assert.AreEqual(Guid.Empty, choice.ContainerId);
    }

    [TestMethod]
    public void ExactlyOnePairedDeviceNamedLikeTheMatchIsChosenWhateverTheCase()
    {
        PairedDevice[] paired = [Device("My AIRPODS Pro", PodsAddress, PodsContainer), Device("Keyboard", OtherAddress, OtherContainer)];

        SetupDeviceChoice choice = SetupValues.Select(null, Settings(), paired, false);

        Assert.IsTrue(choice.Ready);
        Assert.AreEqual("paired", choice.Source);
        Assert.AreEqual(PodsAddress, choice.Address);
        Assert.AreEqual(PodsContainer, choice.ContainerId);
    }

    [TestMethod]
    public void TwoNodesOfOneAddressAreOneDevice()
    {
        PairedDevice[] paired = [Device("AirPods Pro", PodsAddress, PodsContainer), Device("AirPods Pro", PodsAddress, PodsContainer)];

        Assert.IsTrue(SetupValues.Select(null, Settings(), paired, false).Ready);
    }

    [TestMethod]
    public void SeveralDevicesNamedLikeTheMatchAreAQuestionNotAGuess()
    {
        PairedDevice[] paired = [Device("AirPods Pro", PodsAddress, PodsContainer), Device("AirPods Max", OtherAddress, OtherContainer)];

        SetupDeviceChoice choice = SetupValues.Select(null, Settings(), paired, false);

        Assert.IsFalse(choice.Ready);
        Assert.AreEqual("several", choice.Reason);
    }

    [TestMethod]
    public void NoDeviceNamedLikeTheMatchIsNotPairedAndAListThatCouldNotBeReadSaysSo()
    {
        PairedDevice[] paired = [Device("Keyboard", OtherAddress, OtherContainer)];

        Assert.AreEqual("not-paired", SetupValues.Select(null, Settings(), paired, pairedUnreadable: false).Reason);
        Assert.AreEqual("unreadable", SetupValues.Select(null, Settings(), [], pairedUnreadable: true).Reason);
    }

    [TestMethod]
    public void ADeviceInThePcContainerOrWithoutAnAddressIsNeverChosen()
    {
        PairedDevice[] paired =
        [
            Device("AirPods Pro", PodsAddress, NodeMatch.PcContainer),
            Device("AirPods Max", OtherAddress, Guid.Empty),
        ];

        SetupDeviceChoice choice = SetupValues.Select(null, Settings(), paired, false);

        Assert.IsFalse(choice.Ready);
        Assert.AreEqual("not-paired", choice.Reason);
    }

    [TestMethod]
    public void ABlankMatchChoosesNothingByName()
    {
        PairedDevice[] paired = [Device("AirPods Pro", PodsAddress, PodsContainer)];

        Assert.IsFalse(SetupValues.Select(null, Settings(match: " "), paired, false).Ready);
    }

    // ----- the report -----

    [TestMethod]
    public void TheReportHasTheFieldsTheScriptReads()
    {
        var report = new SetupValuesReport(
            TestUsers.Sid,
            new SetupDeviceChoice(true, "", PodsAddress, PodsContainer, "machine"),
            new SetupInstall("usable", "", "1.2.1"));

        using JsonDocument document = JsonDocument.Parse(ProbeContext.JsonText(w => SetupValues.Write(w, report)));
        JsonElement root = document.RootElement;

        Assert.AreEqual(1, root.GetProperty("schema").GetInt32());
        Assert.AreEqual(TestUsers.Sid, root.GetProperty("userSid").GetString());
        Assert.IsTrue(root.GetProperty("ready").GetBoolean());
        Assert.AreEqual("", root.GetProperty("reason").GetString());
        Assert.AreEqual(PodsAddress, root.GetProperty("address").GetString());
        Assert.AreEqual("5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13", root.GetProperty("containerId").GetString());
        Assert.AreEqual("machine", root.GetProperty("source").GetString());
        Assert.AreEqual("usable", root.GetProperty("install").GetProperty("state").GetString());
        Assert.AreEqual("", root.GetProperty("install").GetProperty("problem").GetString());
        Assert.AreEqual("1.2.1", root.GetProperty("install").GetProperty("version").GetString());
    }

    [TestMethod]
    public void ANotReadyReportCarriesEmptyDeviceFieldsAndTheReason()
    {
        var report = new SetupValuesReport(TestUsers.Sid, new SetupDeviceChoice(false, "several", "", Guid.Empty, ""), new SetupInstall("nothing", "", ""));

        using JsonDocument document = JsonDocument.Parse(ProbeContext.JsonText(w => SetupValues.Write(w, report)));
        JsonElement root = document.RootElement;

        Assert.IsFalse(root.GetProperty("ready").GetBoolean());
        Assert.AreEqual("several", root.GetProperty("reason").GetString());
        Assert.AreEqual("", root.GetProperty("address").GetString());
        Assert.AreEqual("", root.GetProperty("containerId").GetString());
    }

    // ----- the readers -----

    [TestMethod]
    public void TheMachineFoldersDeviceFileIsUsedOnlyWhenTheFolderPassesTheGatesCheck()
    {
        using var temp = new TempFolder();
        Paths paths = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? temp.Path : null);
        Directory.CreateDirectory(paths.MachineFolder);
        Assert.IsTrue(new GateStore(paths.MachineFolder).WriteDevice(Identity(PodsAddress, PodsContainer)).Ok);
        var log = new CapturingLog();

        DeviceIdentity? trusted = SetupValues.ReadMachineIdentity(paths, new FakeFolderSecurity(), log);
        var writable = new FakeFolderSecurity { SddlFor = _ => "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;0x1301bf;;;BU)" };
        DeviceIdentity? untrusted = SetupValues.ReadMachineIdentity(paths, writable, log);

        Assert.IsNotNull(trusted);
        Assert.AreEqual(PodsAddress, trusted.Address);
        Assert.AreEqual(PodsContainer, trusted.ContainerId);
        Assert.IsNull(untrusted, "A file in a folder ordinary programs can write is not evidence.");
        Assert.IsTrue(log.Has(LogLevel.Info, "the machine folder was not used"));
    }

    [TestMethod]
    public void AMissingOrInvalidDeviceFileIsNoIdentity()
    {
        using var temp = new TempFolder();
        Paths paths = Paths.FromEnvironment(name => name == Paths.DataRootVariable ? temp.Path : null);
        Directory.CreateDirectory(paths.MachineFolder);
        var log = new CapturingLog();

        Assert.IsNull(SetupValues.ReadMachineIdentity(paths, new FakeFolderSecurity(), log), "No file.");

        File.WriteAllText(paths.DeviceIdentityFile, "{ \"Address\": \"not an address\", \"ContainerId\": \"x\" }");
        Assert.IsNull(SetupValues.ReadMachineIdentity(paths, new FakeFolderSecurity(), log), "A file that does not validate.");
    }

    [TestMethod]
    public void TheInstallIsReadAsNothingUsableOrUnusable()
    {
        using var temp = new TempFolder();
        string install = temp.File(Path.Combine("ProgramFiles", "Earshot"));
        string exe = Path.Combine(install, "Earshot.exe");
        var folders = new FakeFolderSecurity();

        Assert.AreEqual("nothing", SetupValues.ReadInstall(exe, folders).State);

        Directory.CreateDirectory(install);
        SetupInstall missing = SetupValues.ReadInstall(exe, folders);
        Assert.AreEqual("unusable", missing.State);
        Assert.AreEqual("ProgramAbsent", missing.Problem);

        File.WriteAllText(exe, "not a program");
        SetupInstall usable = SetupValues.ReadInstall(exe, folders);
        Assert.AreEqual("usable", usable.State);
        Assert.AreEqual("", usable.Problem);
        Assert.AreEqual("", usable.Version, "A file with no version resource reports none.");

        folders.SddlFor = _ => "O:BAG:SYD:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;0x1301bf;;;BU)";
        SetupInstall writable = SetupValues.ReadInstall(exe, folders);
        Assert.AreEqual("unusable", writable.State);
        Assert.AreEqual("FolderNotTrusted", writable.Problem);
    }

    [TestMethod]
    public void AProgramsVersionIsItsInformationalVersionAsMajorMinorPatch()
    {
        string assembly = typeof(Program).Assembly.Location;
        ReleaseVersion? running = ReleaseVersion.Running(typeof(Program).Assembly);

        Assert.IsNotNull(running);
        Assert.AreEqual(running.Value.Major + "." + running.Value.Minor + "." + running.Value.Patch, SetupValues.ProgramVersion(assembly));
    }

    // ----- the real program, once -----

    [TestMethod]
    public void TheRealProgramWritesValidJsonForTheSetupValuesWithSafeModeAndATemporaryDataRoot()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Earshot.exe");
        if (!File.Exists(exe))
        {
            Assert.Inconclusive("The test build has no Earshot.exe beside the tests: " + exe);
        }

        using var temp = new TempFolder();
        string report = temp.File("setup.json");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("probe");
        info.ArgumentList.Add("setup-values");
        info.ArgumentList.Add("--out");
        info.ArgumentList.Add(report);
        info.Environment["EARSHOT_SAFE_MODE"] = "1";
        info.Environment["EARSHOT_DATA_ROOT"] = temp.File("data");

        using Process process = Process.Start(info)!;
        Assert.IsTrue(process.WaitForExit(TimeSpan.FromSeconds(60)), "probe setup-values did not end.");
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd());

        byte[] bytes = File.ReadAllBytes(report);
        Assert.IsFalse(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "No byte order mark.");
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        Assert.AreEqual(1, root.GetProperty("schema").GetInt32());
        Assert.IsTrue(Earshot.Boot.Sddl.IsUserSid(root.GetProperty("userSid").GetString()), "The user's SID.");
        bool ready = root.GetProperty("ready").GetBoolean();
        string reason = root.GetProperty("reason").GetString()!;
        if (ready)
        {
            Assert.AreEqual("", reason);
            Assert.IsTrue(BoundaryValidation.IsAddress12(root.GetProperty("address").GetString()));
            Assert.IsTrue(Guid.TryParseExact(root.GetProperty("containerId").GetString(), "D", out _));
        }
        else
        {
            CollectionAssert.Contains(NotReadyReasons, reason);
        }

        CollectionAssert.Contains(Sources, root.GetProperty("source").GetString());
        CollectionAssert.Contains(InstallStates, root.GetProperty("install").GetProperty("state").GetString());
        Assert.IsFalse(Directory.Exists(temp.File("data")) && Directory.GetFiles(temp.File("data"), "settings.json", SearchOption.AllDirectories).Length > 0,
            "A read-only probe writes no settings file.");
    }
}
