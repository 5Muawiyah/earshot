using Earshot.Contracts;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The file the owner's last readings are kept in: what comes back from a good file, from none, from one that is corrupt,
// cut short, locked, from a newer Earshot or partly out of range, and that a file that could not be read is never written
// over by the next save.
[TestClass]
public sealed class LastReadingStoreTests : IDisposable
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private const ushort Model = 0x2027;

    private readonly TempFolder _temp = new();
    private readonly CapturingLog _log = new();

    public void Dispose() => _temp.Dispose();

    private string FilePath => _temp.File(Path.Combine("widget", "last-reading.json"));

    private LastReadingStore NewStore() => new(FilePath, _log);

    private static LastReadingBook SampleBook() =>
        new(
            new SavedReading(70, false, ReadAt, Model),
            new SavedReading(60, true, ReadAt + TimeSpan.FromSeconds(5), Model),
            new SavedReading(80, true, ReadAt + TimeSpan.FromSeconds(9), Model),
            [
                new LearnedRate(Model, ChargePart.Bud, 45.5, ReadAt - TimeSpan.FromHours(1), TimeSpan.FromMinutes(20)),
                new LearnedRate(Model, ChargePart.Case, 30, ReadAt - TimeSpan.FromHours(2), TimeSpan.FromMinutes(40)),
            ])
        {
            Marks = [new EstimateMark(ChargeComponent.Case, ReadAt + TimeSpan.FromSeconds(9), 80, 93)],
        };

    private void WriteRaw(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, text);
    }

    private const string GoodJson = """
        { "SchemaVersion": 1,
          "Left": { "Percent": 70, "Charging": false, "ReadAt": "2026-10-02T09:00:00+00:00", "Model": 8231 },
          "Right": { "Percent": 900, "Charging": false, "ReadAt": "2026-10-02T09:00:00+00:00", "Model": 8231 },
          "Case": { "Percent": 50, "Charging": true, "ReadAt": "2026-10-02T09:00:00+00:00", "Model": 0 },
          "Rates": [
            { "Model": 8231, "Part": "bud", "PercentPerHour": 40, "MeasuredAt": "2026-10-02T08:00:00+00:00", "SpanMinutes": 20 },
            { "Model": 8231, "Part": "case", "PercentPerHour": -3, "MeasuredAt": "2026-10-02T08:00:00+00:00", "SpanMinutes": 20 } ] }
        """;

    [TestMethod]
    public void ABookComesBackAsItWasSaved()
    {
        LastReadingBook book = SampleBook();
        NewStore().Save(book);

        LastReadingBook loaded = NewStore().Load();

        Assert.AreEqual(book.Left, loaded.Left);
        Assert.AreEqual(book.Right, loaded.Right);
        Assert.AreEqual(book.Case, loaded.Case);
        Assert.HasCount(2, loaded.Rates);
        Assert.AreEqual(book.Rates[0], loaded.Rates[0]);
        Assert.AreEqual(book.Rates[1], loaded.Rates[1]);
        Assert.HasCount(1, loaded.Marks);
        Assert.AreEqual(book.Marks[0], loaded.Marks[0]);
    }

    [TestMethod]
    public void AMissingFileAndAMissingFolderAreTheEmptyBookWithNothingLogged()
    {
        Assert.AreSame(LastReadingBook.Empty, NewStore().Load(), "No folder at all.");

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        Assert.AreSame(LastReadingBook.Empty, NewStore().Load(), "A folder and no file.");
        Assert.IsEmpty(_log.Entries);
    }

    [TestMethod]
    public void ACorruptFileIsTheEmptyBookAndIsSaidOnceInTheLog()
    {
        WriteRaw("this is {not json");

        Assert.AreSame(LastReadingBook.Empty, NewStore().Load());
        Assert.HasCount(1, _log.Entries.Where(e => e.Level == LogLevel.Warn));
    }

    [TestMethod]
    public void ATruncatedFileIsTheEmptyBook()
    {
        NewStore().Save(SampleBook());
        string whole = File.ReadAllText(FilePath);
        File.WriteAllText(FilePath, whole[..(whole.Length / 2)]);

        LastReadingBook loaded = NewStore().Load();

        Assert.IsNull(loaded.Left);
        Assert.IsNull(loaded.Case);
        Assert.IsEmpty(loaded.Rates);
        Assert.IsTrue(_log.Has(LogLevel.Warn, "not usable"));
    }

    [TestMethod]
    public void ALockedFileIsTheEmptyBookWithItsCodeLoggedAndIsLeftAsItIs()
    {
        NewStore().Save(SampleBook());
        byte[] before = File.ReadAllBytes(FilePath);

        using (var hold = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            LastReadingStore store = NewStore();
            Assert.AreSame(LastReadingBook.Empty, store.Load(), "A sharing violation is not a missing file.");
            Assert.IsTrue(_log.Has(LogLevel.Warn, "could not be read (0x"));

            store.Save(SampleBook() with { Left = new SavedReading(10, false, ReadAt, Model) });
        }

        CollectionAssert.AreEqual(before, File.ReadAllBytes(FilePath), "A file that could not be read is not replaced by a save.");
    }

    [TestMethod]
    public void AFileOfANewerSchemaIsNotReadAndNothingIsWrittenOverIt()
    {
        const string Newer = "{ \"SchemaVersion\": 2, \"Left\": { \"Percent\": 70, \"Charging\": false, \"ReadAt\": \"2026-10-02T09:00:00+00:00\", \"Model\": 8231 }, \"Future\": 1 }";
        WriteRaw(Newer);
        LastReadingStore store = NewStore();

        Assert.AreSame(LastReadingBook.Empty, store.Load());
        store.Save(SampleBook());

        Assert.AreEqual(Newer, File.ReadAllText(FilePath));
        Assert.IsFalse(File.Exists(store.TempPath));
    }

    [TestMethod]
    public void AnEntryOutOfRangeIsDroppedOnItsOwn()
    {
        WriteRaw(GoodJson);

        LastReadingBook loaded = NewStore().Load();

        Assert.AreEqual(70, loaded.Left?.Percent, "The good entry stays.");
        Assert.IsNull(loaded.Right, "A percent of 900 is dropped.");
        Assert.IsNull(loaded.Case, "A model of 0 is dropped.");
        Assert.HasCount(1, loaded.Rates, "A negative rate is dropped.");
        Assert.AreEqual(ChargePart.Bud, loaded.Rates[0].Part);
    }

    [TestMethod]
    public void ALeftOverTempFileIsIgnoredAndReplacedByTheNextSave()
    {
        LastReadingStore store = NewStore();
        store.Save(SampleBook());
        File.WriteAllText(store.TempPath, "half a write");

        Assert.AreEqual(70, NewStore().Load().Left?.Percent, "The whole file is read, never the temp one.");

        store.Save(SampleBook() with { Left = new SavedReading(55, false, ReadAt, Model) });

        Assert.AreEqual(55, NewStore().Load().Left?.Percent);
        Assert.IsFalse(File.Exists(store.TempPath), "The temp file is moved over the file, not left behind.");
    }

    // A file that could not be read may hold readings that cannot be had again, so the next save leaves it be; deleting it
    // (a missing file is a fresh start) lets the save through, and so does it becoming readable.
    [TestMethod]
    public void AFileThatCouldNotBeReadIsNotReplacedUntilItIsReadableOrGone()
    {
        const string Corrupt = "this is {not json";
        WriteRaw(Corrupt);
        LastReadingStore store = NewStore();
        Assert.AreSame(LastReadingBook.Empty, store.Load());

        store.Save(SampleBook());
        Assert.AreEqual(Corrupt, File.ReadAllText(FilePath), "Not replaced by the next save.");
        store.Save(SampleBook());
        Assert.AreEqual(Corrupt, File.ReadAllText(FilePath), "Nor the one after.");

        File.Delete(FilePath);
        store.Save(SampleBook());
        Assert.AreEqual(70, NewStore().Load().Left?.Percent, "A missing file is a fresh start: the save goes through.");

        // Another file that could not be read, this time mended by hand into a good one.
        File.WriteAllText(FilePath, "{");
        var again = NewStore();
        Assert.AreSame(LastReadingBook.Empty, again.Load());
        NewStore().Save(SampleBook());
        File.WriteAllText(FilePath, GoodJson);
        again.Save(SampleBook() with { Left = new SavedReading(33, false, ReadAt, Model) });
        Assert.AreEqual(33, NewStore().Load().Left?.Percent, "Readable now: a successful read lets the save through.");
    }

    [TestMethod]
    public void SpentPartsComeBackAndASavedBookKeepsThem()
    {
        LastReadingStore store = NewStore();
        store.Save(SampleBook());
        store.SaveSpent([new SpentMark(ChargeComponent.Case, ReadAt, 80), new SpentMark(ChargeComponent.Left, ReadAt, 100)]);

        store.Save(SampleBook() with { Sequence = 1 });

        LastReadingStore reopened = NewStore();
        IReadOnlyList<SpentMark> spent = reopened.LoadSpent();
        Assert.HasCount(2, spent);
        Assert.AreEqual(new SpentMark(ChargeComponent.Case, ReadAt, 80), spent[0]);
        Assert.AreEqual(70, reopened.Load().Left?.Percent, "The readings were not changed by the marks.");
    }

    [TestMethod]
    public void SpentMarksOutOfRangeOrOfAnUnknownPartAreLeftOut()
    {
        WriteRaw("""
            { "SchemaVersion": 1,
              "Spent": [
                { "Part": "case", "ReadAt": "2026-10-02T09:00:00+00:00", "ReadPercent": 80 },
                { "Part": "case", "ReadAt": "2026-10-02T09:00:00+00:00", "ReadPercent": 60 },
                { "Part": "left", "ReadAt": "2026-10-02T09:00:00+00:00", "ReadPercent": 101 },
                { "Part": "boot", "ReadAt": "2026-10-02T09:00:00+00:00", "ReadPercent": 50 } ] }
            """);

        IReadOnlyList<SpentMark> spent = NewStore().LoadSpent();

        Assert.HasCount(1, spent);
        Assert.AreEqual(80, spent[0].ReadPercent);
    }

    [TestMethod]
    public void SpentMarksAreNotWrittenOverAFileThatCannotBeRead()
    {
        NewStore().Save(SampleBook());
        byte[] before = File.ReadAllBytes(FilePath);

        using (var hold = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            LastReadingStore store = NewStore();
            Assert.IsEmpty(store.LoadSpent());
            store.SaveSpent([new SpentMark(ChargeComponent.Case, ReadAt, 80)]);
            Assert.HasCount(1, store.LoadSpent(), "The run goes on by what it holds.");
        }

        CollectionAssert.AreEqual(before, File.ReadAllBytes(FilePath));
    }

    [TestMethod]
    public void SpentMarksAreNotWrittenOverAFileOfANewerSchema()
    {
        const string Newer = "{ \"SchemaVersion\": 2, \"Future\": 1 }";
        WriteRaw(Newer);

        NewStore().SaveSpent([new SpentMark(ChargeComponent.Case, ReadAt, 80)]);

        Assert.AreEqual(Newer, File.ReadAllText(FilePath));
    }

    // Two threads take books to write in order and may reach the file out of it: the older, arriving last, is dropped.
    [TestMethod]
    public void ABookTakenEarlierAndWrittenLaterDoesNotReplaceANewerOne()
    {
        LastReadingStore store = NewStore();
        LastReadingBook older = SampleBook() with { Left = new SavedReading(40, false, ReadAt, Model), Sequence = 1 };
        LastReadingBook newer = SampleBook() with { Left = new SavedReading(50, false, ReadAt, Model), Sequence = 2 };

        store.Save(newer);
        store.Save(older);

        Assert.AreEqual(50, NewStore().Load().Left?.Percent, "The older book was dropped.");

        store.Save(SampleBook() with { Left = new SavedReading(60, false, ReadAt, Model), Sequence = 3 });
        Assert.AreEqual(60, NewStore().Load().Left?.Percent, "A later one is written.");
    }
}
