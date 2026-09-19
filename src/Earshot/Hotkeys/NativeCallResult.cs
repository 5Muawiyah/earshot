namespace Earshot.Hotkeys;

// The raw outcome of one RegisterHotKey or UnregisterHotKey call. ErrorCode is 0 on success.
public readonly record struct NativeCallResult(bool Succeeded, int ErrorCode)
{
    public static NativeCallResult Success() => new(true, 0);

    public static NativeCallResult Failure(int errorCode) => new(false, errorCode);
}
