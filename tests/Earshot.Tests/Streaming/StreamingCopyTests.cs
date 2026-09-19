using System.Reflection;
using Earshot.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Streaming;

// Acceptance test 6, the house style held mechanically so it cannot rot. Sentences and labels are checked by
// different rules on purpose: a sentence ends with a full stop, and the words on something the owner clicks do not.
[TestClass]
public sealed class StreamingCopyTests
{
    private static readonly string[] AmericanSpellings = ["color", "customize", "analyze", "favorite"];

    private static Dictionary<string, string> StringsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetValue(null)!);

    [TestMethod]
    public void CopyIsBritishAndPlain()
    {
        Dictionary<string, string> sentences = StringsOf(typeof(StreamingCopy));
        Dictionary<string, string> labels = StringsOf(typeof(StreamingLabels));

        Assert.IsTrue(sentences.Count >= 12, "StreamingCopy holds " + sentences.Count + " sentences; neither half may pass by being empty.");
        Assert.AreEqual(3, labels.Count, "Play from a phone, Refresh the list and Stop playing from a device, and nothing else.");

        foreach ((string name, string text) in sentences.Concat(labels))
        {
            Assert.IsFalse(text.Contains((char)0x2014), name + " has an em-dash.");
            Assert.IsFalse(text.Contains((char)0x2013), name + " has an en-dash.");
            foreach (string spelling in AmericanSpellings)
            {
                Assert.IsFalse(text.Contains(spelling, StringComparison.OrdinalIgnoreCase), name + " spells \"" + spelling + "\" the American way.");
            }

            // No figures in anything the owner reads: a code from Windows goes to the log, and no limit, count or
            // version number is ever put on a card. The one digit allowed is the placeholder in a format string.
            string withoutPlaceholder = text.Replace("{0}", "", StringComparison.Ordinal);
            Assert.IsFalse(withoutPlaceholder.Any(char.IsDigit), name + " carries a figure: " + text);
            Assert.IsTrue(text.Length <= 90, name + " is " + text.Length + " characters long.");
        }

        foreach ((string name, string text) in sentences)
        {
            Assert.IsTrue(text.EndsWith('.'), name + " is a sentence and must end with a full stop: " + text);
        }

        foreach ((string name, string text) in labels)
        {
            Assert.IsFalse(text.EndsWith('.'), name + " is a label the owner clicks and must not end with a full stop: " + text);
        }
    }

    [TestMethod]
    public void TheLabelsAreExactlyThese()
    {
        // Read through reflection, as the test above reads them: a constant compared with a literal is folded by the
        // compiler, and then proves nothing about the assembly that ships.
        Dictionary<string, string> labels = StringsOf(typeof(StreamingLabels));
        Assert.AreEqual("Play from a phone", labels["Parent"]);
        Assert.AreEqual("Refresh the list", labels["Refresh"]);
        Assert.AreEqual("Stop playing from {0}", labels["StopFormat"]);
        Assert.AreEqual("Stop playing from Test Phone", StreamingLabels.Stop("Test Phone"));
    }

    // What no Microsoft page says is never claimed: nothing about which speaker the audio comes out of or that it
    // reaches the AirPods, nothing about what the adapter supports, nothing about making the phone discoverable,
    // nothing that asks for administrator rights, and no offer to pair: pairing is done in Windows Settings.
    [TestMethod]
    public void TheCopyClaimsNothingTheDocumentationDoesNotSay()
    {
        string[] forbidden = ["AirPods", "earbud", "headphone", "speaker", "adapter", "A2DP", "discoverable", "administrator", "pairing mode", "volume"];
        foreach ((string name, string text) in StringsOf(typeof(StreamingCopy)).Concat(StringsOf(typeof(StreamingLabels))))
        {
            foreach (string word in forbidden)
            {
                Assert.IsFalse(text.Contains(word, StringComparison.OrdinalIgnoreCase), name + " says \"" + word + "\": " + text);
            }
        }

        Assert.AreEqual("Pair the phone in Windows Settings first.", StringsOf(typeof(StreamingCopy))["PairInSettings"]);
    }

    [TestMethod]
    public void EveryFormatTakesExactlyTheDeviceName()
    {
        foreach ((string name, string text) in StringsOf(typeof(StreamingCopy)).Concat(StringsOf(typeof(StreamingLabels))))
        {
            bool isFormat = name.EndsWith("Format", StringComparison.Ordinal);
            Assert.AreEqual(isFormat, text.Contains("{0}", StringComparison.Ordinal), name);
            Assert.IsFalse(text.Contains("{1}", StringComparison.Ordinal), name);
        }
    }
}
