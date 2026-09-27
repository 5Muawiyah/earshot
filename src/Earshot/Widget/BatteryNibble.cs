namespace Earshot.Widget;

// One battery nibble to a percent, or "unknown, never shown as a number". Neither permitted source says
// 0xF means unknown; that is the owner's own rule for this decode. No caller ever turns a null percent
// into a number, a dash with a number, or an interpolation.
public static class BatteryNibble
{
    public static int? ToPercent(int nibble) => nibble is >= 0 and <= 10 ? nibble * 10 : null;

    // 11 to 14 are also unknown, but unlike 0xF they are a shape the sources never describe at all, so a
    // caller can log that the form drifted rather than treating them the same as the documented "unknown".
    public static bool IsOutOfRange(int nibble) => nibble is >= 11 and <= 14;
}
