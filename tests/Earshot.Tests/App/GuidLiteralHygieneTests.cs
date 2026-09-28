using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Earshot.Tests.TestWindow;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.App;

// A structural allow-list, not a deny-list: every GUID-shaped literal under src\Earshot and
// tests\Earshot.Tests must be one of the values listed below, or the scan at the bottom of this file
// fails and names it. Two real captured values sat unnoticed in test fixtures until a reviewer happened
// to look closely at them, indistinguishable from any other hex string in a plain read of the file: the
// owner's own AirPods render endpoint id and a container id both leaked this way. Listing every value in
// use, rather than writing a pattern that tries to describe "looks synthetic", means a new value can only
// ever get in by a reviewable addition to this list, never by quietly matching a permissive shape.
//
// Known holds two kinds of value, not distinguished by the check itself: a documented Windows or
// Bluetooth SIG constant (a property key, an interface or class id, or a member of the Bluetooth base
// UUID family 0000xxxx-0000-1000-8000-00805F9B34FB), cited by URL or name at the line that declares it,
// and a fixture value invented for a test, never read from real hardware. Nothing on this list doubles as
// that citation: a Windows constant added here still needs its own doc URL at its own declaration.
[TestClass]
public sealed class GuidLiteralHygieneTests
{
    private static readonly Regex GuidLiteral = new(
        @"\b[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "00000000-0000-0000-0000-000000000000", "00000000-0000-0000-ffff-ffffffffffff", "00000000-deca-fade-deca-deafdecacafe", "0000010b-0000-0000-c000-000000000046",
        "00001000-0000-1000-8000-00805f9b34fb", "00001101-0000-1000-8000-00805f9b34fb", "00001108-0000-1000-8000-00805f9b34fb", "0000110a-0000-1000-8000-00805f9b34fb",
        "0000110b-0000-1000-8000-00805f9b34fb", "0000110c-0000-1000-8000-00805f9b34fb", "0000110e-0000-1000-8000-00805f9b34fb", "00001116-0000-1000-8000-00805f9b34fb",
        "0000111e-0000-1000-8000-00805f9b34fb", "0000111f-0000-1000-8000-00805f9b34fb", "0000112f-0000-1000-8000-00805f9b34fb", "00001132-0000-1000-8000-00805f9b34fb",
        "00001800-0000-1000-8000-00805f9b34fb", "00001801-0000-1000-8000-00805f9b34fb", "00021401-0000-0000-c000-000000000046", "000214f9-0000-0000-c000-000000000046",
        "02030302-1d19-415f-86f2-22a2106a0a77", "026e516e-b814-414b-83cd-856d6fef4822", "02820e19-7b98-4ed2-b2e8-fdccceff619b", "0642cb2f-2075-4469-918c-4441e69c548a",
        "09941815-ea89-4b5b-89e0-2a773801fac3", "0b8e5a51-7f0c-5f5e-9c6b-2d7c1e0f4a11", "0bba1ede-7566-4f47-90ec-25fc567ced2a", "0bd7a1be-7a1a-44db-8397-cc5392387b5e",
        "0d5c1e2f-3a4b-4c5d-8e6f-708192a3b4c5", "0f87369f-a4e5-4cfc-bd3e-73e6154572dd", "104ea319-6ee2-4701-bd47-8ddbf425bbe5", "11111111-0000-4000-8000-000000000002",
        "11111111-2222-4333-8444-555555555501", "11111111-2222-4333-8444-555555555502", "11111111-2222-4333-8444-555555555503", "14314595-b4bc-4055-95f2-58f2e42c9855",
        "1be09788-6894-4089-8586-9a2a6c265ac5", "1d7c22f5-7a64-4f0e-9b2c-5c6c0a47b1e2", "1da5d803-d492-4edd-8c23-e0c0ffee7f0e", "1da87e53-152b-403e-98dc-74d7b4d63d59",
        "1ff31936-572e-4b36-a2bf-b2409b1aa6f4", "28f54685-06fd-11d2-b27a-00a0c9223196", "2a07407e-6497-4a18-9787-32f79bd0d98f", "2a9c35da-d357-41f4-bbc1-207ac1b1f3cb",
        "2faba4c7-4da9-4013-9697-20cc3fd40f85", "30cbe57d-d9d0-452a-ab13-7ac5ac4825ee", "33333333-4444-4555-8666-777777777701", "352ffba8-0973-437c-a61f-f64cafd81df9",
        "416d8b73-cb41-4ea1-805c-9be9a5ac4a74", "4340a6c5-93fa-4706-972c-7b648008a5a7", "44444444-5555-4666-8777-888888888801", "44444444-5555-4666-8777-888888888802",
        "4509f757-2d46-4637-8e62-ce7db944f57b", "4715650b-5e9d-4ac2-b898-a4fc0aa5df78", "49cd1f76-5626-4b17-a4e8-18b4aa1a2213", "4c3d624d-fd6b-49a3-b9b7-09cb3cd3f047",
        "540b947e-8b40-45bc-a8a2-6a0b894cbda2", "55555555-6666-4777-8888-999999999901", "55555555-6666-4777-8888-999999999902", "5c3a9e21-4b7d-5f18-9a6c-2d8e0b4f7a13",
        "653758fb-7b9a-4f1e-a471-beeb8e9b834e", "6994ad04-93ef-11d0-a3cc-00a0c9223196", "6a0d2c11-8f5e-4b7a-a9d3-2e41c7b05f18", "6a1e3c52-0b7d-4f4b-9f7a-3c1d2e5b8a90",
        "74ec2172-0bad-4d01-8f77-997b2be0722a", "77777777-8888-4999-8000-000000000001", "77777777-8888-4999-8000-000000000002", "77777777-8888-4999-8000-000000000003",
        "77777777-8888-4999-8000-000000000004", "7991eec9-7e89-4d85-8390-6c703cec60c0", "7e2a9c40-1b3d-4f5e-8a6b-0c1d2e3f4a5b", "7e2d4c8a-1b3f-5a6e-b9d0-6c4a2f8e1d35",
        "7e2d4c8a-5a1e-4d0b-9c61-3f0e2a7b8c90", "7fa06c40-b8f6-4c7e-8556-e8c33a12e54d", "85df5081-1b24-4f32-878a-d9d14df4cb77", "86627eb4-42a7-41e4-a4d9-ac33a72f2d52",
        "886d8eeb-8cf2-4446-8d02-cdba1dbdcf99", "8c134960-51ad-11cf-878a-94f801c10000", "8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", "8cfac062-a080-4c15-9a88-aa7c2af80dfc",
        "8fd4711d-2d02-4c8c-87e3-eff699de127e", "9c2c4058-23f5-41de-877a-df3af236a09e", "9c86f320-dee3-4dd1-b972-a303f26b061e", "9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3",
        "a2901f31-dc17-41b7-b0ad-2f77a5f04490", "a35996ab-11cf-4935-8b61-a6761081ecdf", "a45c254e-df1c-4efd-8020-67d146a850e0", "a95664d2-9614-4f35-a746-de8db63617e6",
        "aaaaaaaa-bbbb-4ccc-8ddd-000000000001", "ae2de0e4-5bca-4f2d-aa46-5d13f8fdb3a9", "b32a92b5-bc25-4078-9c08-d7ee95c48e03", "b725f130-47ef-101a-a5f1-02608c9eebac",
        "bae54997-48b1-4cbe-9965-d6be263ebea4", "bcde0395-e52f-467c-8e3d-c4579291692e", "c4c07f2b-8524-4e66-ae3a-a6235f103beb", "cccccccc-dddd-4eee-8fff-000000000001",
        "d22108aa-8ac5-49a5-837b-37bbb3d7591e", "d666063f-1587-4e43-81f1-b948e807363f", "d98d51e5-c9b4-496a-a9c1-18980261cf0f", "e7c3fb29-caa7-4f47-8c8b-be59b330d4c5",
        "f5bc8fc5-536d-4f77-b852-fbc1356fdeb6", "f8b6e6c2-c316-4c33-b73f-6a328bde4457", "ff48dba4-60ef-4201-aa87-54103eef594e",
    };

    private static readonly string[] OwnedFolders =
    {
        Path.Combine("src", "Earshot"),
        Path.Combine("tests", "Earshot.Tests"),
    };

    // This file's own path, the same trick NoSpecCitationsTests uses: the list above is full of GUID
    // literals by definition, so this one file is excluded from the scan below rather than made to pass
    // by coincidence.
    private static string ThisFilePath([CallerFilePath] string path = "") => path;

    // Every GUID this line contains that is not on the list above. Used directly by the two planted-example
    // tests below, and by the full-tree scan.
    private static IEnumerable<string> UnknownGuidsIn(string line) =>
        GuidLiteral.Matches(line).Select(m => m.Value).Where(g => !Known.Contains(g)).Distinct(StringComparer.OrdinalIgnoreCase);

    // A GUID nobody has reviewed and added to the list is refused, whatever it looks like: this one is
    // built to look exactly like the fixtures already in this file, plain hex with no repeating digits, no
    // obvious pattern to catch by eye - which is exactly how the two real captured values that prompted
    // this list read before anyone noticed either.
    [TestMethod]
    public void APlantedRandomLookingGuidIsFlagged()
    {
        string planted = "private static readonly Guid Suspect = new(\"3f9a7c21-88de-4a56-9b12-0e5d7a44c8b1\");";

        Assert.IsTrue(UnknownGuidsIn(planted).Any(), "A GUID not on the known list must be flagged.");
    }

    [TestMethod]
    public void AGuidAlreadyOnTheListIsNotFlagged()
    {
        string line = "internal static readonly Guid AudioSinkServiceClass = new(\"0000110B-0000-1000-8000-00805F9B34FB\");";

        Assert.IsFalse(UnknownGuidsIn(line).Any(), "A GUID already reviewed and listed must not be flagged.");
    }

    [TestMethod]
    public void EveryGuidLiteralInTheTreeIsOnTheKnownList()
    {
        string root = RepositoryLocator.RepositoryRoot();
        string thisFile = Path.GetFullPath(ThisFilePath());
        var found = new List<string>();
        int filesRead = 0;

        foreach (string folder in OwnedFolders)
        {
            string full = Path.Combine(root, folder);
            Assert.IsTrue(Directory.Exists(full), "Owned folder not found: " + full);

            foreach (string file in Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file) || string.Equals(Path.GetFullPath(file), thisFile, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                filesRead++;

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (string unknown in UnknownGuidsIn(lines[i]))
                    {
                        found.Add(Path.GetRelativePath(root, file) + ":" + (i + 1) + ": " + unknown);
                    }
                }
            }
        }

        Assert.IsTrue(filesRead >= 300, "Only " + filesRead + " files were read across " + string.Join(", ", OwnedFolders) + ", so a clean result would prove nothing.");
        Assert.AreEqual(0, found.Count, "A GUID literal is not on the known list (a documented Windows or Bluetooth constant, or a listed fixture value):" +
            Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    private static bool IsBuildOutput(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return path.Contains(separator + "bin" + separator, StringComparison.OrdinalIgnoreCase) ||
            path.Contains(separator + "obj" + separator, StringComparison.OrdinalIgnoreCase);
    }
}
