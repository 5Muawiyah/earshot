using Earshot.Widget;

namespace Earshot.Tests.Widget;

// Synthetic Apple manufacturer-data sections for the widget's tests. Nothing here is read from a real
// device: no Bluetooth address, no captured payload, no real model or colour code, no device name.
// WidgetFixtureHygieneTests scans this folder for anything that looks like it was.
internal static class WidgetFixtures
{
    // "No product": neither permitted source lists a model using these bytes.
    public const byte ModelHigh = 0xEE;
    public const byte ModelLow = 0xEE;
    public const byte StrangerModelLow = 0xEF;
    public const byte StrangerModelHigh = ModelHigh;

    // "No colour in the paper's table."
    public const byte Colour = 0xEE;
    public const byte StrangerColour = 0xED;

    // The 25-byte value of a documented-form 0x07 item: prefix, model, status, the two battery bytes,
    // lid, colour, the reserved byte, then 16 bytes that are not the encrypted payload of any real
    // AirPods (0x10 to 0x1F, just a fixed, recognisable run).
    public static byte[] ProximityValue(
        byte modelHigh = ModelHigh,
        byte modelLow = ModelLow,
        byte status = 0x00,
        byte batteryA = 0x00,
        byte batteryB = 0x00,
        byte lid = 0x00,
        byte colour = Colour,
        byte reserved = 0x00)
    {
        var value = new byte[ProximityParser.DocumentedLength];
        value[0] = ProximityParser.DocumentedPrefix;
        value[1] = modelHigh;
        value[2] = modelLow;
        value[3] = status;
        value[4] = batteryA;
        value[5] = batteryB;
        value[6] = lid;
        value[7] = colour;
        value[8] = reserved;
        for (int i = 0; i < ProximityParser.EncryptedLength; i++)
        {
            value[9 + i] = (byte)(0x10 + i);
        }

        return value;
    }

    // A full 27-byte Apple section holding one documented-form item: type 0x07, length 25, the value above.
    public static byte[] Proximity(
        byte modelHigh = ModelHigh,
        byte modelLow = ModelLow,
        byte status = 0x00,
        byte batteryA = 0x00,
        byte batteryB = 0x00,
        byte lid = 0x00,
        byte colour = Colour,
        byte reserved = 0x00) =>
        Item(ProximityParser.ProximityType, ProximityValue(modelHigh, modelLow, status, batteryA, batteryB, lid, colour, reserved));

    // The 17-byte 0x06 form the probe saw: prefix 0x06 followed by 16 bytes that are not a captured payload.
    public static byte[] UnknownSeventeenByteForm()
    {
        var value = new byte[17];
        value[0] = 0x06;
        for (int i = 0; i < 16; i++)
        {
            value[1 + i] = (byte)(0x20 + i);
        }

        return Item(ProximityParser.ProximityType, value);
    }

    // Any type/value as a type-length-value item.
    public static byte[] Item(byte type, byte[] value)
    {
        var result = new byte[2 + value.Length];
        result[0] = type;
        result[1] = checked((byte)value.Length);
        Array.Copy(value, 0, result, 2, value.Length);
        return result;
    }

    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
