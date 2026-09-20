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

        // Ten years back is well before this machine's log window (test-gui.md section 9.3
        // records ten transitions over six days on 2026-09-20), which proves the real script and
        // its "no events were found" handling both run clean over a real, populated log rather
        // than an artificially narrow or empty one.
        DateTimeOffset since = DateTimeOffset.UtcNow.AddYears(-10);

        string output = ChildRunner.RunPowerCycleProbe(host, scriptPath, since, TimeSpan.FromSeconds(30));

        using JsonDocument document = JsonDocument.Parse(output);
        Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind, "not a JSON object: " + output);

        // Either shape is an honest answer from a real machine: a readable log (arrays, possibly
        // empty) or a genuine read error. Both must decide without throwing.
        bool hasArrays = document.RootElement.TryGetProperty("kernelPower109", out _) &&
            document.RootElement.TryGetProperty("kernelGeneral12", out _);
        bool hasError = document.RootElement.TryGetProperty("error", out _);
        Assert.IsTrue(hasArrays || hasError, "neither the arrays nor an error member were present: " + output);

        PowerCycleVerdict verdict = PowerCycle.Decide(output);
        Assert.IsTrue(Enum.IsDefined(verdict));

        if (hasArrays)
        {
            // Ten years of real history on a machine that is regularly shut down and started: a
            // start (Kernel-General 12) must exist, so "not-yet" (rule 2, "no start at all") is
            // ruled out here specifically, pinning that this is a live, non-empty read rather than
            // an accidentally narrow filter silently returning nothing.
            Assert.AreNotEqual(PowerCycleVerdict.NotYet, verdict, "no Kernel-General 12 start found in ten years of real history: " + output);
        }
    }
}
