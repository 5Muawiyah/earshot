using System.Text.RegularExpressions;

namespace Earshot.Contracts;

// Pure, hardware-free device-node predicate shared verbatim by tray verification (Interop\CfgMgr32
// reads) and the SYSTEM gate action, so selection logic cannot drift. The brief regex alone is
// NOT sufficient: ^(BTHENUM|BTHLE|BTHLEDEVICE|BTH)\\ also matches the radio bus node
// BTH\MS_BTHBRB\... The real guards are container + address.
public static partial class NodeMatch
{
    [GeneratedRegex(@"^(BTHENUM|BTHLE|BTHLEDEVICE|BTH)\\", RegexOptions.IgnoreCase)]
    private static partial Regex BluetoothPrefix();

    // The Windows PC container. Never a target. https://learn.microsoft.com/en-us/windows-hardware/drivers/install/container-ids-for-bluetooth-devices
    public static readonly Guid PcContainer = new("00000000-0000-0000-FFFF-FFFFFFFFFFFF");

    public static bool IsValidTargetContainer(Guid c) => c != Guid.Empty && c != PcContainer;

    // A disable target: in the pinned container AND a BTHENUM/BTH bus node AND carries the 12-hex address.
    // Excludes SWD\MMDEVAPI endpoints (go phantom when disconnected), BTHHFENUM children (protection
    // removes/re-adds them), the radio (PC container, no address), and the iPhone (different container).
    // The pinned address must be well formed (12 upper-case hex characters): an empty or partial string
    // is contained in every id and would reduce the three guards to two, so it selects nothing.
    public static bool IsDisableTarget(string instanceId, Guid nodeContainer,
                                       Guid pinnedContainer, string address12) =>
        IsValidTargetContainer(pinnedContainer) &&
        BoundaryValidation.IsAddress12(address12) &&
        nodeContainer == pinnedContainer &&
        BluetoothPrefix().IsMatch(instanceId) &&
        instanceId.Contains(address12, StringComparison.OrdinalIgnoreCase);

    // Name matching for discovery. NEVER wildcards: PS-style '[' throws, backtick is not escaped,
    // and a curly U+2019 apostrophe does not match a straight one. Ordinal contains handles all.
    public static bool NameMatches(string? name, string match) =>
        name is not null && name.Contains(match, StringComparison.OrdinalIgnoreCase);
}
