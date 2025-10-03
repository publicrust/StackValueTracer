using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace StackValueTracer;

internal static class IlDisassembler
{
    private const int DefaultInstructionLimit = 12;

    private static readonly OpCode[] SingleByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] DoubleByteOpCodes = new OpCode[0x100];

    static IlDisassembler()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opCode)
            {
                var value = (ushort)opCode.Value;
                if (value < 0x100)
                {
                    SingleByteOpCodes[value] = opCode;
                }
                else
                {
                    DoubleByteOpCodes[value & 0xFF] = opCode;
                }
            }
        }
    }

    public static IReadOnlyList<SnippetLine> Disassemble(MethodBase method)
    {
        try
        {
            var body = method.GetMethodBody();
            if (body is null)
            {
                return Array.Empty<SnippetLine>();
            }

            var il = body.GetILAsByteArray();
            if (il is null || il.Length == 0)
            {
                return Array.Empty<SnippetLine>();
            }

            var instructions = new List<SnippetLine>();
            var offset = 0;
            var module = method.Module;
            var primarySet = false;

            while (offset < il.Length && instructions.Count < DefaultInstructionLimit)
            {
                var startOffset = offset;
                var opInfo = ReadOpCode(il, ref offset);
                var operandText = opInfo.Known ? ReadOperandText(opInfo.OpCode, il, ref offset, module) : string.Empty;
                var opName = opInfo.Known ? opInfo.OpCode.Name : $"op_{opInfo.RawValue:X4}";
                var builder = new StringBuilder();
                builder.Append("IL_").Append(startOffset.ToString("X4", CultureInfo.InvariantCulture)).Append(':').Append(' ').Append(opName);
                if (!string.IsNullOrEmpty(operandText))
                {
                    builder.Append(' ').Append(operandText);
                }

                instructions.Add(new SnippetLine(startOffset, builder.ToString(), !primarySet));
                primarySet = true;
            }

            return instructions;
        }
        catch
        {
            return Array.Empty<SnippetLine>();
        }
    }

    private static OpCodeInfo ReadOpCode(IReadOnlyList<byte> il, ref int offset)
    {
        var first = il[offset++];
        if (first == 0xFE)
        {
            var second = il[offset++];
            var op = DoubleByteOpCodes[second];
            return new OpCodeInfo(op, op.Name is not null, (ushort)(0xFE00 | second));
        }

        var single = SingleByteOpCodes[first];
        return new OpCodeInfo(single, single.Name is not null, first);
    }

    private static string ReadOperandText(OpCode opCode, byte[] il, ref int offset, Module module)
    {
        try
        {
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    return string.Empty;
                case OperandType.ShortInlineBrTarget:
                {
                    sbyte delta = unchecked((sbyte)il[offset++]);
                    var target = offset + delta;
                    return $"IL_{target:X4}";
                }
                case OperandType.InlineBrTarget:
                {
                    var delta = BitConverter.ToInt32(il, offset);
                    offset += sizeof(int);
                    var target = offset + delta;
                    return $"IL_{target:X4}";
                }
                case OperandType.ShortInlineI:
                    return ((sbyte)il[offset++]).ToString(CultureInfo.InvariantCulture);
                case OperandType.InlineI:
                {
                    var value = BitConverter.ToInt32(il, offset);
                    offset += sizeof(int);
                    return value.ToString(CultureInfo.InvariantCulture);
                }
                case OperandType.InlineI8:
                {
                    var value = BitConverter.ToInt64(il, offset);
                    offset += sizeof(long);
                    return value.ToString(CultureInfo.InvariantCulture);
                }
                case OperandType.ShortInlineR:
                {
                    var value = BitConverter.ToSingle(il, offset);
                    offset += sizeof(float);
                    return value.ToString(CultureInfo.InvariantCulture);
                }
                case OperandType.InlineR:
                {
                    var value = BitConverter.ToDouble(il, offset);
                    offset += sizeof(double);
                    return value.ToString(CultureInfo.InvariantCulture);
                }
                case OperandType.ShortInlineVar:
                    return FormatVariable(il[offset++]);
                case OperandType.InlineVar:
                {
                    var index = BitConverter.ToUInt16(il, offset);
                    offset += sizeof(ushort);
                    return FormatVariable(index);
                }
                case OperandType.InlineString:
                {
                    var token = BitConverter.ToInt32(il, offset);
                    offset += sizeof(int);
                    var value = module.ResolveString(token);
                    return $"\"{value}\"";
                }
                case OperandType.InlineMethod:
                {
                    var token = BitConverter.ToInt32(il, offset);
                    offset += sizeof(int);
                    var methodBase = ResolveSafe(() => module.ResolveMethod(token));
                    return methodBase is null ? $"0x{token:X8}" : FormatMember(methodBase);
                }
                case OperandType.InlineField:
                {
                    var token = BitConverter.ToInt32(il, offset);
                    offset += sizeof(int);
                    var field = ResolveSafe(() => module.ResolveField(token));
                    return field is null ? $"0x{token:X8}" : FormatMember(field);
                }
                case OperandType.InlineType:
                {
                    var token = BitConverter.ToInt32(il, offset);
                    offset += sizeof(int);
                    var type = ResolveSafe(() => module.ResolveType(token));
                    return type is null ? $"0x{token:X8}" : FormatType(type);
                }
                case OperandType.InlineTok:
                {
                    var token = BitConverter.ToInt32(il, offset);
                    offset += sizeof(int);
                    var member = ResolveSafe(() => module.ResolveMember(token));
                    return member is null ? $"0x{token:X8}" : FormatMember(member);
                }
                case OperandType.InlineSig:
                {
                    var token = BitConverter.ToInt32(il, offset);
                    offset += sizeof(int);
                    var signature = ResolveSafe(() => module.ResolveSignature(token));
                    return signature is null ? $"0x{token:X8}" : $"sig[{signature.Length}]";
                }
                case OperandType.InlineSwitch:
                {
                    var count = BitConverter.ToInt32(il, offset);
                    offset += sizeof(int);
                    var targets = new string[count];
                    for (var i = 0; i < count; i++)
                    {
                        var delta = BitConverter.ToInt32(il, offset);
                        offset += sizeof(int);
                        var target = offset + delta;
                        targets[i] = $"IL_{target:X4}";
                    }

                    return $"[{string.Join(", ", targets)}]";
                }
                default:
                    return string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FormatVariable(int index)
    {
        return index switch
        {
            < 0 => $"var?{index}",
            _ => $"var{index}"
        };
    }

    private static string FormatMember(MemberInfo member)
    {
        return member switch
        {
            MethodBase method => $"{FormatType(method.DeclaringType)}::{method.Name}",
            FieldInfo field => $"{FormatType(field.DeclaringType)}::{field.Name}",
            Type type => FormatType(type),
            _ => member.Name
        };
    }

    private static string FormatType(Type? type)
    {
        if (type is null)
        {
            return "<unknown>";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(type.Namespace))
        {
            builder.Append(type.Namespace).Append('.');
        }

        var genericName = type.Name;
        var tickIndex = genericName.IndexOf('`');
        if (tickIndex > 0)
        {
            genericName = genericName[..tickIndex];
        }

        builder.Append(genericName).Append('[');

        var args = type.GetGenericArguments();
        for (var i = 0; i < args.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',').Append(' ');
            }

            builder.Append(FormatType(args[i]));
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static T? ResolveSafe<T>(Func<T> resolver)
    {
        try
        {
            return resolver();
        }
        catch
        {
            return default;
        }
    }

    private readonly struct OpCodeInfo
    {
        public OpCodeInfo(OpCode opCode, bool known, ushort rawValue)
        {
            OpCode = opCode;
            Known = known;
            RawValue = rawValue;
        }

        public OpCode OpCode { get; }
        public bool Known { get; }
        public ushort RawValue { get; }
    }
}
