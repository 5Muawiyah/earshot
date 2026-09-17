using System.Text;
using System.Text.RegularExpressions;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Contracts;

// The gate treats $(Arg0), $(Arg1) and $(Arg2) as attacker-controlled. These tests pin the verb
// whitelist and the nonce and address grammars, including injection-shaped strings and the
// literal placeholder Task Scheduler may pass for an unsupplied argument.
[TestClass]
public sealed class GateVerbsTests
{
    private static readonly string[] Expected =
        ["block", "allow", "status", "setboot-on", "setboot-off", "protect-on", "protect-off", "set-device", "boot"];

    [TestMethod]
    public void AllHoldsExactlyTheNineVerbs()
    {
        Assert.HasCount(Expected.Length, GateVerbs.All);
        foreach (string verb in Expected)
        {
            Assert.IsTrue(GateVerbs.All.Contains(verb), verb);
        }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("BLOCK")]
    [DataRow("Block")]
    [DataRow("block ")]
    [DataRow(" block")]
    [DataRow("block\n")]
    [DataRow("block\0")]
    [DataRow("block;calc")]
    [DataRow("block & whoami")]
    [DataRow("block|calc")]
    [DataRow("\"block\"")]
    [DataRow("'block'")]
    [DataRow("../block")]
    [DataRow("block/..")]
    [DataRow("install")]
    [DataRow("uninstall")]
    [DataRow("probe")]
    [DataRow("diag")]
    [DataRow("gate")]
    [DataRow("$(Arg0)")]
    [DataRow("$(Arg2)")]
    [DataRow("setboot")]
    [DataRow("set-device=0A1B2C3D4E8C")]
    [DataRow("prote\u0441t-on")]
    [DataRow("\uFF42lock")]
    public void RejectsAnythingElse(string candidate)
    {
        Assert.IsFalse(GateVerbs.All.Contains(candidate));
    }

    [TestMethod]
    public void FuzzOnlyTheWhitelistPasses()
    {
        var random = new Random(20260915);
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_ ;&|$()'\"\\/.\r\n\t\0\u2019\u0430";
        for (int i = 0; i < 20000; i++)
        {
            string candidate = i % 3 == 0
                ? Mutate(Expected[random.Next(Expected.Length)], random, alphabet)
                : RandomString(random, alphabet, random.Next(0, 16));
            bool expected = Array.IndexOf(Expected, candidate) >= 0;
            Assert.AreEqual(expected, GateVerbs.All.Contains(candidate), "Candidate: " + Escape(candidate));
        }
    }

    internal static string RandomString(Random random, string alphabet, int length)
    {
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append(alphabet[random.Next(alphabet.Length)]);
        }

        return sb.ToString();
    }

    // One random edit: replace, insert or delete a character.
    internal static string Mutate(string value, Random random, string alphabet)
    {
        var sb = new StringBuilder(value);
        int op = random.Next(3);
        char c = alphabet[random.Next(alphabet.Length)];
        if (op == 0 && sb.Length > 0)
        {
            sb[random.Next(sb.Length)] = c;
        }
        else if (op == 1)
        {
            sb.Insert(random.Next(sb.Length + 1), c);
        }
        else if (sb.Length > 0)
        {
            sb.Remove(random.Next(sb.Length), 1);
        }

        return sb.ToString();
    }

    internal static string Escape(string value)
    {
        var sb = new StringBuilder();
        foreach (char c in value)
        {
            sb.Append(c is >= ' ' and <= '~' ? c.ToString() : "\\u" + ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}

[TestClass]
public sealed class BoundaryValidationTests
{
    private const string ValidNonce = "0123456789abcdef0123456789abcdef";
    private const string ValidAddress = "0A1B2C3D4E8C";

    [TestMethod]
    [DataRow(ValidNonce)]
    [DataRow("00000000000000000000000000000000")]
    [DataRow("ffffffffffffffffffffffffffffffff")]
    [DataRow("9f86d081884c7d659a2feaa0c55ad015")]
    public void IsNonceAcceptsLowerCaseHex32(string nonce)
    {
        Assert.IsTrue(BoundaryValidation.IsNonce(nonce));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("0123456789ABCDEF0123456789ABCDEF")]
    [DataRow("0123456789abcdef0123456789abcde")]
    [DataRow("0123456789abcdef0123456789abcdef0")]
    [DataRow("0123456789abcdef0123456789abcdeg")]
    [DataRow(" 0123456789abcdef0123456789abcdef")]
    [DataRow("0123456789abcdef0123456789abcde\n")]
    [DataRow("0123456789abcdef0123456789abcde\0")]
    [DataRow("0123456789abcdef-0123456789abcde")]
    [DataRow("{0123456789abcdef0123456789abcd}")]
    [DataRow("$(Arg1)")]
    [DataRow("0123456789abcdef0123456789abc;&x")]
    [DataRow("\u0660123456789abcdef0123456789abcdef")]
    [DataRow("\uFF10123456789abcdef0123456789abcde")]
    public void IsNonceRejectsEverythingElse(string? candidate)
    {
        Assert.IsFalse(BoundaryValidation.IsNonce(candidate));
    }

    [TestMethod]
    [DataRow(ValidAddress)]
    [DataRow("1A2B3C4D5E6F")]
    [DataRow("000000000000")]
    [DataRow("FFFFFFFFFFFF")]
    public void IsAddress12AcceptsUpperCaseHex12(string address)
    {
        Assert.IsTrue(BoundaryValidation.IsAddress12(address));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("0a1b2c3d4e8c")]
    [DataRow("0A1B2C3D4E8")]
    [DataRow("0A1B2C3D4E8C0")]
    [DataRow("0A1B2C3D4E8G")]
    [DataRow("0A:1B:2C:3D:4E:8C")]
    [DataRow("5A-6B-7C-8D-9E-AF")]
    [DataRow(" 0A1B2C3D4E8C")]
    [DataRow("0A1B2C3D4E8C ")]
    [DataRow("0A1B2C3D4E\n8C")]
    [DataRow("0A1B2C3D4E8C;")]
    [DataRow("$(Arg2)")]
    [DataRow("0x0A1B2C3D4E")]
    [DataRow("..\\..\\Windo")]
    [DataRow("\u0663\u0660\u0660B2C3D4E8C")]
    public void IsAddress12RejectsEverythingElse(string? candidate)
    {
        Assert.IsFalse(BoundaryValidation.IsAddress12(candidate));
    }

    [TestMethod]
    [DataRow(null, "")]
    [DataRow("", "")]
    [DataRow("$(Arg2)", "")]
    [DataRow(ValidAddress, ValidAddress)]
    [DataRow("$(ARG2)", "$(ARG2)")]
    [DataRow("$(Arg1)", "$(Arg1)")]
    [DataRow(" $(Arg2)", " $(Arg2)")]
    public void NormaliseTreatsOnlyTheExactPlaceholderAsEmpty(string? input, string expected)
    {
        Assert.AreEqual(expected, BoundaryValidation.Normalise(input));
    }

    [TestMethod]
    public void AnUnsuppliedPlaceholderIsNeverAValidArgument()
    {
        Assert.IsFalse(BoundaryValidation.IsNonce(BoundaryValidation.Normalise("$(Arg1)")));
        Assert.IsFalse(BoundaryValidation.IsAddress12(BoundaryValidation.Normalise("$(Arg2)")));
    }

    [TestMethod]
    public void FuzzNonceAgreesWithTheDocumentedPattern()
    {
        var oracle = new Regex(@"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant);
        var random = new Random(15092026);
        const string alphabet = "0123456789abcdefABCDEFgxyz -{}$()\n\0\u0660\uFF10";
        for (int i = 0; i < 20000; i++)
        {
            string candidate = i % 2 == 0
                ? GateVerbsTests.Mutate(RandomHex(random, "0123456789abcdef", 32), random, alphabet)
                : GateVerbsTests.RandomString(random, alphabet, random.Next(28, 36));
            Assert.AreEqual(oracle.IsMatch(candidate), BoundaryValidation.IsNonce(candidate), "Candidate: " + GateVerbsTests.Escape(candidate));
        }
    }

    [TestMethod]
    public void FuzzAddressAgreesWithTheDocumentedPattern()
    {
        var oracle = new Regex(@"\A[0-9A-F]{12}\z", RegexOptions.CultureInvariant);
        var random = new Random(26091505);
        const string alphabet = "0123456789ABCDEFabcdefGXYZ :-;$()\n\0\u0663\uFF21";
        for (int i = 0; i < 20000; i++)
        {
            string candidate = i % 2 == 0
                ? GateVerbsTests.Mutate(RandomHex(random, "0123456789ABCDEF", 12), random, alphabet)
                : GateVerbsTests.RandomString(random, alphabet, random.Next(10, 15));
            Assert.AreEqual(oracle.IsMatch(candidate), BoundaryValidation.IsAddress12(candidate), "Candidate: " + GateVerbsTests.Escape(candidate));
        }
    }

    private static string RandomHex(Random random, string digits, int length) =>
        GateVerbsTests.RandomString(random, digits, length);
}
