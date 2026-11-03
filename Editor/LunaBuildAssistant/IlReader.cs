using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Supercent.Util.Editor
{
    // Minimal IL walker over MethodBody.GetILAsByteArray: yields each instruction and resolves
    // member/type tokens in the method's generic context. Enough for call graphs and simple lint rules.
    public static class IlReader
    {
        public struct Instruction
        {
            public OpCode OpCode;
            public int Token;           // Raw operand for token-carrying opcodes, otherwise 0.
            public object Operand;      // Resolved MemberInfo/Type/string, the float of ldc.r4, or null when unresolvable.
        }

        static readonly OpCode[] OneByte = new OpCode[0x100];
        static readonly OpCode[] TwoByte = new OpCode[0x100];

        static IlReader()
        {
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var op = (OpCode)f.GetValue(null);
                ushort value = unchecked((ushort)op.Value);
                if (value < 0x100)
                    OneByte[value] = op;
                else if ((value & 0xff00) == 0xfe00)
                    TwoByte[value & 0xff] = op;
            }
        }

        public static IEnumerable<Instruction> Read(MethodBase method)
        {
            byte[] il;
            try { il = method.GetMethodBody()?.GetILAsByteArray(); }
            catch (Exception) { il = null; }
            if (il == null)
                yield break;

            Module module = method.Module;
            Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

            int pos = 0;
            while (pos < il.Length)
            {
                OpCode op = il[pos] == 0xfe && pos + 1 < il.Length ? TwoByte[il[++pos]] : OneByte[il[pos]];
                pos++;
                var ins = new Instruction { OpCode = op };
                switch (op.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        pos += 1;
                        break;
                    case OperandType.InlineVar:
                        pos += 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        pos += 8;
                        break;
                    case OperandType.InlineSwitch:
                        pos += 4 + 4 * BitConverter.ToInt32(il, pos);
                        break;
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                    case OperandType.InlineString:
                        ins.Token = BitConverter.ToInt32(il, pos);
                        ins.Operand = Resolve(module, op.OperandType, ins.Token, typeArgs, methodArgs);
                        pos += 4;
                        break;
                    case OperandType.ShortInlineR:
                        ins.Operand = BitConverter.ToSingle(il, pos);     // ldc.r4 constant.
                        pos += 4;
                        break;
                    default:                // InlineI, InlineBrTarget, InlineSig
                        pos += 4;
                        break;
                }
                yield return ins;
            }
        }

        static object Resolve(Module module, OperandType kind, int token, Type[] typeArgs, Type[] methodArgs)
        {
            try
            {
                switch (kind)
                {
                    case OperandType.InlineMethod: return module.ResolveMethod(token, typeArgs, methodArgs);
                    case OperandType.InlineField: return module.ResolveField(token, typeArgs, methodArgs);
                    case OperandType.InlineType: return module.ResolveType(token, typeArgs, methodArgs);
                    case OperandType.InlineString: return module.ResolveString(token);
                    default: return module.ResolveMember(token, typeArgs, methodArgs);
                }
            }
            catch (Exception)
            {
                return null;        // Member from an assembly that is not loaded, or a malformed token.
            }
        }
    }
}
