namespace Earshot.Widget;

// One battery nibble to a percent, or "unknown, never shown as a number". Levels 0 to 10 are tens of percent
// (furiousMAC Continuity notes). That 0xF means unknown is neither source's statement but the local reading:
// the one message in the saved set-up records from a bud just out of the case carried case nibble 0xF with no
// charging bits, consistent with the paper's remark that the case level is only exposed with the lid open. No
// caller ever turns a null percent into a number, a dash with a number, or an interpolation.
public static class BatteryNibble
{
    public static int? ToPercent(int nibble) => nibble is >= 0 and <= 10 ? nibble * 10 : null;

    // 11 to 14 are also unknown, but unlike 0xF they are a shape the sources never describe at all, so a
    // caller can log that the form drifted rather than treating them the same as the documented "unknown".
    public static bool IsOutOfRange(int nibble) => nibble is >= 11 and <= 14;
}
