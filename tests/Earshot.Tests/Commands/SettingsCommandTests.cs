using System.Diagnostics;
using Earshot.Commands;
using Earshot.Contracts;
using Earshot.Infra;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Commands;

[TestClass]
public sealed class SettingsCommandTests
{
    private static (int Code, string Text) Run(JsonSettingsStore store, params string[] args)
    {
        var writer = new StringWriter();
        int code = SettingsCommand.Run(args, store, writer);
        return (code, writer.ToString());
    }

    private static JsonSettingsStore NewStore(TempFolder temp) => new(temp.File("settings.json"), new CapturingLog());

    [TestMethod]
    public void ListNamesEverySettingWithItsValueAndWhatItAccepts()
    {
        using var temp = new TempFolder();
        var store = NewStore(temp);

        (int code, string text) = Run(store, "list");

        Assert.AreEqual(0, code);
        foreach (SettingsCatalog.SettingDefinition d in SettingsCatalog.All)
        {
            StringAssert.Contains(text, d.Name + " = ");
            StringAssert.Contains(text, d.Accepts);
        }

        StringAssert.Contains(text, "low-battery-percent = 20");
        StringAssert.Contains(text, "shortcut-connect = Ctrl+Alt+Shift+A");
    }

    [TestMethod]
    public void GetPrintsOneValue()
    {
        using var temp = new TempFolder();

        (int code, string text) = Run(NewStore(temp), "get", "gauge-position");

        Assert.AreEqual(0, code);
        Assert.AreEqual("right-end", text.Trim());
    }

    // One accepted value for each setting, and what get then reads.
    private static readonly (string Name, string Value, string Read)[] Accepted =
    [
        ("gauge-position", "next-to-apps", "next-to-apps"),
        ("gauge-order", "bolt-number-ring", "bolt-number-ring"),
        ("other-device-name", "Pixel", "Pixel"),
        ("pause-when-bud-out", "off", "off"),
        ("pause-when-airpods-leave", "false", "off"),
        ("low-battery-alerts", "off", "off"),
        ("low-battery-percent", "40", "40"),
        ("fully-charged-notice", "off", "off"),
        ("left-click-connects", "on", "on"),
        ("case-open-card", "off", "off"),
        ("case-open-card-close", "30", "30"),
        ("check-automatically", "on", "on"),
        ("shortcut-connect", "Ctrl+Alt+Shift+Q", "Ctrl+Alt+Shift+Q"),
        ("shortcut-disconnect", "none", "none"),
        ("shortcut-card", "Ctrl+Alt+Shift+W", "Ctrl+Alt+Shift+W"),
    ];

    [TestMethod]
    public void EverySettingTheCatalogHasTakesAValueAndKeepsItInTheFile()
    {
        CollectionAssert.AreEquivalent(
            SettingsCatalog.All.Select(d => d.Name).ToArray(),
            Accepted.Select(a => a.Name).ToArray(),
            "A setting was added or removed without a row here.");

        using var temp = new TempFolder();
        var store = NewStore(temp);
        foreach ((string name, string value, string read) in Accepted)
        {
            (int code, string text) = Run(store, "set", name, value);
            Assert.AreEqual(0, code, name + ": " + text);
        }

        // A second store reads the file, as a tray or the next command would.
        var reread = NewStore(temp);
        foreach ((string name, _, string read) in Accepted)
        {
            Assert.AreEqual(read, Run(reread, "get", name).Text.Trim(), name);
        }
    }

    [TestMethod]
    public void TheWatcherFlagFollowsTheSettingsThatNeedIt()
    {
        using var temp = new TempFolder();
        var store = NewStore(temp);

        Run(store, "set", "case-open-card", "off");
        Run(store, "set", "fully-charged-notice", "off");
        Run(store, "set", "low-battery-alerts", "off");
        Run(store, "set", "pause-when-bud-out", "off");
        Assert.IsTrue(store.Current.Widget.Enabled, "The gauge is still shown.");
        Assert.IsTrue(store.Current.Widget.ShowOnTaskbar);

        Run(store, "set", "case-open-card", "on");
        Assert.IsTrue(store.Current.Widget.Enabled);
    }

    [TestMethod]
    [DataRow("low-battery-percent", "15")]
    [DataRow("low-battery-percent", "100")]
    [DataRow("low-battery-percent", "0")]
    [DataRow("low-battery-percent", "-10")]
    [DataRow("low-battery-percent", "abc")]
    [DataRow("case-open-card-close", "7")]
    [DataRow("case-open-card-close", "0")]
    [DataRow("gauge-position", "middle")]
    [DataRow("gauge-order", "7")]
    [DataRow("pause-when-bud-out", "maybe")]
    [DataRow("other-device-name", "a name that is far longer than forty characters in all")]
    [DataRow("other-device-name", "bad\u0007name")]
    [DataRow("shortcut-card", "Ctrl+Alt+Shift+")]
    [DataRow("shortcut-card", "F12")]
    [DataRow("shortcut-card", "Ctrl+Alt+Shift+A")]
    public void AValueOutOfRangeIsRefusedWithAMessageAndASavesNothing(string name, string value)
    {
        using var temp = new TempFolder();
        var store = NewStore(temp);
        string before = File.ReadAllText(temp.File("settings.json"));

        (int code, string text) = Run(store, "set", name, value);

        Assert.AreEqual(ExitCodes.Usage, code, text);
        StringAssert.StartsWith(text, "Not changed.");
        StringAssert.Contains(text, name + " accepts:");
        Assert.AreEqual(before, File.ReadAllText(temp.File("settings.json")), "The file must not change.");
    }

    [TestMethod]
    public void AnUnknownNameIsRefusedAndTheMessageListsTheRealOnes()
    {
        using var temp = new TempFolder();
        var store = NewStore(temp);

        foreach (string verb in new[] { "get", "set" })
        {
            string[] args = verb == "get" ? [verb, "volume"] : [verb, "volume", "11"];
            (int code, string text) = Run(store, args);
            Assert.AreEqual(ExitCodes.Usage, code);
            StringAssert.Contains(text, "There is no setting called \"volume\".");
            StringAssert.Contains(text, "low-battery-percent");
        }
    }

    [TestMethod]
    [DataRow]
    [DataRow("frobnicate")]
    [DataRow("get")]
    [DataRow("set", "low-battery-percent")]
    [DataRow("list", "extra")]
    public void ALineThatIsNotListGetOrSetPrintsTheUsageAndExitsNonZero(params string[] args)
    {
        using var temp = new TempFolder();

        (int code, string text) = Run(NewStore(temp), args);

        Assert.AreEqual(ExitCodes.Usage, code);
        StringAssert.Contains(text, SettingsCommand.Usage);
    }

    [TestMethod]
    public void AFileFromANewerEarshotIsLeftAlone()
    {
        using var temp = new TempFolder();
        string newer = "{ \"SchemaVersion\": 99, \"DeviceMatch\": \"AirPods\" }";
        File.WriteAllText(temp.File("settings.json"), newer);
        var store = NewStore(temp);

        (int code, string text) = Run(store, "set", "low-battery-percent", "30");

        Assert.AreEqual(ExitCodes.Refused, code, text);
        Assert.AreEqual(newer, File.ReadAllText(temp.File("settings.json")));
    }

    // ----- the real program, once -----

    private static (int Code, string Text) RunProgram(TempFolder temp, params string[] args)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "Earshot.exe");
        if (!File.Exists(exe))
        {
            Assert.Inconclusive("The test build has no Earshot.exe beside the tests: " + exe);
        }

        string output = temp.File("out-" + Guid.NewGuid().ToString("N") + ".txt");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        info.ArgumentList.Add("--out");
        info.ArgumentList.Add(output);
        info.Environment["EARSHOT_SAFE_MODE"] = "1";
        info.Environment["EARSHOT_DATA_ROOT"] = temp.File("data");
        using Process process = Process.Start(info)!;
        Assert.IsTrue(process.WaitForExit(TimeSpan.FromSeconds(60)), "Earshot.exe did not end.");
        return (process.ExitCode, File.Exists(output) ? File.ReadAllText(output) : "");
    }

    [TestMethod]
    public void TheRealProgramSetsReadsAndRefusesThroughItsOwnVerb()
    {
        using var temp = new TempFolder();

        (int bad, string badText) = RunProgram(temp, "settings", "set", "low-battery-percent", "15");
        Assert.AreEqual(ExitCodes.Usage, bad, badText);

        (int set, string setText) = RunProgram(temp, "settings", "set", "low-battery-percent", "50");
        Assert.AreEqual(0, set, setText);

        (int get, string getText) = RunProgram(temp, "settings", "get", "low-battery-percent");
        Assert.AreEqual(0, get, getText);
        Assert.AreEqual("50", getText.Trim());

        (int unknown, string unknownText) = RunProgram(temp, "settings", "get", "volume");
        Assert.AreEqual(ExitCodes.Usage, unknown, unknownText);
    }

    [TestMethod]
    public void TheRealProgramsDoctorVerbPrintsTheSummaryAndTheJson()
    {
        using var temp = new TempFolder();

        (int code, string text) = RunProgram(temp, "doctor");

        Assert.AreEqual(0, code, text);
        StringAssert.StartsWith(text, "Earshot doctor");
        StringAssert.Contains(text, "```json");
        StringAssert.Contains(text, "\"schema\": 1");
    }

    [TestMethod]
    public void OnlySetCanSaveSettings()
    {
        Assert.IsTrue(SettingsCommand.Writes(["set", "low-battery-percent", "30"]));
        Assert.IsFalse(SettingsCommand.Writes(["list"]));
        Assert.IsFalse(SettingsCommand.Writes(["get", "gauge-position"]));
        Assert.IsFalse(SettingsCommand.Writes([]));
    }

    [TestMethod]
    public void TheRealProgramsListAndGetLeaveAHalfEditedFileWhereItIs()
    {
        using var temp = new TempFolder();
        string folder = Path.Combine(temp.File("data"), "Roaming", "Earshot");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "settings.json");
        File.WriteAllText(file, "{ \"DeviceMatch\": \"Lat");

        (int list, string listText) = RunProgram(temp, "settings", "list");
        Assert.AreEqual(0, list, listText);
        (int get, string getText) = RunProgram(temp, "settings", "get", "low-battery-percent");
        Assert.AreEqual(0, get, getText);

        Assert.AreEqual("{ \"DeviceMatch\": \"Lat", File.ReadAllText(file), "A read must not rewrite a file someone is editing.");
        Assert.IsEmpty(Directory.GetFiles(folder, "settings.corrupt-*"), "A read must not move the file aside.");
        Assert.IsFalse(File.Exists(file + ".bak"));
    }
}
