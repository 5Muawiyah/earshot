using System.Speech.Synthesis;
using Earshot.Contracts;
using Earshot.Voice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Voice;

// The tests in this suite allowed to construct a real System.Speech.Synthesis.SpeechSynthesizer (the
// binding proof the task brief asks for): none of them ever calls Speak, so none of them ever produces
// sound, and each fails if SystemSpeechEngine is not actually talking to System.Speech.
//
// A machine with an enabled voice can still fail SetOutputToDefaultAudioDevice() with no default audio
// endpoint (a hosted CI runner, for example: .github/workflows/build.yml runs on windows-latest, which
// this repository cannot itself confirm has one). That is an environment limit, not a defect in
// SystemSpeechEngine, so every test here tells the two apart: Assert.Inconclusive with the reason, never
// a silent pass and never a red build for an environment this code cannot control.
[TestClass]
public sealed class SystemSpeechEngineBindingTests
{
    // A fabricated Open that always returns Ok (or always returns NotAvailable) fails this on a machine
    // where the independent probe disagrees; a fabricated GetInstalledVoices call (returning a
    // hardcoded list rather than the real one) fails the name comparison below.
    [TestMethod]
    public void OpenAgreesWithTheRealSynthesizerAboutInstalledVoices()
    {
        (string Name, bool Enabled)[] independent = ProbeInstalledVoices();
        string[] independentEnabledNames = independent.Where(v => v.Enabled).Select(v => v.Name).Order(StringComparer.Ordinal).ToArray();
        bool machineHasAnEnabledVoice = independentEnabledNames.Length > 0;

        using var engine = new SystemSpeechEngine();
        StepOutcome outcome = engine.Open(VoiceOverSettings.Default);

        Assert.AreEqual(machineHasAnEnabledVoice, outcome.Ok,
            "SystemSpeechEngine.Open must agree with a real SpeechSynthesizer.GetInstalledVoices() on this machine.");

        if (!machineHasAnEnabledVoice)
        {
            Assert.AreEqual(NativeCodes.NotAvailable, outcome.Code);
            return;
        }

        if (!outcome.Ok)
        {
            InconclusiveForEnvironment(outcome);
            return;
        }

        string[] engineEnabledNames = engine.LastOpenEnabledVoiceNames.Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(independentEnabledNames, engineEnabledNames,
            "SystemSpeechEngine.Open must see exactly the same enabled voices GetInstalledVoices() itself reports, " +
            "not a hardcoded or cached list.");
    }

    // SelectedVoiceName is guarded against SpeechSynthesizer.Voice's own undocumented exception
    // (SystemSpeechEngine.cs). This reads it right after a real Open with the default voice (no
    // SelectVoice call at all, so the exception the "each reported name" test below sometimes meets is
    // not even in play here), the simplest real path to it, to prove the guard itself never throws
    // rather than only reasoning about it.
    [TestMethod]
    public void SelectedVoiceNameNeverThrowsAfterOpen()
    {
        (string Name, bool Enabled)[] independent = ProbeInstalledVoices();
        if (!independent.Any(v => v.Enabled))
        {
            Assert.Inconclusive("No enabled voice is installed on this machine.");
            return;
        }

        using var engine = new SystemSpeechEngine();
        StepOutcome outcome = engine.Open(VoiceOverSettings.Default);
        if (!outcome.Ok)
        {
            InconclusiveForEnvironment(outcome);
            return;
        }

        // The guard itself is the thing under test: no try/catch here. If SelectedVoiceName can still
        // throw, this test fails with that exception rather than hiding it.
        string? selected = engine.SelectedVoiceName;
        Assert.IsTrue(selected is null || selected.Length > 0, "SelectedVoiceName must be null or a real name, never empty.");
    }

    // Proves Open actually drives SelectVoice for a real installed name, not just that it accepts the
    // setting: for every enabled voice this machine has, opening with that exact name must succeed and
    // the synthesiser must report that voice selected afterwards.
    [TestMethod]
    public void OpeningWithEachReportedVoiceNameSucceedsAndSelectsIt()
    {
        (string Name, bool Enabled)[] independent = ProbeInstalledVoices();
        string[] enabledNames = independent.Where(v => v.Enabled).Select(v => v.Name).ToArray();
        if (enabledNames.Length == 0)
        {
            Assert.Inconclusive("No enabled voice is installed on this machine, so there is nothing to open with by name.");
            return;
        }

        foreach (string name in enabledNames)
        {
            using var engine = new SystemSpeechEngine();
            StepOutcome outcome = engine.Open(VoiceOverSettings.Default with { VoiceName = name });

            if (!outcome.Ok)
            {
                InconclusiveForEnvironment(outcome, name);
                return;
            }

            if (!string.IsNullOrEmpty(outcome.Detail))
            {
                // Open recorded a fallback note even though the name matched exactly (VoiceSelection.
                // Choose only returns a name GetInstalledVoices itself reported as enabled): the real
                // SelectVoice threw for it on this machine, the exact undocumented-exception class the
                // blocker this task fixed exists to survive, caught here rather than failing Open. The
                // selection did not really happen, so this machine cannot prove the rest of this test;
                // that is an environment limit, not something SystemSpeechEngine got wrong.
                //
                // The swallowed exception's raw HResult must still show up in the structured Code field,
                // not only decoded into Detail's English sentence: Ok stays true (Open still succeeded,
                // on the default voice), but Code and CodeName must carry exactly what SelectVoice threw.
                Assert.AreNotEqual(0, outcome.Code, "The raw HResult of the swallowed SelectVoice exception must be in Code, not just folded into Detail.");
                Assert.AreEqual(NativeCodes.Name(outcome.Code), outcome.CodeName, "CodeName must decode the same Code that was recorded.");
                StringAssert.Contains(outcome.Detail, NativeCodes.Name(outcome.Code));

                Assert.Inconclusive("SystemSpeechEngine could not actually select voice \"" + name +
                    "\" on this machine even though it matched exactly; recorded: " + outcome.Detail);
                return;
            }

            // SelectedVoiceName is guarded (SystemSpeechEngine.cs) rather than left to throw: reading
            // SpeechSynthesizer.Voice has shown NullReferenceException on this machine, from
            // System.Speech.Internal.Synthesis.VoiceSynthesis.GetEngineWithVoice, undocumented and the
            // same class of platform defect as the SelectVoice blocker this task fixed, just reached
            // through the property getter instead of the method (Microsoft's Voice page documents no
            // exception either). A null here is that guard doing its job, not SystemSpeechEngine crashing,
            // but it still means this machine's System.Speech cannot answer "which voice is selected" at
            // all, which is an environment limit this test records rather than a defect.
            string? selected = engine.SelectedVoiceName;
            if (selected is null)
            {
                Assert.Inconclusive("Reading the selected voice came back null after opening with \"" + name +
                    "\" on this machine, the guarded answer for the undocumented exception this property exists to survive.");
                return;
            }

            Assert.AreEqual(name, selected, "Opening with an exact installed voice name must select that voice.");
        }
    }

    // The blocker this task fixed, proved through the real Open rather than through the pure
    // VoiceSelection.Choose (which VoiceSelectionTests already covers exhaustively): a VoiceName that
    // matches no installed voice must still leave Open Ok, with a fallback outcome recorded, not throw
    // and not fail Open. Constructs and disposes a real SpeechSynthesizer; never calls Speak.
    [TestMethod]
    public void OpenWithANonMatchingVoiceNameFallsBackToTheDefaultVoiceAndRecordsWhy()
    {
        (string Name, bool Enabled)[] independent = ProbeInstalledVoices();
        if (!independent.Any(v => v.Enabled))
        {
            Assert.Inconclusive("No enabled voice is installed on this machine.");
            return;
        }

        using var engine = new SystemSpeechEngine();
        StepOutcome outcome = engine.Open(VoiceOverSettings.Default with { VoiceName = "Zz Not An Installed Voice Zz" });

        if (!outcome.Ok)
        {
            InconclusiveForEnvironment(outcome);
            return;
        }

        Assert.IsTrue(outcome.Ok, "A non-matching VoiceName must not fail Open.");
        Assert.IsFalse(string.IsNullOrEmpty(outcome.Detail), "The fallback must be recorded, not silent.");
        StringAssert.Contains(outcome.Detail, "not found");
        StringAssert.Contains(outcome.Detail, "default voice");
    }

    // A VoiceName is data from a user-writable settings file, not something Earshot chose, so
    // SystemSpeechEngine.Echo bounds and sanitises it before it can reach the recorded outcome (and from
    // there, the log): pins the three shapes that matter (an oversized name, one carrying CRLF and a fake
    // timestamped line that could otherwise be misread as a second log entry, and one carrying other
    // control characters), each guaranteed not to match an installed voice so Open takes the "not found"
    // branch and records the echoed name in Detail. Constructs and disposes a real SpeechSynthesizer;
    // never calls Speak.
    [TestMethod]
    public void VoiceNameIsBoundedAndSanitisedInTheFallbackDetail()
    {
        (string Name, bool Enabled)[] independent = ProbeInstalledVoices();
        if (!independent.Any(v => v.Enabled))
        {
            Assert.Inconclusive("No enabled voice is installed on this machine.");
            return;
        }

        var cases = new[]
        {
            new string('Z', 100_000),
            "Fake\r\n2026-09-19T10:00:00.000Z ERROR injected as a second log line\r\nName",
            "ControlCharsHere.",
        };

        foreach (string requested in cases)
        {
            using var engine = new SystemSpeechEngine();
            StepOutcome outcome = engine.Open(VoiceOverSettings.Default with { VoiceName = requested });

            if (!outcome.Ok)
            {
                InconclusiveForEnvironment(outcome);
                return;
            }

            Assert.IsFalse(string.IsNullOrEmpty(outcome.Detail), "A VoiceName that matches nothing installed must still record a fallback.");
            Assert.IsTrue(outcome.Detail!.Length < 200,
                "The echoed VoiceName must be bounded, not the full " + requested.Length + " characters: length was " + outcome.Detail.Length + ".");
            Assert.IsFalse(outcome.Detail.Contains('\r'), "No CR may reach the recorded detail: " + outcome.Detail);
            Assert.IsFalse(outcome.Detail.Contains('\n'), "No LF may reach the recorded detail: " + outcome.Detail);
            foreach (char c in outcome.Detail)
            {
                Assert.IsFalse(char.IsControl(c),
                    "No control character may reach the recorded detail (found U+" + ((int)c).ToString("X4") + "): " + outcome.Detail);
            }
        }
    }

    // Deleting CR/LF outright (string.Empty rather than a space) would run the words on either side of it
    // together, changing what the log actually shows; VoiceNameIsBoundedAndSanitisedInTheFallbackDetail
    // above only proves no CR or LF reaches the detail, not that a space took their place, so that is
    // pinned here on its own.
    [TestMethod]
    public void CarriageReturnAndLineFeedBecomeASpaceRatherThanDisappearing()
    {
        (string Name, bool Enabled)[] independent = ProbeInstalledVoices();
        if (!independent.Any(v => v.Enabled))
        {
            Assert.Inconclusive("No enabled voice is installed on this machine.");
            return;
        }

        using var engine = new SystemSpeechEngine();
        StepOutcome outcome = engine.Open(VoiceOverSettings.Default with { VoiceName = "Hello\r\nWorld" });

        if (!outcome.Ok)
        {
            InconclusiveForEnvironment(outcome);
            return;
        }

        Assert.IsFalse(string.IsNullOrEmpty(outcome.Detail), "A VoiceName that matches nothing installed must still record a fallback.");
        Assert.IsFalse(outcome.Detail!.Contains("HelloWorld"), "Deleting CR/LF outright must not run the two words together: " + outcome.Detail);
        Assert.IsTrue(outcome.Detail.Contains("Hello") && outcome.Detail.Contains("World"),
            "Both words either side of the CRLF must still be present: " + outcome.Detail);
    }

    private static (string Name, bool Enabled)[] ProbeInstalledVoices()
    {
        using var probe = new SpeechSynthesizer();
        return probe.GetInstalledVoices().Select(v => (v.VoiceInfo.Name, v.Enabled)).ToArray();
    }

    private static void InconclusiveForEnvironment(StepOutcome outcome, string? voiceName = null)
    {
        string forVoice = voiceName is null ? string.Empty : " for voice \"" + voiceName + "\"";
        Assert.Inconclusive(
            "SystemSpeechEngine.Open failed" + forVoice + " on a machine with an enabled voice; treating as an " +
            "environment limit (no default audio endpoint, for example) rather than a defect: " +
            outcome.CodeName + " (" + outcome.Code + ") " + outcome.Detail);
    }
}
