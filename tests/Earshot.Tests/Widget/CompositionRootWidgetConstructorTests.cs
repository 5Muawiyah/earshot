using System.Reflection;
using System.Reflection.Emit;
using Earshot.Composition;
using Earshot.Widget;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Earshot.Tests.Widget;

// A security review found CompositionRoot.BuildWidget calling WidgetStatusService's internal, test-only
// constructor (the one that takes the decode table directly) from production code: a real production binary
// built this way could silently run with a wrong or fixed table. WidgetStatusService's own public constructor
// takes the DecodeProofStore and reads the table and the broadcast observation from it, so nothing composing
// this service can wire up a different table.
//
// Checked over the compiled IL, not by reading the source: which constructor overload the compiler bound the
// call to is a fact about the compiled method, read here by actually decoding its IL and resolving the
// constructor token, not by pattern-matching source text.
[TestClass]
public sealed class CompositionRootWidgetConstructorTests
{
    private static readonly Dictionary<int, OpCode> OpCodesByValue = BuildOpCodeTable();

    [TestMethod]
    public void BuildWidgetConstructsWidgetStatusServiceThroughItsPublicConstructorOnly()
    {
        MethodInfo buildWidget = typeof(CompositionRoot).GetMethod("BuildWidget", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsNotNull(buildWidget, "Sanity: CompositionRoot.BuildWidget must exist for this test to mean anything.");

        List<ConstructorInfo> calls = WidgetStatusServiceConstructorsCalledBy(buildWidget);

        Assert.HasCount(1, calls, "BuildWidget must construct WidgetStatusService exactly once.");
        Assert.IsTrue(calls[0].IsPublic,
            "BuildWidget must call WidgetStatusService's public constructor, not the internal, test-only " +
            "overload that takes the decode table directly.");
        ParameterInfo[] parameters = calls[0].GetParameters();
        Assert.HasCount(9, parameters,
            "The public constructor takes exactly 9 parameters, the last Windows' figure source; a call with more " +
            "is the internal overload.");
        Assert.AreEqual(typeof(IPairedModelSource), parameters[^2].ParameterType,
            "The candidates come from the paired AirPods' model.");
        Assert.AreEqual(typeof(IHandsFreeBatterySource), parameters[^1].ParameterType,
            "Windows' own figure comes from its own source.");
    }

    // Mirrors WidgetAtRestTests' own IL walker (a full operand-size table is needed to skip variable-length
    // instructions correctly; there is no public API that already does this), scoped down to just newobj.
    private static List<ConstructorInfo> WidgetStatusServiceConstructorsCalledBy(MethodBase method)
    {
        var found = new List<ConstructorInfo>();
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        Module module = method.Module;
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
                    if (opcode == OpCodes.Newobj &&
                        module.ResolveMember(BitConverter.ToInt32(il, i)) is ConstructorInfo ctor &&
                        ctor.DeclaringType == typeof(WidgetStatusService))
                    {
                        found.Add(ctor);
                    }

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
                    throw new NotSupportedException("Unhandled IL operand type " + opcode.OperandType + " in CompositionRoot.BuildWidget.");
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
                // OpCode.Value is a signed Int16; a two-byte (0xFE-prefixed) opcode's value is negative when
                // sign-extended, but the decoder above builds a positive 0xFE00 | secondByte key, so the low
                // 16 bits are masked back to their unsigned form here.
                table[opcode.Value & 0xFFFF] = opcode;
            }
        }

        return table;
    }
}
