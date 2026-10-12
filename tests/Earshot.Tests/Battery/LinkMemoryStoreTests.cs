using System.Text.Json;
using Earshot.Widget;
using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Battery;

// The file the link to the owner's pair is remembered in: it comes back as it was saved, a file that is missing, corrupt
// or of another schema reads as none (and says why, with the raw reason), and what is written holds exactly the schema's
// fields, so nothing that names a sender can be in it.
[TestClass]
public sealed class LinkMemoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset LinkedAt = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] SchemaFields = ["Colour", "LastHeardAt", "LinkedAt", "Model", "SchemaVersion"];

    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string FilePath => _temp.File(Path.Combine("widget", "link.json"));

    private LinkMemoryStore NewStore() => new(FilePath, _log);

    private static LinkMemory Sample() => new(0xEEEE, 0xEE, LinkedAt, LinkedAt + TimeSpan.FromMinutes(7));

    private void WriteRaw(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, text);
    }

    [TestMethod]
    public void AMemoryComesBackAsItWasSaved()
    {
        NewStore().Save(Sample());

        LinkMemory? loaded = NewStore().Load();

        Assert.AreEqual(Sample(), loaded);
        Assert.IsEmpty(_log.Entries);
    }

    // Writes are taken in an order under the service's lock and may reach the file in another. A forgotten link (nothing
    // saved, sequence 3) must not be brought back by a write of the link taken before it (sequence 2) that lands after it.
    [TestMethod]
    public void ALateOlderWriteDoesNotBringAForgottenLinkBack()
    {
        LinkMemoryStore store = NewStore();
        store.Save(Sample(), 1);
        store.Save(null, 3);
        Assert.IsFalse(File.Exists(FilePath), "Forgotten.");

        store.Save(Sample(), 2);

        Assert.IsFalse(File.Exists(FilePath), "A write taken before the forgetting is dropped.");
        Assert.IsNull(NewStore().Load());
    }

    [TestMethod]
    public void ALateOlderWriteDoesNotReplaceANewerLinkAndAnUnorderedWriteStillLands()
    {
        LinkMemoryStore store = NewStore();
        LinkMemory newer = Sample() with { LastHeardAt = LinkedAt + TimeSpan.FromMinutes(20) };
        store.Save(newer, 5);

        store.Save(Sample(), 4);
        Assert.AreEqual(newer, NewStore().Load(), "The older write is dropped.");

        store.Save(Sample(), 6);
        Assert.AreEqual(Sample(), NewStore().Load(), "A later write lands.");

        store.Save(newer);
        Assert.AreEqual(newer, NewStore().Load(), "A write with no sequence is not ordered and lands.");
    }

    [TestMethod]
    public void AMissingFileAndAMissingFolderAreNoMemoryWithNothingLogged()
    {
        Assert.IsNull(NewStore().Load(), "No folder at all.");

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        Assert.IsNull(NewStore().Load(), "A folder and no file.");
        Assert.IsEmpty(_log.Entries);
    }

    [TestMethod]
    public void ACorruptFileIsNoMemoryAndTheRawReasonIsLogged()
    {
        WriteRaw("this is {not json");

        Assert.IsNull(NewStore().Load());

        LogEntry entry = _log.Entries.Single(e => e.Level == LogLevel.Warn);
        StringAssert.Contains(entry.Message, "link.json");
        Assert.IsNotNull(entry.Exception, "The parser's own reason travels with the line.");
    }

    [TestMethod]
    public void AFileOfAnotherSchemaOrWithOutOfRangeValuesIsNoMemoryAndIsSaid()
    {
        WriteRaw("""{ "SchemaVersion": 2, "Model": 61166, "Colour": 238, "LinkedAt": "2026-10-12T09:00:00+00:00", "LastHeardAt": "2026-10-12T09:07:00+00:00" }""");
        Assert.IsNull(NewStore().Load(), "A newer schema.");
        Assert.HasCount(1, _log.Entries.Where(e => e.Level == LogLevel.Warn));
        StringAssert.Contains(_log.Entries.Single(e => e.Level == LogLevel.Warn).Message, "schema 2");

        WriteRaw("""{ "SchemaVersion": 1, "Model": 0, "Colour": 238, "LinkedAt": "2026-10-12T09:00:00+00:00", "LastHeardAt": "2026-10-12T09:07:00+00:00" }""");
        Assert.IsNull(NewStore().Load(), "A model of 0 is no model.");

        WriteRaw("""{ "SchemaVersion": 1, "Model": 61166, "Colour": 999, "LinkedAt": "2026-10-12T09:00:00+00:00", "LastHeardAt": "2026-10-12T09:07:00+00:00" }""");
        Assert.IsNull(NewStore().Load(), "A colour that does not fit a byte.");

        WriteRaw("""{ "SchemaVersion": 1, "Model": 61166, "Colour": 238, "LinkedAt": "2026-10-12T09:07:00+00:00", "LastHeardAt": "2026-10-12T09:00:00+00:00" }""");
        Assert.IsNull(NewStore().Load(), "Heard before it was linked.");
    }

    [TestMethod]
    public void TheWrittenJsonHoldsExactlyTheSchemasFields()
    {
        NewStore().Save(Sample());

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FilePath));
        string[] names = document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

        CollectionAssert.AreEqual(SchemaFields, names);
        Assert.AreEqual(1, document.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    [TestMethod]
    public void SavingNothingRemovesTheFileAndSavingAgainWritesItBack()
    {
        NewStore().Save(Sample());
        Assert.IsTrue(File.Exists(FilePath));

        NewStore().Save(null);
        Assert.IsFalse(File.Exists(FilePath), "A forgotten link is not remembered after a restart.");
        Assert.IsNull(NewStore().Load());

        NewStore().Save(null);
        NewStore().Save(Sample());
        Assert.AreEqual(Sample(), NewStore().Load());
        Assert.IsFalse(File.Exists(FilePath + ".tmp"), "The temporary file does not stay.");
    }

    [TestMethod]
    public void AFailedWriteIsLoggedWithItsCodeAndDoesNotThrow()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        Directory.CreateDirectory(FilePath); // a folder where the file belongs: the move over it fails

        NewStore().Save(Sample());

        LogEntry entry = _log.Entries.Single(e => e.Level == LogLevel.Warn);
        StringAssert.Contains(entry.Message, "0x");
    }
}
