using System.Reflection;
using System.Reflection.Emit;
using Earshot.Boot;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Contracts.Null;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using AppNs = Earshot.App;
using AudioNs = Earshot.Audio;
using IconsNs = Earshot.Icons;
using PopupNs = Earshot.Popup;
using TrayNs = Earshot.Tray;

namespace Earshot.Tests.Widget;

// The at-rest invariant, checked over the built assembly rather than trusted from reading the source.
// Every type under Earshot.Widget (including nested and compiler-generated types: async state machines,
// lambda closures) is scanned for a field, property, constructor parameter, method parameter, return type
// or generic argument (at any depth) that is, or reaches, a device controller or its concrete
// implementation; the whole Boot, AudioProtection or Audio.Connect (KS) families by namespace; the
// composition registry or the block coordinator; or a device-action result type such as ConnectResult in a
// delegate signature. It also walks every method body's IL (call, callvirt, newobj, ldfld, stfld, ldsfld,
// stsfld, ldftn, ldvirtftn, ldtoken), resolving each token, so a call to a forbidden static class
// (CfgMgr32, BluetoothApis, KsControl, TaskSchedulerCom cannot appear as a member type, only be called) is
// caught too. Base types and interfaces are checked by assignability, not equality, so a subclass or an
// implementer is caught even when it is not itself named in the forbidden list.
[TestClass]
public sealed class WidgetAtRestTests
{
    [TestMethod]
    public void NoWidgetTypeReferencesADeviceController()
    {
        Assembly assembly = typeof(Earshot.Widget.WidgetStatusService).Assembly;
        Assert.AreEqual("Earshot", assembly.GetName().Name);

        Type[] widgetTypes = assembly.GetTypes().Where(IsWidgetType).ToArray();
        Assert.IsTrue(widgetTypes.Length >= 10, "Too few Earshot.Widget types were read for a clean result to mean anything: " + widgetTypes.Length);

        List<string> found = Scanner.Scan(widgetTypes);

        Assert.AreEqual(0, found.Count, "A widget type reaches a forbidden device path:" + Environment.NewLine + string.Join(Environment.NewLine, found));
        Assert.AreEqual(
            0,
            Scanner.UnresolvedTokens,
            "A body token could not be resolved at all: the scanner cannot vouch for a widget method it gave up reading partway through.");
        Assert.AreEqual(
            0,
            Scanner.MethodBodyReadFailures,
            "GetMethodBody() threw for a real widget method: the scanner cannot vouch for a body it could not read at all.");
    }

    // The reviewers' five escapes, reproduced here so the scanner itself is proved against them, plus a
    // sixth: a subclass of a forbidden interface. None of these types is ever instantiated or its methods
    // called; the scanner only reads their shape and IL.
    [TestMethod]
    public void TheScannerCatchesEveryKnownEscape()
    {
        AssertFlags(typeof(EscapeConcreteConnectionControllerField), "a concrete ConnectionController field");
        AssertFlags(typeof(EscapeBlockControllerField), "a BlockController field");
        AssertFlags(typeof(EscapeServiceRegistryField), "a ServiceRegistry field");
        AssertFlags(typeof(EscapeInjectedConnectFunc), "an injected Func<Task<ConnectResult>>");
        AssertFlags(typeof(EscapeCallsCfgMgr32), "a body calling CfgMgr32.CM_Disable_DevNode");
        AssertFlags(typeof(EscapeInterfaceSubclass), "a subclass of a forbidden interface");
        AssertFlags(typeof(EscapeTrayContextFieldCallingOnIconMouseClick), "a TrayContext field calling OnIconMouseClick");
        AssertFlags(typeof(EscapeWidgetLocalDllImport), "a widget-local DllImport of CM_Disable_DevNode");
        AssertFlags(typeof(EscapeCallsProgramDispatch), "a body calling Earshot.Program.Dispatch");
        AssertFlags(typeof(EscapeReflectionGetType), "a body calling Type.GetType");
        AssertFlags(typeof(EscapeReflectionActivatorCreateInstance), "a body calling Activator.CreateInstance");
        AssertFlags(typeof(EscapeReflectionMethodInfoInvoke), "a body calling MethodInfo.Invoke on ConnectionController");
    }

    // Fourth return of this class: the scanner was a deny-list (a named, closed set of forbidden types,
    // namespaces and members), widened three times already and still incomplete by construction - anything
    // never named is implicitly trusted. Scanner.Scan below is now an allow-list instead: every type a
    // widget member, parameter, return, generic argument or method body reaches must be the widget's own,
    // System.*, one of the specific Windows.* projections it actually uses, or individually named here, or
    // it is refused regardless of what it is called. The five escapes below are the ones named when this
    // was asked for; each reaches something a widget body must never be able to reach by a path the
    // allow-list itself does not already forbid by construction (a forbidden type in a field or a call), so
    // each one specifically exercises IsForbiddenInvokePath: a named, dangerous entry point on an otherwise
    // ordinary, allowed type (System.Reflection.Assembly, System.Type, System.Reflection.ConstructorInfo,
    // System.Delegate, System.Diagnostics.Process, System.Runtime.InteropServices.NativeLibrary), forbidden
    // by name regardless of what is passed to it, since nothing in any of their own signatures says what
    // they can reach.
    [TestMethod]
    public void TheScannerCatchesEveryNamedInvokePathEscape()
    {
        AssertFlags(typeof(EscapeAssemblyGetTypeThenConstructorInvoke), "Assembly.GetType by name, then ConstructorInfo.Invoke");
        AssertFlags(typeof(EscapeTypeInvokeMember), "Type.InvokeMember");
        AssertFlags(typeof(EscapeDelegateDynamicInvoke), "Delegate.DynamicInvoke");
        AssertFlags(typeof(EscapeProcessStartDiagConnect), "Process.Start of Earshot.exe \"diag connect\"");
        AssertFlags(typeof(EscapeNativeLibraryGetExportThenDelegate), "NativeLibrary.GetExport, then Marshal.GetDelegateForFunctionPointer");
    }

    // Not caught, and cannot be by anything this scanner does: an Action (or Action<T>, Func<T>, and so on)
    // is exactly how much of the widget's own architecture already crosses a UI-post boundary on purpose
    // (WidgetCardPresenter and CaseOpenCardPresenter both take one, uiPost: Action<Action>, to run work on
    // the UI thread), so a delegate-typed field or parameter cannot be forbidden outright without also
    // forbidding that. Once constructed, invoking it calls whatever method its target was bound to at
    // runtime, which carries no type information at the call site (Invoke on any delegate type takes and
    // returns only what that delegate's own signature says, and that signature can be as uninformative as
    // Action - no parameters, no return, nothing to check) for a type-and-IL scanner to read. This is a
    // structural limit of what this scanner - or any scanner working from a built assembly's own metadata
    // alone - can prove, not a gap a wider allow-list closes: the fix, if one is wanted, is a narrower type
    // than a bare delegate at whatever call site actually runs on the case opening, reviewed by hand, not a
    // rule here. Recorded rather than silently left untested: EscapeOpaqueActionOnCaseOpen exists so a
    // future reader sees this was considered and found unreachable, not overlooked.
    [TestMethod]
    public void TheScannerCannotCatchAnOpaqueActionInvokedOnCaseOpen()
    {
        List<string> found = Scanner.Scan(new[] { typeof(EscapeOpaqueActionOnCaseOpen) });
        Assert.AreEqual(0, found.Count,
            "An opaque Action's own invocation carries no type information for this scanner to read; " +
            "this documents that limit rather than a real finding: " + string.Join(" | ", found));
    }

    private static void AssertFlags(Type escapeType, string what)
    {
        List<string> found = Scanner.Scan(new[] { escapeType });
        Assert.IsTrue(found.Count > 0, "The scanner did not catch " + what + " (" + escapeType.Name + ").");
    }

    // Walks to the outermost declaring type before reading Namespace, so a nested or compiler-generated type
    // (an async state machine, a lambda's display class) is included exactly when its outermost type is.
    private static bool IsWidgetType(Type t)
    {
        Type root = t;
        while (root.IsNested && root.DeclaringType is Type outer)
        {
            root = outer;
        }

        return root.Namespace == "Earshot.Widget" || (root.Namespace?.StartsWith("Earshot.Widget.", StringComparison.Ordinal) ?? false);
    }

    // ---- Escape fixtures, the scanner's own red proof: each reproduces one finding a reviewer made. ----

    private sealed class EscapeConcreteConnectionControllerField
    {
        private readonly Earshot.Audio.Connect.ConnectionController _field = null!;

        public Earshot.Audio.Connect.ConnectionController? Peek() => _field;
    }

    private sealed class EscapeBlockControllerField
    {
        private readonly BlockController _field = null!;

        public BlockController? Peek() => _field;
    }

    private sealed class EscapeServiceRegistryField
    {
        private readonly ServiceRegistry _field = null!;

        public ServiceRegistry? Peek() => _field;
    }

    private sealed class EscapeInjectedConnectFunc
    {
        public EscapeInjectedConnectFunc(Func<Task<ConnectResult>> connect)
        {
            _connect = connect;
        }

        private readonly Func<Task<ConnectResult>> _connect;

        public object Peek() => _connect;
    }

    private sealed class EscapeCallsCfgMgr32
    {
        public static void Touch() => _ = CfgMgr32.CM_Disable_DevNode(0, 0);
    }

    private sealed class EscapeInterfaceSubclass : IConnectionController
    {
        public Task<ConnectResult> ConnectAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ConnectResult> DisconnectAsync(Guid containerId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // A field of the tray's own context, reaching OnIconMouseClick (which itself calls the private
    // StartToggle) the way TrayContext.cs actually wires its notify icon's click handler.
    private sealed class EscapeTrayContextFieldCallingOnIconMouseClick
    {
        private readonly Earshot.App.TrayContext _tray = null!;

        public void Touch() => _tray.OnIconMouseClick(null, default!);
    }

    // A P/Invoke declared directly on a type the scanner is asked to check (standing in for a widget type),
    // rather than calling the vetted CfgMgr32.CM_Disable_DevNode through Interop.
    private static class EscapeWidgetLocalDllImport
    {
        [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
        private static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

        public static void Touch() => _ = CM_Disable_DevNode(0, 0);
    }

    // Third return of this class: Earshot.Program.Dispatch (the diag and gate command dispatcher) reaches
    // the elevated worker, Task Scheduler and the rest of Boot; a widget type calling it, or referencing
    // Earshot.Program at all, is exactly as forbidden as one calling TrayContext directly.
    private sealed class EscapeCallsProgramDispatch
    {
        public static int Touch() => Earshot.Program.Dispatch(Array.Empty<string>(), null!, null!);
    }

    // Type.GetType(string) looks a type up by name at runtime: a widget body that calls it can reach any
    // type at all, including a forbidden one, with nothing in the method's own signature to say so.
    private sealed class EscapeReflectionGetType
    {
        public static Type? Touch() => Type.GetType("Earshot.Audio.Connect.ConnectionController");
    }

    // Activator.CreateInstance(Type) constructs whatever Type.GetType found, the second half of the same
    // by-name escape.
    private sealed class EscapeReflectionActivatorCreateInstance
    {
        public static object? Touch(Type t) => Activator.CreateInstance(t);
    }

    // MethodInfo.Invoke (declared on the common base, MethodBase) calls whatever method a by-name lookup
    // found, completing the by-name path to ConnectionController.ConnectAsync: neither the field holding
    // the MethodInfo nor this call's own parameters (object, object[]) ever mention ConnectionController,
    // so nothing about this method's signature gives it away.
    private sealed class EscapeReflectionMethodInfoInvoke
    {
        public static object? Touch(MethodInfo m, object target) => m.Invoke(target, Array.Empty<object>());
    }

    // Assembly.GetType(string) is the same by-name lookup as Type.GetType(string), reached from an Assembly
    // reference instead of the static Type entry point; ConstructorInfo.Invoke(object[]) is its own member,
    // not inherited from MethodBase (only MethodInfo shares MethodBase's two-argument Invoke), so it needs
    // its own check.
    private sealed class EscapeAssemblyGetTypeThenConstructorInvoke
    {
        public static Type? LookUp(Assembly a) => a.GetType("Earshot.Audio.Connect.ConnectionController");

        public static object? Construct(ConstructorInfo c) => c.Invoke(Array.Empty<object>());
    }

    // Type.InvokeMember looks a member up by name and calls it in one step, bypassing both the MethodInfo
    // field a body would otherwise need and the MethodBase.Invoke check that catches the two-step form.
    private sealed class EscapeTypeInvokeMember
    {
        public static object? Touch(Type t, object target) =>
            t.InvokeMember("ConnectAsync", BindingFlags.InvokeMethod, null, target, Array.Empty<object>());
    }

    // A Delegate field or parameter is allowed (System.Delegate lives in System, and the widget's own
    // uiPost: Action<Action> pattern needs a plain delegate type to be reachable); DynamicInvoke is the
    // named, dangerous member on it, since it runs whatever the delegate's target turns out to be with no
    // further type information at this call site.
    private sealed class EscapeDelegateDynamicInvoke
    {
        public static object? Touch(Delegate d) => d.DynamicInvoke();
    }

    // Process.Start on the widget's own Earshot.exe, with "diag connect" as an argument, reaches the same
    // connect path as calling TrayContext directly would, through a child process instead of an in-process
    // call.
    private sealed class EscapeProcessStartDiagConnect
    {
        public static void Touch() => System.Diagnostics.Process.Start("Earshot.exe", "diag connect");
    }

    // NativeLibrary.GetExport resolves an exported function by name from an already-loaded module, and
    // Marshal.GetDelegateForFunctionPointer turns the result into a callable delegate: together they reach
    // any native entry point at all, including CfgMgr32's or BluetoothApis's own exports, with nothing in
    // either call's own signature naming which one.
    private sealed class EscapeNativeLibraryGetExportThenDelegate
    {
        public static void Touch(nint moduleHandle)
        {
            nint export = System.Runtime.InteropServices.NativeLibrary.GetExport(moduleHandle, "CM_Disable_DevNode");
            _ = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<Action>(export);
        }
    }

    // Not caught (see TheScannerCannotCatchAnOpaqueActionInvokedOnCaseOpen above): a plain Action field,
    // invoked with nothing at the call site to say what it runs.
    private sealed class EscapeOpaqueActionOnCaseOpen
    {
        private readonly Action _onCaseOpen = null!;

        public void Touch() => _onCaseOpen();
    }

    // The scanner itself: reusable so both the real assembly scan and the escape-fixture proof run the same
    // logic, and the positive result actually means what it claims.
    private static class Scanner
    {
        // The allow-list: every external type a widget member, parameter, return, generic argument or
        // method body may reach, enumerated by running a reflection walk of the built widget types against
        // the real assembly and recording every distinct external type it actually touches (App: 3, Audio:
        // 1, Contracts: 12, Icons: 2, Interop: 29, Popup: 10, Tray: 2 - 59 in total). Nothing else from
        // these seven namespaces is reachable, including the very types the old deny-list named by hand:
        // IConnectionController, ConnectResult, ControllerResult and the Null* controllers live in
        // Earshot.Contracts and Earshot.Contracts.Null alongside allowed types, so the namespace itself
        // cannot be allowed wholesale, only these 59 names; ServiceRegistry (Earshot.Composition) and
        // BlockCoordinator, TrayContext (Earshot.App) are excluded the same way, as are CfgMgr32,
        // BluetoothApis, KsControl, TaskSchedulerCom and IKsControl sitting inside Earshot.Interop next to
        // 29 allowed ones. A type reached that is not here, not under Earshot.Widget itself, and not under
        // System.* or Windows.* (the BCL and the WinRT projections, neither of which can reach an
        // Earshot-internal device path on its own) is refused regardless of what it is.
        private static readonly Type[] AllowedTypes =
        {
            typeof(AppNs.CardPlace), typeof(AppNs.CoordinatorRules), typeof(AppNs.RenderState),
            typeof(AudioNs.ComRelease),
            typeof(BootBlockStatus), typeof(CardAnchor), typeof(CardContent), typeof(DeviceSnapshot),
            typeof(DeviceSnapshotEventArgs), typeof(EarshotSettings), typeof(ICardPresenter), typeof(IDeviceMonitor),
            typeof(ILog), typeof(ISettingsStore), typeof(LogExtensions), typeof(LogLevel), typeof(StepOutcome),
            typeof(StepOutcomes),
            typeof(IconsNs.EarbudGlyph), typeof(IconsNs.GlyphState),
            typeof(APPBARDATA), typeof(BITMAPINFO), typeof(BITMAPINFOHEADER), typeof(BLENDFUNCTION),
            typeof(ComActivation), typeof(Dwm), typeof(IPersistFile), typeof(IPropertyStore), typeof(IShellLinkW),
            typeof(IUIAutomation), typeof(IUIAutomationCacheRequest), typeof(IUIAutomationCondition),
            typeof(IUIAutomationElement),
            typeof(IUIAutomationElementArray), typeof(LayeredWindow), typeof(MARGINS), typeof(MONITORINFO),
            typeof(NativeMethods), typeof(POINT), typeof(PROPERTYKEY), typeof(PROPVARIANT),
            typeof(PropVariantInterop), typeof(PropertyRead<>), typeof(RECT), typeof(SIZE), typeof(Shell),
            typeof(ShellLinkCom), typeof(TRACKMOUSEEVENT), typeof(TaskbarDpi), typeof(UiAutomation),
            typeof(PopupNs.CardPalette), typeof(PopupNs.CardPlacement), typeof(PopupNs.CardPresenter),
            typeof(PopupNs.CardTarget), typeof(PopupNs.CardTheme), typeof(PopupNs.DisplayArea),
            typeof(PopupNs.ICardEnvironment), typeof(PopupNs.NotificationStateReading),
            typeof(PopupNs.PlacementScene), typeof(PopupNs.TaskbarEdge),
            typeof(TrayNs.ToggleIntent), typeof(TrayNs.TrayReport),
        };

        private const BindingFlags AllDeclared =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<int, OpCode> OpCodesByValue = BuildOpCodeTable();

        // A body token this resolver could not walk at all: counted rather than silently dropped, since a
        // real one means IL that had something forbidden to say and the scanner simply gave up hearing it.
        public static int UnresolvedTokens { get; private set; }

        // GetMethodBody() throwing at all, counted rather than silently dropped: the class comment this
        // used to carry claimed a P/Invoke stub or an abstract member explained it, but GetMethodBody is
        // documented to return null for exactly those cases, not throw - the null check right after this
        // call already handles them - so a real throw here is unexplained and must not be read as proof of
        // nothing to find. NoWidgetTypeReferencesADeviceController asserts this stays 0 for the real scan.
        public static int MethodBodyReadFailures { get; private set; }

        public static List<string> Scan(IEnumerable<Type> types)
        {
            UnresolvedTokens = 0;
            MethodBodyReadFailures = 0;
            var found = new List<string>();

            foreach (Type type in types)
            {
                if (type.BaseType is Type baseType && IsForbidden(baseType))
                {
                    found.Add(type.FullName + " base type: " + Describe(baseType));
                }

                foreach (Type iface in type.GetInterfaces())
                {
                    if (IsForbidden(iface))
                    {
                        found.Add(type.FullName + " interface: " + Describe(iface));
                    }
                }

                foreach (FieldInfo field in type.GetFields(AllDeclared))
                {
                    if (References(field.FieldType))
                    {
                        found.Add(type.FullName + "." + field.Name + " field: " + Describe(field.FieldType));
                    }
                }

                foreach (PropertyInfo property in type.GetProperties(AllDeclared))
                {
                    if (References(property.PropertyType))
                    {
                        found.Add(type.FullName + "." + property.Name + " property: " + Describe(property.PropertyType));
                    }
                }

                foreach (ConstructorInfo ctor in type.GetConstructors(AllDeclared))
                {
                    foreach (ParameterInfo p in ctor.GetParameters())
                    {
                        if (References(p.ParameterType))
                        {
                            found.Add(type.FullName + " constructor parameter " + p.Name + ": " + Describe(p.ParameterType));
                        }
                    }

                    ScanBody(ctor, type, found);
                }

                foreach (MethodInfo method in type.GetMethods(AllDeclared))
                {
                    if (References(method.ReturnType))
                    {
                        found.Add(type.FullName + "." + method.Name + " return type: " + Describe(method.ReturnType));
                    }

                    foreach (ParameterInfo p in method.GetParameters())
                    {
                        if (References(p.ParameterType))
                        {
                            found.Add(type.FullName + "." + method.Name + " parameter " + p.Name + ": " + Describe(p.ParameterType));
                        }
                    }

                    // A native call declared directly on the type under scan, whether written as [DllImport]
                    // or generated from [LibraryImport]: both compile to a method carrying the runtime's own
                    // PInvokeImpl flag, so this catches either attribute without needing to name it.
                    if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                    {
                        found.Add(type.FullName + "." + method.Name + " is a native call (DllImport/LibraryImport) declared directly on this type.");
                    }

                    ScanBody(method, type, found);
                }
            }

            return found;
        }

        private static Dictionary<int, OpCode> BuildOpCodeTable()
        {
            var table = new Dictionary<int, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.GetValue(null) is OpCode opcode)
                {
                    // OpCode.Value is a signed Int16; a two-byte (0xFE-prefixed) opcode's value is negative
                    // when sign-extended (0xFE15 becomes -491), but the decoder below builds a positive
                    // 0xFE00 | secondByte key, so the low 16 bits are masked back to their unsigned form here.
                    table[opcode.Value & 0xFFFF] = opcode;
                }
            }

            return table;
        }

        // Walks method IL by hand (Module.ResolveMember through the token on every call, callvirt, newobj,
        // ldfld, stfld, ldsfld, stsfld, ldftn, ldvirtftn and ldtoken), since none of those show up as a
        // parameter, field or return type when the forbidden thing is only ever touched inside a body.
        private static void ScanBody(MethodBase method, Type owner, List<string> found)
        {
            MethodBody? body;
            try
            {
                body = method.GetMethodBody();
            }
            catch (Exception)
            {
                MethodBodyReadFailures++;
                return;
            }

            byte[]? il = body?.GetILAsByteArray();
            if (il is null)
            {
                return;
            }

            Module module = method.Module;
            Type[]? typeArgs = owner.IsGenericType ? owner.GetGenericArguments() : null;
            Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

            int i = 0;
            while (i < il.Length)
            {
                byte b = il[i];
                OpCode opcode;
                if (b == 0xFE)
                {
                    opcode = OpCodesByValue[0xFE00 | il[i + 1]];
                    i += 2;
                }
                else
                {
                    opcode = OpCodesByValue[b];
                    i += 1;
                }

                switch (opcode.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        i += 1;
                        break;
                    case OperandType.InlineVar:
                        i += 2;
                        break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                        CheckToken(module, BitConverter.ToInt32(il, i), typeArgs, methodArgs, owner, method, found);
                        i += 4;
                        break;
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineI:
                    case OperandType.InlineSig:
                    case OperandType.InlineString:
                    case OperandType.ShortInlineR:
                        i += 4;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        i += 8;
                        break;
                    case OperandType.InlineSwitch:
                        {
                            int count = BitConverter.ToInt32(il, i);
                            i += 4 + (count * 4);
                            break;
                        }
                    default:
                        throw new NotSupportedException(
                            "Unhandled IL operand type " + opcode.OperandType + " in " + owner.FullName + "." + method.Name);
                }
            }
        }

        private static void CheckToken(Module module, int token, Type[]? typeArgs, Type[]? methodArgs, Type owner, MethodBase method, List<string> found)
        {
            MemberInfo? member;
            try
            {
                member = module.ResolveMember(token, typeArgs, methodArgs);
            }
            catch (Exception)
            {
                // Counted, not silently dropped: a genuinely unresolvable token (a stand-alone signature) is
                // plausible, but the count is asserted at the call site so a real widget body full of these
                // cannot quietly stop being checked at all.
                UnresolvedTokens++;
                return;
            }

            if (member is null)
            {
                return;
            }

            string where = owner.FullName + "." + method.Name + " body";
            switch (member)
            {
                case Type t:
                    if (IsForbidden(t))
                    {
                        found.Add(where + " references type " + Describe(t));
                    }

                    break;

                // The DeclaringType checks below only ever mean "this body reaches into a field, constructor
                // or method belonging to some OTHER, forbidden type" - a type touching its own field,
                // constructor or method is not reaching anything external, whether or not that type's own
                // namespace happens to be on the allow list (an escape fixture declared in the test's own
                // namespace, not under Earshot.Widget, would otherwise flag on nothing more than its own
                // field initialiser). The field, constructor parameter and return/parameter type checks
                // alongside each of these still run regardless, since those catch a genuinely forbidden
                // type reached through shape rather than through "whose member is this".
                case FieldInfo f:
                    if (f.DeclaringType is Type fieldOwner && fieldOwner != owner && IsForbidden(fieldOwner))
                    {
                        found.Add(where + " accesses field " + Describe(fieldOwner) + "." + f.Name);
                    }
                    else if (References(f.FieldType))
                    {
                        found.Add(where + " accesses a field of type " + Describe(f.FieldType));
                    }

                    break;

                case ConstructorInfo c:
                    if (c.DeclaringType is Type ctorOwner && ctorOwner != owner && IsForbidden(ctorOwner))
                    {
                        found.Add(where + " constructs " + Describe(ctorOwner));
                    }

                    foreach (ParameterInfo p in c.GetParameters())
                    {
                        if (References(p.ParameterType))
                        {
                            found.Add(where + " constructs something taking " + Describe(p.ParameterType));
                        }
                    }

                    break;

                case MethodInfo m:
                    if (m.DeclaringType is Type methodOwner && methodOwner != owner && IsForbidden(methodOwner))
                    {
                        found.Add(where + " calls " + Describe(methodOwner) + "." + m.Name);
                    }

                    if (IsForbiddenInvokePath(m))
                    {
                        found.Add(where + " calls " + Describe(m.DeclaringType!) + "." + m.Name +
                            ", a named entry point that can reach a forbidden type, member or native export by name, invisibly to every other check here.");
                    }

                    if (References(m.ReturnType))
                    {
                        found.Add(where + " calls a method returning " + Describe(m.ReturnType));
                    }

                    foreach (ParameterInfo p in m.GetParameters())
                    {
                        if (References(p.ParameterType))
                        {
                            found.Add(where + " calls a method taking " + Describe(p.ParameterType));
                        }
                    }

                    break;
            }
        }

        // A widget type calling one of these can reach any type, member or native export at all at runtime,
        // by name, with nothing in the call's own signature to say so - each is allowed as a type (System.*
        // and the reflection/interop types themselves are ordinary, reachable BCL surface) but forbidden by
        // name as a call target regardless of what is passed to it:
        //   - Type.GetType(string) and Assembly.GetType(string) look a type up by name;
        //   - Type.InvokeMember looks a member up by name and calls it in one step;
        //   - Activator.CreateInstance(Type) builds whatever a by-name lookup found;
        //   - MethodBase.Invoke (MethodInfo's own) and ConstructorInfo.Invoke (its own, not inherited from
        //     MethodBase) call whatever member or constructor a lookup found;
        //   - Delegate.DynamicInvoke runs whatever the delegate's target turns out to be;
        //   - Process.Start launches an arbitrary child process by path and arguments;
        //   - NativeLibrary.GetExport resolves a native export by name, and
        //     Marshal.GetDelegateForFunctionPointer turns the result into a callable delegate.
        // Deliberately narrow: object.GetType() (declared on System.Object, not System.Type) is the
        // ordinary, harmless instance method every object has and stays unflagged.
        private static bool IsForbiddenInvokePath(MethodInfo m)
        {
            Type? declaring = m.DeclaringType;
            if (declaring is null)
            {
                return false;
            }

            return
                (declaring == typeof(Type) && (m.Name == nameof(Type.GetType) || m.Name == nameof(Type.InvokeMember))) ||
                declaring == typeof(Activator) ||
                (declaring == typeof(MethodBase) && m.Name == "Invoke") ||
                (declaring == typeof(ConstructorInfo) && m.Name == nameof(ConstructorInfo.Invoke)) ||
                (declaring == typeof(Assembly) && m.Name == nameof(Assembly.GetType)) ||
                ((declaring == typeof(Delegate) || declaring == typeof(MulticastDelegate)) && m.Name == nameof(Delegate.DynamicInvoke)) ||
                (declaring == typeof(System.Diagnostics.Process) && m.Name == nameof(System.Diagnostics.Process.Start)) ||
                (declaring == typeof(System.Runtime.InteropServices.NativeLibrary) && m.Name == nameof(System.Runtime.InteropServices.NativeLibrary.GetExport)) ||
                (declaring == typeof(System.Runtime.InteropServices.Marshal) && m.Name == nameof(System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer));
        }

        // True when a type is allowed: the widget's own (Earshot.Widget and nested/compiler-generated
        // namespaces under it), the BCL (System.*), the WinRT projections (Windows.*), an open generic
        // parameter (nothing concrete to check until it is substituted at the use site), or one of the 59
        // types named in AllowedTypes above. Everything else - by default, not by naming it - is forbidden.
        private static bool IsAllowed(Type candidate)
        {
            if (candidate.IsGenericParameter)
            {
                return true;
            }

            string? ns = candidate.Namespace;
            if (ns is not null)
            {
                if (ns == "Earshot.Widget" || ns.StartsWith("Earshot.Widget.", StringComparison.Ordinal))
                {
                    return true;
                }

                if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal))
                {
                    return true;
                }

                if (ns == "Windows" || ns.StartsWith("Windows.", StringComparison.Ordinal))
                {
                    return true;
                }

                // The interface every System.Windows.Forms accessible object implements. The card describes its own controls
                // to screen readers, and that reaches no device path: it is the framework's UI Automation face.
                if (ns == "Accessibility" && candidate.Name == "IAccessible")
                {
                    return true;
                }
            }

            Type key = candidate.IsGenericType && !candidate.IsGenericTypeDefinition
                ? candidate.GetGenericTypeDefinition()
                : candidate;

            foreach (Type allowed in AllowedTypes)
            {
                if (allowed == key)
                {
                    return true;
                }
            }

            return false;
        }

        // Kept under its old name at every other call site in this class (base type, interface, field,
        // property, parameter, return and body-token checks): now the allow-list's negation rather than a
        // deny-list lookup.
        private static bool IsForbidden(Type candidate) => !IsAllowed(candidate);

        // True when type is forbidden, or reaches a forbidden type through a generic argument, an array
        // element, or a by-ref/pointer element, at any depth.
        private static bool References(Type type)
        {
            Type effective = type.IsByRef || type.IsPointer ? type.GetElementType()! : type;

            if (IsForbidden(effective))
            {
                return true;
            }

            if (effective.IsArray && effective.GetElementType() is Type element)
            {
                return References(element);
            }

            if (effective.IsGenericType)
            {
                foreach (Type argument in effective.GetGenericArguments())
                {
                    if (References(argument))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string Describe(Type type) => type.FullName ?? type.Name;
    }
}
