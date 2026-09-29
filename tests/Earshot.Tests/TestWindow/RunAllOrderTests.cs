using Earshot.TestWindow.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.TestWindow;

// A guided sequence with 10 as five items, 17 beside 09 and the other hand-back tests straight
// after it, 16 and 21 after 15 and 18 last; 00 is not in it.
[TestClass]
public sealed class RunAllOrderTests
{
    [TestMethod]
    public void HasTwentyFiveItemsTwelveSinglesFiveVariantsEightMoreSingles()
    {
        Assert.AreEqual(25, RunAllOrder.Items.Count);
    }

    [TestMethod]
    public void RestoreZeroZeroIsNeverInTheOrder()
    {
        Assert.IsFalse(RunAllOrder.Items.Any(item => item.RowNumber == "00"));
    }

    [TestMethod]
    public void TheOrderIsExactlyTheOwnersOrderWithTenExpandedIntoFiveVariants()
    {
        string[] expectedKeys =
        {
            "01", "02", "03", "04", "05", "06", "07", "08", "09", "17", "20", "22",
            "10v1", "10v2", "10v3", "10v4", "10v5",
            "11", "12", "13", "14", "15", "16", "21", "18",
        };

        CollectionAssert.AreEqual(expectedKeys, RunAllOrder.Items.Select(item => item.Key).ToArray());
    }

    [TestMethod]
    public void EveryTenItemCarriesItsOwnVariantNumberAndEveryOtherItemCarriesNone()
    {
        foreach (RunAllItem item in RunAllOrder.Items)
        {
            if (item.RowNumber == "10")
            {
                Assert.IsNotNull(item.Variant);
            }
            else
            {
                Assert.IsNull(item.Variant);
            }
        }
    }
}
