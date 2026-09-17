using System.Globalization;
using System.Text.Json;
using Earshot.App;
using Earshot.Boot.Gate;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Tests.Phase4;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.LiveTests;

// The live test scripts read their answers out of the JSON Earshot writes. A name the scripts read
// that no writer emits reads as $null for ever: the criterion built on it is decided on nothing, and
// it decides the wrong way quietly. That is not a parse error and no other test here would see it,
// so the names are checked against the writers that produce them.
//
// Nothing is run: the node report is built from the recorded fake table, and the other reports are
// checked by reading the writer's own source for the names it writes.
[TestClass]
public sealed class LiveTestFieldTests
{
    // The shared step writer lives beside the audio probe, so a report that writes steps names it too.
    private const string AudioProbe = "src/Earshot/Audio/ProbeAudio.cs";
    private const string NodesProbe = "src/Earshot/Boot/ProbeNodes.cs";

    // What each report writes, and the names the scripts read out of it. Grouped by the report, because
    // a name is only meaningful against the writer it is read from: configFlagsDisabled is written by
    // diag gate, and reading it from probe nodes was silently always null until probe nodes wrote it too.
    private static readonly ReportShape[] Reports =
    [
        new(
            "probe nodes",
            [NodesProbe],
            ["address", "configFlagsDisabled", "container", "instanceId", "nodeState", "nodes", "present", "problem", "status", "target"]),
        new(
            "probe audio",
            [AudioProbe],
            ["connection", "containerId", "device", "endpoints", "flow", "friendlyName", "groups", "resolution", "state"]),
        new(
            "probe topology",
            ["src/Earshot/Audio/ProbeTopology.cs", AudioProbe],
            ["adapterId", "adapters", "guardPassed", "ksControlActivated", "pinCount"]),
        new(
            "probe services",
            ["src/Earshot/Protection/ProbeServices.cs", AudioProbe],
            ["complete", "connected", "guid", "installedServices", "label", "protection"]),
        new(
            "probe task",
            ["src/Earshot/Boot/ProbeTask.cs"],
            ["lastTaskResult", "logonType", "path", "present", "runLevel", "setUp", "tasks", "trayMayRun", "userId", "userMask"]),
        new(
            "probe battery",
            ["src/Earshot/App/ProbeBattery.cs"],
            ["hasSource", "hasValue"]),
        new(
            "diag gate evidence",
            ["src/Earshot/Boot/DiagGate.cs", NodesProbe],
            ["exitCode", "lastTaskResult", "lastTaskResultCode", "nonce", "outcome", "result", "runMilliseconds", "statusFile"]),
        new(
            "diag connect and ks evidence",
            ["src/Earshot/Audio/Connect/DiagConnect.cs", AudioProbe],
            [
                "accepted", "action", "apartment", "bytesReturned", "callMilliseconds", "confirmation", "error",
                "filterChoice", "filters", "guardPassed", "hr", "hrName", "kind", "ksControlActivated",
                "millisecondsAfterRequest", "millisecondsToFirstWantedStateNotification", "name", "newState",
                "notSentReason", "notifications", "payload", "reached", "requestSent", "role", "sendFault",
                "sequence", "source", "threadId", "unreachable", "utc",
            ]),
        new(
            "diag protect evidence",
            ["src/Earshot/Protection/DiagProtectUnelevated.cs", AudioProbe],
            ["code", "codeName", "detail", "step", "steps"]),
        new(
            "diag battery-sweep evidence",
            ["src/Earshot/App/DiagBatterySweep.cs"],
            ["containerIdReadOnAMatchedNode", "friendlyName", "instanceId", "keys", "matched", "present", "watchedValues"]),
    ];

    // The scripts also read the small files Earshot keeps beside the application. Those are serialised
    // from these types, so the member is the name, and reflection is the check.
    private static readonly Type[] StoredFiles =
    [
        typeof(EarshotSettings),
        typeof(GateConfig),
        typeof(DeviceIdentity),
        typeof(ProtectionRecord),
    ];

    // Names that come from neither. The startup value under the user's Run key is called Earshot, and
    // HiberbootEnabled is the registry value Windows keeps Fast Startup in, which the two tests that
    // power the machine down read so their evidence says which kind of shutdown it was.
    private static readonly string[] NotJson = ["Earshot", "HiberbootEnabled"];

    private static ServiceRegistry NoServices() => throw new AssertFailedException("Writing a report needs no services.");

    // The one the blocker went through, checked on the real writer rather than on its source: the
    // scripts that settle whether a block took, and whether it survived a restart, count the nodes
    // whose configFlagsDisabled is true.
    [TestMethod]
    public void TheNodeReportCarriesEveryNameTheBlockTestsCountOn()
    {
        using var temp = new TempFolder();
        FakeNodeApi table = RecordedNodes.Table();
        foreach (string target in RecordedNodes.AirPodsTargets)
        {
            table[target].MarkDisabled(persistent: true);
        }

        Program.NodesProbeReport report = Program.ReadNodesProbe(
            table,
            new EarshotSettings { PinnedAddress = RecordedNodes.AirPodsAddress, PinnedContainerId = RecordedNodes.AirPodsContainer },
            new GateStore(temp.Path));

        using var json = new StringWriter(CultureInfo.InvariantCulture);
        Program.WriteNodesProbe(new ProbeContext("nodes", json, json: true, NoServices), report);

        using JsonDocument document = JsonDocument.Parse(json.ToString());
        int disabled = 0;
        int targets = 0;
        foreach (JsonElement node in document.RootElement.GetProperty("nodes").EnumerateArray())
        {
            foreach (string name in new[] { "instanceId", "target", "present", "status", "problem", "configFlags", "configFlagsDisabled" })
            {
                Assert.IsTrue(
                    node.TryGetProperty(name, out _),
                    "probe nodes writes no " + name + ", which the live tests read out of every node.");
            }

            if (!node.GetProperty("target").GetBoolean())
            {
                continue;
            }

            targets++;
            if (node.GetProperty("configFlagsDisabled").GetBoolean())
            {
                disabled++;
            }
        }

        Assert.AreEqual(RecordedNodes.AirPodsTargets.Length, targets);
        Assert.AreEqual(
            targets,
            disabled,
            "Every target node was disabled with the persistent flag, so every one has to read back as carrying the bit.");
    }

    [TestMethod]
    public void EveryNameAReportIsReadForIsWrittenByThatReport()
    {
        var problems = new List<string>();
        foreach (ReportShape report in Reports)
        {
            HashSet<string> written = NamesWrittenBy(report);
            foreach (string field in report.Fields)
            {
                if (!written.Contains(field))
                {
                    problems.Add("the live tests read \"" + field + "\" out of " + report.What +
                        ", which writes no such name (" + string.Join(", ", report.Sources) + ")");
                }
            }
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    // Every name a script reads has to be declared above against the report it comes from. A new read
    // that nobody classified fails here rather than returning null on the owner's machine.
    [TestMethod]
    public void EveryNameTheScriptsReadIsAccountedFor()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReportShape report in Reports)
        {
            declared.UnionWith(report.Fields);
        }

        foreach (Type type in StoredFiles)
        {
            foreach (System.Reflection.PropertyInfo property in type.GetProperties())
            {
                declared.Add(property.Name);
            }
        }

        declared.UnionWith(NotJson);

        var problems = new List<string>();
        foreach (LiveTestScriptTests.FieldRead read in LiveTestScriptTests.FieldReads)
        {
            foreach (string name in read.Names)
            {
                if (!declared.Contains(name))
                {
                    problems.Add(read.File + " line " + read.Line.ToString(CultureInfo.InvariantCulture) +
                        " reads \"" + name + "\", which is not declared against any report in this test.");
                }
            }
        }

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
        Assert.IsGreaterThan(0, LiveTestScriptTests.FieldReads.Count, "No field read was found in the scripts, which cannot be right.");
    }

    [TestMethod]
    public void EveryStoredFileNameTheScriptsReadIsAMemberOfItsType()
    {
        foreach ((string name, Type type) in new[]
        {
            ("BlockAtBoot", typeof(GateConfig)),
            ("ProtectAudioQuality", typeof(EarshotSettings)),
            ("Address", typeof(DeviceIdentity)),
            ("ContainerId", typeof(DeviceIdentity)),
            ("DisabledServices", typeof(ProtectionRecord)),
        })
        {
            Assert.IsNotNull(type.GetProperty(name), type.Name + " has no " + name + ", so the live tests read a name its file does not carry.");
        }
    }

    // The names a writer writes, read from its source. A JSON name is the first argument of a Write
    // call, or the one after the writer, so a literal that follows "(" or "," on a line that writes is
    // taken as a name. That takes in a few written values as well, which is the safe direction: this
    // test can miss a nesting mistake, but it cannot fail for a name the writer really does write.
    private static HashSet<string> NamesWrittenBy(ReportShape report)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string source in report.Sources)
        {
            string path = Path.Combine(RepositoryRoot(), Path.Combine(source.Split('/')));
            Assert.IsTrue(File.Exists(path), path + " is gone, so " + report.What + " cannot be checked.");
            foreach (string line in File.ReadLines(path))
            {
                if (line.Contains("Write", StringComparison.Ordinal))
                {
                    AddLiterals(names, line);
                }
            }
        }

        return names;
    }

    private static void AddLiterals(HashSet<string> names, string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != '"')
            {
                continue;
            }

            int end = line.IndexOf('"', i + 1);
            if (end < 0)
            {
                return;
            }

            int before = i - 1;
            while (before >= 0 && line[before] == ' ')
            {
                before--;
            }

            if (before >= 0 && (line[before] == '(' || line[before] == ','))
            {
                names.Add(line[(i + 1)..end]);
            }

            i = end;
        }
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

    private sealed record ReportShape(string What, string[] Sources, string[] Fields);
}
