using System.Reflection;
using System.Reflection.Emit;
using Earshot.Boot;
using Earshot.Composition;
using Earshot.Contracts;
using Earshot.Contracts.Null;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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

    // Round 2 residual: a field of the tray's own context, reaching OnIconMouseClick (which itself calls the
    // private StartToggle) the way TrayContext.cs actually wires its notify icon's click handler.
    private sealed class EscapeTrayContextFieldCallingOnIconMouseClick
    {
        private readonly Earshot.App.TrayContext _tray = null!;

        public void Touch() => _tray.OnIconMouseClick(null, default!);
    }

    // Round 2 residual: a P/Invoke declared directly on a type the scanner is asked to check (standing in for
    // a widget type), rather than calling the vetted CfgMgr32.CM_Disable_DevNode through Interop.
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

    // The scanner itself: reusable so both the real assembly scan and the escape-fixture proof run the same
    // logic, and the positive result actually means what it claims.
    private static class Scanner
    {
        private static readonly Type[] ForbiddenTypes =
        {
            typeof(IConnectionController), typeof(IBlockController), typeof(IAudioProtectionController),
            typeof(ConnectResult), typeof(ControllerResult),
            typeof(NullConnectionController), typeof(NullBlockController), typeof(NullAudioProtectionController),
            typeof(ServiceRegistry), typeof(Earshot.App.BlockCoordinator),
            typeof(IKsControl), typeof(CfgMgr32), typeof(BluetoothApis), typeof(KsControl), typeof(TaskSchedulerCom),
            // The tray's own context: a field, a return, a parameter, or a call to any of its methods
            // (including OnIconMouseClick and the private StartToggle it calls) is forbidden, since reaching
            // either is reaching the tray's connect/disconnect toggle.
            typeof(Earshot.App.TrayContext),
            // The command dispatcher (diag, gate): reaches the elevated worker, Task Scheduler and Boot.
            typeof(Earshot.Program),
        };

        // "Anything in Earshot.Boot, Earshot.Protection [the AudioProtection namespace], Audio.Connect (KS
        // path)": whole families caught by namespace rather than naming every type in them.
        private static readonly string[] ForbiddenNamespacePrefixes =
        {
            "Earshot.Boot", "Earshot.AudioProtection", "Earshot.Audio.Connect",
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

                case FieldInfo f:
                    if (f.DeclaringType is Type fieldOwner && IsForbidden(fieldOwner))
                    {
                        found.Add(where + " accesses field " + Describe(fieldOwner) + "." + f.Name);
                    }
                    else if (References(f.FieldType))
                    {
                        found.Add(where + " accesses a field of type " + Describe(f.FieldType));
                    }

                    break;

                case ConstructorInfo c:
                    if (c.DeclaringType is Type ctorOwner && IsForbidden(ctorOwner))
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
                    if (m.DeclaringType is Type methodOwner && IsForbidden(methodOwner))
                    {
                        found.Add(where + " calls " + Describe(methodOwner) + "." + m.Name);
                    }

                    if (IsReflectionEntryPoint(m))
                    {
                        found.Add(where + " calls " + Describe(m.DeclaringType!) + "." + m.Name +
                            ", a reflection entry point that can reach a forbidden type or member by name, invisibly to every other check here.");
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

        // A widget type calling one of these can reach any type or member by name at runtime, including a
        // forbidden one, with nothing in the call's own signature to say so: Type.GetType(string) looks a
        // type up by name, Activator.CreateInstance(Type) builds whatever that found, and
        // MethodBase.Invoke (the base every MethodInfo and ConstructorInfo shares) calls whatever member a
        // lookup found. Deliberately narrow: object.GetType() (declared on System.Object, not System.Type)
        // is the ordinary, harmless instance method every object has and stays unflagged; only the static
        // by-name lookup on System.Type itself is checked here.
        private static bool IsReflectionEntryPoint(MethodInfo m) =>
            (m.DeclaringType == typeof(Type) && m.Name == nameof(Type.GetType)) ||
            m.DeclaringType == typeof(Activator) ||
            m.DeclaringType == typeof(MethodBase);

        // True when type is forbidden itself, is assignable to a forbidden type (a subclass or an
        // implementer, not just an exact match), or sits under a forbidden namespace.
        private static bool IsForbidden(Type candidate)
        {
            foreach (Type f in ForbiddenTypes)
            {
                if (f.IsAssignableFrom(candidate))
                {
                    return true;
                }
            }

            string? ns = candidate.Namespace;
            if (ns is null)
            {
                return false;
            }

            foreach (string prefix in ForbiddenNamespacePrefixes)
            {
                if (ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

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
