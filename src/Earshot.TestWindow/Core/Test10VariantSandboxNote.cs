namespace Earshot.TestWindow.Core;

// Whether the row detail area shows Copy.Test10VariantNotSandboxTestable: only ever in a sandbox
// window, and only for row 10's variants 2 to 5, since tools\live-tests\selftest\Fakes.psm1 has a
// StartStates entry for variant 1 only. A pure decision, kept apart from MainForm so a test can
// prove "never shown outside a sandbox window" without ever constructing a real, non-sandboxed
// form.
internal static class Test10VariantSandboxNote
{
    internal static bool ShouldShow(bool sandboxed, string rowNumber, int variantNumber) =>
        sandboxed && rowNumber == "10" && variantNumber is >= 2 and <= 5;
}
