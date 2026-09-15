using Earshot.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Contracts;

[TestClass]
public sealed class NativeCodesTests
{
    [TestMethod]
    [DataRow(0x00000000u, "S_OK")]
    [DataRow(0x80070490u, "E_NOTFOUND")]
    [DataRow(0x80004002u, "E_NOINTERFACE")]
    [DataRow(0x88890004u, "AUDCLNT_E_DEVICE_INVALIDATED")]
    [DataRow(0x80070002u, "ERROR_FILE_NOT_FOUND")]
    [DataRow(0x80070003u, "ERROR_PATH_NOT_FOUND")]
    [DataRow(0x80070057u, "E_INVALIDARG")]
    [DataRow(0xE0000225u, "ERROR_NO_SUCH_DEVICE_INTERFACE")]
    [DataRow(0xE000020Bu, "ERROR_NO_SUCH_DEVINST")]
    [DataRow(0x0000000Du, "CR_NO_SUCH_DEVNODE")]
    [DataRow(0x00000017u, "CR_REMOVE_VETOED")]
    [DataRow(0x00000028u, "CR_NOT_DISABLEABLE")]
    [DataRow(0x00000033u, "CR_ACCESS_DENIED")]
    [DataRow(0x00000025u, "CR_NO_SUCH_VALUE")]
    [DataRow(0x00000005u, "ERROR_ACCESS_DENIED")]
    [DataRow(0x00000424u, "ERROR_SERVICE_DOES_NOT_EXIST")]
    [DataRow(0x80004005u, "E_FAIL")]
    [DataRow(0x80070005u, "E_ACCESSDENIED")]
    [DataRow(0x800700B7u, "ERROR_ALREADY_EXISTS")]
    [DataRow(0x0000001Au, "CR_BUFFER_SMALL")]
    [DataRow(0x00000022u, "CR_NEED_RESTART")]
    [DataRow(0x00000057u, "ERROR_INVALID_PARAMETER")]
    [DataRow(0x000000EAu, "ERROR_MORE_DATA")]
    [DataRow(0x00000103u, "ERROR_NO_MORE_ITEMS")]
    [DataRow(0x00000490u, "ERROR_NOT_FOUND")]
    [DataRow(0x0000051Au, "ERROR_REVISION_MISMATCH")]
    [DataRow(0x00041325u, "SCHED_S_TASK_QUEUED")]
    [DataRow(0x80041326u, "SCHED_E_TASK_DISABLED")]
    public void NamesDocumentedCodes(uint code, string expected)
    {
        Assert.AreEqual(expected, NativeCodes.Name(unchecked((int)code)));
    }

    [TestMethod]
    [DataRow(0x12345678u, "0x12345678")]
    [DataRow(0xDEADBEEFu, "0xDEADBEEF")]
    [DataRow(0x00000001u, "0x00000001")]
    [DataRow(0xFFFFFFFFu, "0xFFFFFFFF")]
    public void UnknownCodesAreShownAsEightDigitHex(uint code, string expected)
    {
        Assert.AreEqual(expected, NativeCodes.Name(unchecked((int)code)));
    }

    [TestMethod]
    public void NegativeHresultsDecodeTheSameAsTheirUnsignedForm()
    {
        const int negative = -2147023728; // 0x80070490
        Assert.AreEqual("E_NOTFOUND", NativeCodes.Name(negative));
    }

    [TestMethod]
    public void SetupApiCodesAreNotConfusedWithWin32HresultForms()
    {
        // 0xE0000225 is not HRESULT_FROM_WIN32(0x225); the table names the SetupAPI value.
        Assert.AreEqual("ERROR_NO_SUCH_DEVICE_INTERFACE", NativeCodes.Name(unchecked((int)0xE0000225)));
        Assert.AreEqual("0x80070225", NativeCodes.Name(unchecked((int)0x80070225)));
    }
}
