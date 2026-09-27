namespace Earshot.Widget;

// Pure layout parsing of one manufacturer-data section: the company identifier and the bytes exactly as
// BluetoothLEManufacturerData.CompanyId and .Data give them. No clock, no I/O, no allocation but the result.
// https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothlemanufacturerdata
//
// The section is walked as type, length, value items from offset 0. The first 0x07 item decides the
// result; every 0x07 item, including a second one, is still counted in ProximityItems, because a run of
// advertisements from a device sending more than one proximity-shaped item is something the counters
// should be able to show even though only the first is ever decoded. TypesSeen only gathers the types met
// before that first 0x07 item, since it exists to explain a WrongType result.
public static class ProximityParser
{
    public const ushort AppleCompanyId = 0x004C;
    public const byte ProximityType = 0x07;
    public const byte DocumentedPrefix = 0x01;
    public const int DocumentedLength = 25;
    public const int EncryptedLength = 16;

    public static ProximityParse Parse(ushort companyId, ReadOnlySpan<byte> data)
    {
        if (companyId != AppleCompanyId)
        {
            return new ProximityParse(ProximityParseStatus.WrongCompany, null, null, null, 0, Array.Empty<byte>());
        }

        var typesSeen = new List<byte>();
        int proximityItems = 0;
        ProximityParseStatus? decidedStatus = null;
        ProximityMessage? decidedMessage = null;
        byte? decidedPrefix = null;
        int? decidedLength = null;

        int offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 2)
            {
                return new ProximityParse(ProximityParseStatus.Truncated, null, null, null, proximityItems, typesSeen);
            }

            byte type = data[offset];
            byte length = data[offset + 1];
            int valueStart = offset + 2;
            if (valueStart + length > data.Length)
            {
                return new ProximityParse(ProximityParseStatus.Truncated, null, null, null, proximityItems, typesSeen);
            }

            if (type == ProximityType)
            {
                proximityItems++;
                if (decidedStatus is null)
                {
                    ReadOnlySpan<byte> value = data.Slice(valueStart, length);
                    (decidedStatus, decidedMessage, decidedPrefix, decidedLength) = DecideProximityItem(value, length);
                }
            }
            else if (decidedStatus is null)
            {
                typesSeen.Add(type);
            }

            offset = valueStart + length;
        }

        return decidedStatus is { } status
            ? new ProximityParse(status, decidedMessage, decidedPrefix, decidedLength, proximityItems, typesSeen)
            : new ProximityParse(ProximityParseStatus.WrongType, null, null, null, proximityItems, typesSeen);
    }

    private static (ProximityParseStatus Status, ProximityMessage? Message, byte? Prefix, int? Length) DecideProximityItem(
        ReadOnlySpan<byte> value, int length)
    {
        if (length == 0)
        {
            return (ProximityParseStatus.UnknownForm, null, null, 0);
        }

        byte prefix = value[0];
        if (prefix == DocumentedPrefix && length == DocumentedLength)
        {
            var message = new ProximityMessage(
                ModelHigh: value[1],
                ModelLow: value[2],
                Status: value[3],
                BatteryA: value[4],
                BatteryB: value[5],
                Lid: value[6],
                Colour: value[7],
                Reserved: value[8]);
            return (ProximityParseStatus.Ok, message, null, null);
        }

        if (prefix == DocumentedPrefix && length < DocumentedLength)
        {
            return (ProximityParseStatus.Truncated, null, DocumentedPrefix, length);
        }

        // Anything else, including a longer 0x01 form: a shape the sources do not describe.
        return (ProximityParseStatus.UnknownForm, null, prefix, length);
    }
}
