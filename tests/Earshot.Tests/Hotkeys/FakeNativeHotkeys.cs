using Earshot.Hotkeys;

namespace Earshot.Tests.Hotkeys;

// One recorded Register or Unregister call, in order.
internal sealed record NativeCall(string Method, nint Handle, int Id, uint Modifiers, uint VirtualKey);

// Records every call and lets a test fix the result RegisterHotKey or UnregisterHotKey gives back for
// one hot key id; every id not set this way succeeds.
internal sealed class FakeNativeHotkeys : INativeHotkeys
{
    private readonly List<NativeCall> _calls = new();
    private readonly Dictionary<int, NativeCallResult> _resultsById = new();

    public IReadOnlyList<NativeCall> Calls => _calls;

    public void SetResult(int id, NativeCallResult result) => _resultsById[id] = result;

    public NativeCallResult Register(nint windowHandle, int hotkeyId, uint modifiers, uint virtualKey)
    {
        _calls.Add(new NativeCall("RegisterHotKey", windowHandle, hotkeyId, modifiers, virtualKey));
        return _resultsById.TryGetValue(hotkeyId, out NativeCallResult result) ? result : NativeCallResult.Success();
    }

    public NativeCallResult Unregister(nint windowHandle, int hotkeyId)
    {
        _calls.Add(new NativeCall("UnregisterHotKey", windowHandle, hotkeyId, 0, 0));
        return _resultsById.TryGetValue(hotkeyId, out NativeCallResult result) ? result : NativeCallResult.Success();
    }
}
