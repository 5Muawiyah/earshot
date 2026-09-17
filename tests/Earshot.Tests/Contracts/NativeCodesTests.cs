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

    // The same small value means different things to CfgMgr32 and to a Win32 API. Access denied must
    // never be reported for a CONFIGRET that says the devnode handle is invalid, and the reverse.
    [TestMethod]
    [DataRow(0x00000005u, "CR_INVALID_DEVNODE", "ERROR_ACCESS_DENIED")]
    [DataRow(0x0000000Du, "CR_NO_SUCH_DEVNODE", "ERROR_INVALID_DATA")]
    [DataRow(0x00000000u, "CR_SUCCESS", "ERROR_SUCCESS")]
    [DataRow(0x00000002u, "CR_OUT_OF_MEMORY", "ERROR_FILE_NOT_FOUND")]
    [DataRow(0x00000003u, "CR_INVALID_POINTER", "ERROR_PATH_NOT_FOUND")]
    public void CollidingValuesAreNamedByTheirFamily(uint code, string configRet, string win32)
    {
        Assert.AreEqual(configRet, NativeCodes.ConfigRet(code));
        Assert.AreEqual(win32, NativeCodes.Win32(code));
    }

    [TestMethod]
    [DataRow(0x00u, "CR_SUCCESS")]
    [DataRow(0x04u, "CR_INVALID_FLAG")]
    [DataRow(0x13u, "CR_FAILURE")]
    [DataRow(0x17u, "CR_REMOVE_VETOED")]
    [DataRow(0x1Au, "CR_BUFFER_SMALL")]
    [DataRow(0x1Eu, "CR_INVALID_DEVICE_ID")]
    [DataRow(0x22u, "CR_NEED_RESTART")]
    [DataRow(0x24u, "CR_DEVICE_NOT_THERE")]
    [DataRow(0x25u, "CR_NO_SUCH_VALUE")]
    [DataRow(0x28u, "CR_NOT_DISABLEABLE")]
    [DataRow(0x33u, "CR_ACCESS_DENIED")]
    [DataRow(0x35u, "CR_INVALID_PROPERTY")]
    [DataRow(0x3Bu, "CR_INVALID_STRUCTURE_SIZE")]
    [DataRow(0x3Cu, "0x0000003C")]
    [DataRow(0x80070005u, "0x80070005")]
    public void ConfigRetNamesTheCfgMgr32Range(uint code, string expected)
    {
        Assert.AreEqual(expected, NativeCodes.ConfigRet(code));
    }

    [TestMethod]
    [DataRow(87u, "ERROR_INVALID_PARAMETER")]
    [DataRow(234u, "ERROR_MORE_DATA")]
    [DataRow(259u, "ERROR_NO_MORE_ITEMS")]
    [DataRow(1060u, "ERROR_SERVICE_DOES_NOT_EXIST")]
    [DataRow(1168u, "ERROR_NOT_FOUND")]
    [DataRow(1332u, "ERROR_NONE_MAPPED")]
    [DataRow(1223u, "ERROR_CANCELLED")]
    [DataRow(1306u, "ERROR_REVISION_MISMATCH")]
    [DataRow(0x80070057u, "E_INVALIDARG")]
    [DataRow(0x33u, "0x00000033")]
    [DataRow(0x1Au, "0x0000001A")]
    public void Win32NamesWin32CodesAndHresultsCarriedInADword(uint code, string expected)
    {
        Assert.AreEqual(expected, NativeCodes.Win32(code));
    }

    [TestMethod]
    public void NoNativeCallCodesAreFailuresWithTheirOwnNames()
    {
        Assert.AreEqual("NOT_ATTEMPTED", NativeCodes.Name(NativeCodes.NotAttempted));
        Assert.AreEqual("NOT_AVAILABLE", NativeCodes.Name(NativeCodes.NotAvailable));
        Assert.IsLessThan(0, NativeCodes.NotAttempted);
        Assert.IsLessThan(0, NativeCodes.NotAvailable);

        // The customer bit is set and the NTSTATUS and reserved bits are clear, so no Windows code,
        // including the SetupAPI 0xE000xxxx range, has these values.
        foreach (int code in new[] { NativeCodes.NotAttempted, NativeCodes.NotAvailable })
        {
            Assert.AreEqual(0xA0000000u, unchecked((uint)code) & 0xF8000000u);
        }
    }

    [TestMethod]
    public void SetupApiCodesAreNotConfusedWithWin32HresultForms()
    {
        // 0xE0000225 is not HRESULT_FROM_WIN32(0x225); the table names the SetupAPI value.
        Assert.AreEqual("ERROR_NO_SUCH_DEVICE_INTERFACE", NativeCodes.Name(unchecked((int)0xE0000225)));
        Assert.AreEqual("0x80070225", NativeCodes.Name(unchecked((int)0x80070225)));
    }
}
