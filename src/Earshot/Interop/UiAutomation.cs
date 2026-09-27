using System.Runtime.InteropServices;

namespace Earshot.Interop;

// The native UI Automation COM surface the widget's taskbar reader uses to find free space on the bar.
// [ComImport] early-bound interfaces in the exact vtable order of UIAutomationCore.idl (Windows SDK
// 10.0.26100.0), the same approach TaskSchedulerCom.cs and CoreAudio.cs already take: every method keeps
// its HRESULT ([PreserveSig]), the slot order is pinned by ComVtableOrderTests, and the managed
// UIAutomationClient.dll (the WPF-profile managed client, hundreds of extra files in a self-contained
// publish) is never referenced.
//
// Declared, not all called: every method up to the last one Earshot uses (CreateTrueCondition, slot 21 on
// IUIAutomation) is declared with a real signature, as the house convention already does for
// TaskSchedulerCom's largely-unused interfaces, so the slot order holds regardless of which members a
// caller reaches.
//
// Threading: created once per UiaTaskbarReader worker thread (a dedicated MTA thread), never on the UI
// thread: UI Automation documents that its calls must come from a thread that owns no window.
internal static class UiAutomation
{
    // CLSID_CUIAutomation.
    internal static readonly Guid CLSID_CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    // TreeScope (UIAutomationCore.h).
    internal const int TreeScope_Element = 0x1;
    internal const int TreeScope_Children = 0x2;
    internal const int TreeScope_Descendants = 0x4;

    // AutomationElementMode (UIAutomationClient.h): AutomationElementMode_None = 0, Full = 1. Earshot never
    // sets this (the default, Full, is used), but the value is declared for put_AutomationElementMode's
    // vtable slot, so it must still match the header rather than the first available enumerator value.
    internal const int AutomationElementMode_Full = 1;

    // Property ids (UIAutomationClient.h).
    internal const int UIA_BoundingRectanglePropertyId = 30001;
    internal const int UIA_ControlTypePropertyId = 30003;
    internal const int UIA_NamePropertyId = 30005;
    internal const int UIA_AutomationIdPropertyId = 30011;
    internal const int UIA_ClassNamePropertyId = 30012;
    internal const int UIA_NativeWindowHandlePropertyId = 30020;
    internal const int UIA_IsOffscreenPropertyId = 30022;

    // UIA_ControlTypeId values Earshot compares against.
    internal const int UIA_ButtonControlTypeId = 50000;
    internal const int UIA_PaneControlTypeId = 50033;
}

// https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomation
[ComImport]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    [PreserveSig]
    int CompareElements(IUIAutomationElement? el1, IUIAutomationElement? el2, [MarshalAs(UnmanagedType.Bool)] out bool areSame);

    [PreserveSig]
    int CompareRuntimeIds(int[]? runtimeId1, int[]? runtimeId2, [MarshalAs(UnmanagedType.Bool)] out bool areSame);

    [PreserveSig]
    int GetRootElement(out IUIAutomationElement? root);

    // The taskbar's own root element from its window handle.
    // https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation-elementfromhandle
    [PreserveSig]
    int ElementFromHandle(nint hwnd, out IUIAutomationElement? element);

    [PreserveSig]
    int ElementFromPoint(POINT pt, out IUIAutomationElement? element);

    [PreserveSig]
    int GetFocusedElement(out IUIAutomationElement? element);

    [PreserveSig]
    int GetRootElementBuildCache(IUIAutomationCacheRequest? cacheRequest, out IUIAutomationElement? root);

    [PreserveSig]
    int ElementFromHandleBuildCache(nint hwnd, IUIAutomationCacheRequest? cacheRequest, out IUIAutomationElement? element);

    [PreserveSig]
    int ElementFromPointBuildCache(POINT pt, IUIAutomationCacheRequest? cacheRequest, out IUIAutomationElement? element);

    [PreserveSig]
    int GetFocusedElementBuildCache(IUIAutomationCacheRequest? cacheRequest, out IUIAutomationElement? element);

    [PreserveSig]
    int CreateTreeWalker(IUIAutomationCondition? pCondition, out nint treeWalker);

    [PreserveSig]
    int get_ControlViewWalker(out nint walker);

    [PreserveSig]
    int get_ContentViewWalker(out nint walker);

    [PreserveSig]
    int get_RawViewWalker(out nint walker);

    [PreserveSig]
    int get_RawViewCondition(out IUIAutomationCondition? condition);

    [PreserveSig]
    int get_ControlViewCondition(out IUIAutomationCondition? condition);

    [PreserveSig]
    int get_ContentViewCondition(out IUIAutomationCondition? condition);

    // https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation-createcacherequest
    [PreserveSig]
    int CreateCacheRequest(out IUIAutomationCacheRequest? cacheRequest);

    // https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomation-createtruecondition
    [PreserveSig]
    int CreateTrueCondition(out IUIAutomationCondition? newCondition);
}

// https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationelement
[ComImport]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    [PreserveSig]
    int SetFocus();

    [PreserveSig]
    int GetRuntimeId(out int[]? runtimeId);

    [PreserveSig]
    int FindFirst(int scope, IUIAutomationCondition? condition, out IUIAutomationElement? found);

    [PreserveSig]
    int FindAll(int scope, IUIAutomationCondition? condition, out IUIAutomationElementArray? found);

    [PreserveSig]
    int FindFirstBuildCache(int scope, IUIAutomationCondition? condition, IUIAutomationCacheRequest? cacheRequest, out IUIAutomationElement? found);

    // UiaTaskbarReader.TryReadOccupants calls this with TreeScope_Descendants passed as the scope
    // parameter here, not through the cache request's own put_TreeScope: a local probe on a real
    // Shell_TrayWnd found put_TreeScope(Descendants) on the cache request is what makes
    // GetCachedPropertyValue return E_INVALIDARG afterwards, while the documented pairing (this method's
    // own scope parameter, cache request left at its default tree scope) reads every occupant's cached
    // properties without error.
    // https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationelement-findallbuildcache
    [PreserveSig]
    int FindAllBuildCache(int scope, IUIAutomationCondition? condition, IUIAutomationCacheRequest? cacheRequest, out IUIAutomationElementArray? found);

    [PreserveSig]
    int BuildUpdatedCache(IUIAutomationCacheRequest? cacheRequest, out IUIAutomationElement? updated);

    [PreserveSig]
    int GetCurrentPropertyValue(int propertyId, out object? value);

    [PreserveSig]
    int GetCurrentPropertyValueEx(int propertyId, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefaultValue, out object? value);

    // Reads a property named in the cache request that built this element (FindAllBuildCache above). See
    // that method's own comment for the tree-scope detail a real read needed to get this to work.
    // https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationelement-getcachedpropertyvalue
    [PreserveSig]
    int GetCachedPropertyValue(int propertyId, out object? value);
}

// https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationelementarray
[ComImport]
[Guid("14314595-b4bc-4055-95f2-58f2e42c9855")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElementArray
{
    [PreserveSig]
    int get_Length(out int length);

    [PreserveSig]
    int GetElement(int index, out IUIAutomationElement? element);
}

// Opaque to Earshot: passed between IUIAutomation calls and never inspected. No methods beyond IUnknown,
// so it carries no vtable slots for ComVtableOrderTests to pin.
// https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationcondition
[ComImport]
[Guid("352ffba8-0973-437c-a61f-f64cafd81df9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCondition
{
}

// https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationcacherequest
[ComImport]
[Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCacheRequest
{
    // https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationcacherequest-addproperty
    [PreserveSig]
    int AddProperty(int propertyId);

    [PreserveSig]
    int AddPattern(int patternId);

    [PreserveSig]
    int Clone(out IUIAutomationCacheRequest? clone);

    [PreserveSig]
    int get_TreeScope(out int scope);

    // https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationcacherequest-put_treescope
    [PreserveSig]
    int put_TreeScope(int scope);

    [PreserveSig]
    int get_TreeFilter(out IUIAutomationCondition? filter);

    [PreserveSig]
    int put_TreeFilter(IUIAutomationCondition? filter);

    [PreserveSig]
    int get_AutomationElementMode(out int mode);

    [PreserveSig]
    int put_AutomationElementMode(int mode);
}
