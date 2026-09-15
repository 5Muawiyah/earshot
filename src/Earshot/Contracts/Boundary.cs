namespace Earshot.Contracts;

// The only strings the gate accepts as $(Arg0). Frozen; fuzz-tested.
public static class GateVerbs
{
    public const string Block      = "block";
    public const string Allow      = "allow";
    public const string Status     = "status";
    public const string SetBootOn  = "setboot-on";
    public const string SetBootOff = "setboot-off";
    public const string ProtectOn  = "protect-on";
    public const string ProtectOff = "protect-off";
    public const string SetDevice  = "set-device";   // the ONLY verb that carries an address arg
    public const string Boot       = "boot";         // supplied by the BootBlock trigger, no nonce

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        { Block, Allow, Status, SetBootOn, SetBootOff, ProtectOn, ProtectOff, SetDevice, Boot };
}

public static class BoundaryValidation
{
    // $(Arg1): the request nonce the tray generated. ^[0-9a-f]{32}$
    public static bool IsNonce(string? s) =>
        s is { Length: 32 } && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    // $(Arg2): the 12-hex device address (set-device only). ^[0-9A-F]{12}$
    public static bool IsAddress12(string? s) =>
        s is { Length: 12 } && s.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'F'));

    // Task Scheduler may leave an unsupplied arg as the literal placeholder; treat it as empty.
    public static string Normalise(string? arg) =>
        string.IsNullOrEmpty(arg) || arg == "$(Arg2)" ? "" : arg;
}
