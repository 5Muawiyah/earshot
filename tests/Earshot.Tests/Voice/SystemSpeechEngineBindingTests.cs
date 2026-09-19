using System.Speech.Synthesis;
using Earshot.Contracts;
using Earshot.Voice;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Voice;

// The one test in this suite allowed to construct a real System.Speech.Synthesis.SpeechSynthesizer (the
// binding proof the task brief asks for): it never calls Speak, so it never produces sound, and it fails
// if SystemSpeechEngine is not actually talking to System.Speech. It asks the real SpeechSynthesizer,
// independently, whether this machine has an enabled voice, then asserts SystemSpeechEngine.Open reports
// exactly the same availability: a fabricated Open that always returns Ok (or always returns
// NotAvailable) would fail this on a machine where the independent probe disagrees.
[TestClass]
public sealed class SystemSpeechEngineBindingTests
{
    [TestMethod]
    public void OpenAgreesWithTheRealSynthesizerAboutInstalledVoices()
    {
        bool machineHasAnEnabledVoice;
        using (var probe = new SpeechSynthesizer())
        {
            machineHasAnEnabledVoice = probe.GetInstalledVoices().Any(v => v.Enabled);
        }

        using var engine = new SystemSpeechEngine();
        StepOutcome outcome = engine.Open(VoiceOverSettings.Default);

        Assert.AreEqual(machineHasAnEnabledVoice, outcome.Ok,
            "SystemSpeechEngine.Open must agree with a real SpeechSynthesizer.GetInstalledVoices() on this machine.");
        if (!machineHasAnEnabledVoice)
        {
            Assert.AreEqual(NativeCodes.NotAvailable, outcome.Code);
        }
    }
}
