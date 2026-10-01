using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// The project rule behind this file: for every helper a self-test fakes, keep one execution of
// the real one. Nothing fakes Get-PowerCycleEvidence.ps1 yet, but it is the one other process
// ChildRunner starts (RunPowerCycleProbe), so it gets the same treatment as the real-launcher and
// real-prompt-helper tests: a real Windows PowerShell 5.1 process, the real script, read-only, no
// elevation, no device (read-only probes are always allowed while working on this repository).
[TestClass]
public sealed class PowerCycleEvidenceRealProbeTests
{
    [TestMethod]
    public void TheRealScriptReturnsJsonThatPowerCycleCanDecideWithoutThrowing()
    {
        string host = PowerShell51.ExecutablePath();
        if (!File.Exists(host))
        {
            Assert.Inconclusive("Windows PowerShell 5.1 is not installed at " + host + ".");
        }

        string scriptPath = Path.Combine(RepositoryLocator.RepositoryRoot(), "tools", "live-tests", "gui", "Get-PowerCycleEvidence.ps1");
        Assert.IsTrue(File.Exists(scriptPath), "fixture script missing: " + scriptPath);

        // Ten years back is well before this machine's log window, so the script reads everything the log still holds. The
        // System log is circular: it keeps the newest rows up to its size limit and drops the oldest, so a start
        // (Kernel-General 12) from days ago is gone once enough newer rows have been written. Every loopback listener a
        // test opens writes four Microsoft-Windows-HttpService rows, and a day of test runs is enough to wrap the log.
        // What a real machine's log holds is therefore not known in advance, and the test reads it a second way (the
        // framework's own event log reader, with an XPath filter instead of Get-WinEvent's hashtable) to know it. A script
        // that filtered too narrowly would disagree with that read, whether the log holds ten starts or none.
        DateTimeOffset since = DateTimeOffset.UtcNow.AddYears(-10);

        (int Power109, int General12, int Boot27) before = CountRows();

        string output = ChildRunner.RunPowerCycleProbe(host, scriptPath, since, TimeSpan.FromSeconds(30));

        (int Power109, int General12, int Boot27) after = CountRows();

        using JsonDocument document = JsonDocument.Parse(output);
        Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind, "not a JSON object: " + output);

        // Either shape is an honest answer from a real machine: a readable log (arrays, possibly
        // empty) or a genuine read error. Both must decide without throwing.
        bool hasPower109 = document.RootElement.TryGetProperty("kernelPower109", out JsonElement power109);
        bool hasGeneral12 = document.RootElement.TryGetProperty("kernelGeneral12", out JsonElement general12);
        bool hasBoot27 = document.RootElement.TryGetProperty("kernelBoot27", out JsonElement boot27);
        bool hasArrays = hasPower109 && hasGeneral12 && hasBoot27;
        bool hasError = document.RootElement.TryGetProperty("error", out _);
        Assert.IsTrue(hasArrays || hasError, "neither the arrays nor an error member were present: " + output);

        PowerCycleVerdict verdict = PowerCycle.Decide(output);
        Assert.IsTrue(Enum.IsDefined(verdict));

        if (!hasArrays)
        {
            return;
        }

        if (before != after)
        {
            // Rows were written or dropped while the script ran (the log is circular and other runs write to it), so the
            // two reads cannot be compared. Nothing was proved either way.
            Assert.Inconclusive("The System log changed while it was read: " + before + " before, " + after + " after.");
        }

        Assert.AreEqual(before.Power109, power109.GetArrayLength(), "Kernel-Power 109 rows: the script and the framework's reader disagree: " + output);
        Assert.AreEqual(before.General12, general12.GetArrayLength(), "Kernel-General 12 rows: the script and the framework's reader disagree: " + output);
        Assert.AreEqual(before.Boot27, boot27.GetArrayLength(), "Kernel-Boot 27 rows: the script and the framework's reader disagree: " + output);

        // Rule 2 of the verdict: no start at all is "not-yet", and any start is not.
        if (before.General12 == 0)
        {
            Assert.AreEqual(PowerCycleVerdict.NotYet, verdict, "no Kernel-General 12 start in the log, so the verdict is not-yet: " + output);

            // The log holds no start, so this run cannot show that the script reads a populated log. That is a fact about
            // the machine, not a fault in the script (the read agreed with the framework's), so it is reported as such.
            Assert.Inconclusive("This machine's System log holds no Kernel-General 12 start (the log has wrapped), so a read of a populated log was not exercised.");
        }

        // With a start in the log this pins that the read is live rather than an accidentally narrow filter returning nothing.
        Assert.AreNotEqual(PowerCycleVerdict.NotYet, verdict, "a Kernel-General 12 start is in the log but the verdict says there is none: " + output);
    }

    // How many rows of each kind the System log holds, read with the framework's event log reader.
    private static (int Power109, int General12, int Boot27) CountRows() =>
        (CountRows("Microsoft-Windows-Kernel-Power", 109), CountRows("Microsoft-Windows-Kernel-General", 12), CountRows("Microsoft-Windows-Kernel-Boot", 27));

    private static int CountRows(string provider, int id)
    {
        var query = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='" + provider + "'] and EventID=" + id + "]]");
        using var reader = new EventLogReader(query);
        int count = 0;
        for (EventRecord? row = reader.ReadEvent(); row is not null; row = reader.ReadEvent())
        {
            row.Dispose();
            count++;
        }

        return count;
    }
}
