using System.Reflection;
using Earshot.Contracts;
using Earshot.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// The at-rest invariant, checked over the built assembly rather than trusted from reading the source: no
// type under Earshot.Widget has a field, constructor parameter, method parameter or return type that is
// one of the device controllers, IKsControl, or the two interop classes that reach CfgMgr32 and the
// Bluetooth service API. The widget watches; it has no path to a connect, an allow, a block or a service
// change.
[TestClass]
public sealed class WidgetAtRestTests
{
    [TestMethod]
    public void NoWidgetTypeReferencesADeviceController()
    {
        Assembly assembly = typeof(Earshot.Widget.WidgetStatusService).Assembly;
        Assert.AreEqual("Earshot", assembly.GetName().Name);

        Type[] forbidden =
        {
            typeof(IConnectionController), typeof(IBlockController), typeof(IAudioProtectionController),
            typeof(IKsControl), typeof(CfgMgr32), typeof(BluetoothApis),
        };

        Type[] widgetTypes = assembly.GetTypes()
            .Where(t => t.Namespace == "Earshot.Widget" || (t.Namespace?.StartsWith("Earshot.Widget.", StringComparison.Ordinal) ?? false))
            .ToArray();
        Assert.IsTrue(widgetTypes.Length >= 10, "Too few Earshot.Widget types were read for a clean result to mean anything: " + widgetTypes.Length);

        var found = new List<string>();
        const BindingFlags AllDeclared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (Type type in widgetTypes)
        {
            foreach (FieldInfo field in type.GetFields(AllDeclared))
            {
                if (References(field.FieldType, forbidden))
                {
                    found.Add(type.FullName + "." + field.Name + " field: " + field.FieldType);
                }
            }

            foreach (ConstructorInfo ctor in type.GetConstructors(AllDeclared))
            {
                foreach (ParameterInfo p in ctor.GetParameters())
                {
                    if (References(p.ParameterType, forbidden))
                    {
                        found.Add(type.FullName + " constructor parameter " + p.Name + ": " + p.ParameterType);
                    }
                }
            }

            foreach (MethodInfo method in type.GetMethods(AllDeclared))
            {
                if (References(method.ReturnType, forbidden))
                {
                    found.Add(type.FullName + "." + method.Name + " return type: " + method.ReturnType);
                }

                foreach (ParameterInfo p in method.GetParameters())
                {
                    if (References(p.ParameterType, forbidden))
                    {
                        found.Add(type.FullName + "." + method.Name + " parameter " + p.Name + ": " + p.ParameterType);
                    }
                }
            }
        }

        Assert.AreEqual(0, found.Count, "A widget type reaches a device controller:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }

    // True when type is one of the forbidden types itself, or reaches one through a generic argument
    // (Task<IConnectionController>, Func<IBlockController>, ...) or an array or ref/out parameter.
    private static bool References(Type type, Type[] forbidden)
    {
        Type effective = type.IsByRef ? type.GetElementType()! : type;

        if (Array.IndexOf(forbidden, effective) >= 0)
        {
            return true;
        }

        if (effective.IsArray && effective.GetElementType() is Type element && References(element, forbidden))
        {
            return true;
        }

        if (effective.IsGenericType)
        {
            foreach (Type argument in effective.GetGenericArguments())
            {
                if (References(argument, forbidden))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
