// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Buffers.Binary;

namespace Content.Meta.Bytecode;

public enum MetaOpcode : ushort
{
    Const = 0x0001, Unit, LoadLocal, StoreLocal, LoadGlobal, StoreGlobal, Pop, Dup, ResetLocal,
    IAdd = 0x0010, ISub, IMul, IDiv, IRem, INeg, IAnd, IOr, IXor, INot, IShl, IShr,
    FAdd = 0x0020, FSub, FMul, FDiv, FRem, FNeg,
    Eq = 0x0030, Ne, Lt, Le, Gt, Ge, Not,
    Br = 0x0040, BrFalse, BrVariant, Call, CallAsync, Return,
    RecordNew = 0x0050, RecordGet, RecordSet, VariantNew, VariantTag, VariantPayload,
    ArrayNew = 0x0060, ArrayLen, ArrayGet, ArraySet, ArrayLiteral,
    CallIntrinsic = 0x0070, LoadLink, HostRead, HostAwait,
    Yield = 0x0080, Safepoint, Trap
}

public readonly record struct MetaInstruction(int Offset, MetaOpcode Opcode, uint A = 0, uint B = 0, uint C = 0)
{
    public int Size => MetaInstructionCodec.GetSize(Opcode);
}

public static class MetaInstructionCodec
{
    public static int GetSize(MetaOpcode op) => op switch
    {
        MetaOpcode.LoadLocal or MetaOpcode.StoreLocal or MetaOpcode.ResetLocal or MetaOpcode.LoadLink or MetaOpcode.Trap => 4,
        MetaOpcode.Const or MetaOpcode.LoadGlobal or MetaOpcode.StoreGlobal or MetaOpcode.Eq or MetaOpcode.Ne or MetaOpcode.Lt or MetaOpcode.Le or MetaOpcode.Gt or MetaOpcode.Ge or MetaOpcode.Br or MetaOpcode.BrFalse or MetaOpcode.Call or MetaOpcode.CallAsync or MetaOpcode.RecordNew or MetaOpcode.VariantTag or MetaOpcode.ArrayNew or MetaOpcode.ArrayLen or MetaOpcode.ArrayGet or MetaOpcode.ArraySet or MetaOpcode.HostRead or MetaOpcode.HostAwait => 6,
        MetaOpcode.RecordGet or MetaOpcode.RecordSet or MetaOpcode.VariantNew or MetaOpcode.VariantPayload or MetaOpcode.CallIntrinsic => 8,
        MetaOpcode.ArrayLiteral => 10,
        MetaOpcode.BrVariant => 12,
        _ when Enum.IsDefined(op) => 2,
        _ => throw new MetaFormatException("Unknown opcode.")
    };

    public static MetaInstruction[] Decode(ReadOnlySpan<byte> code)
    {
        var result = new List<MetaInstruction>();
        for (var pc = 0; pc < code.Length;)
        {
            if (code.Length - pc < 2) throw new MetaFormatException("Truncated opcode.");
            var op = (MetaOpcode) BinaryPrimitives.ReadUInt16LittleEndian(code[pc..]);
            var size = GetSize(op);
            if (code.Length - pc < size) throw new MetaFormatException("Truncated operand.");
            uint a = 0, b = 0, c = 0;
            if (size >= 4)
            {
                if (size == 4 || op == MetaOpcode.CallIntrinsic) a = BinaryPrimitives.ReadUInt16LittleEndian(code[(pc + 2)..]);
                else a = BinaryPrimitives.ReadUInt32LittleEndian(code[(pc + 2)..]);
            }
            if (size >= 8)
            {
                b = op == MetaOpcode.CallIntrinsic
                    ? BinaryPrimitives.ReadUInt32LittleEndian(code[(pc + 4)..])
                    : size == 10 ? BinaryPrimitives.ReadUInt32LittleEndian(code[(pc + 6)..])
                    : BinaryPrimitives.ReadUInt16LittleEndian(code[(pc + 6)..]);
            }
            if (size == 12) c = BinaryPrimitives.ReadUInt32LittleEndian(code[(pc + 8)..]);
            result.Add(new MetaInstruction(pc, op, a, b, c));
            pc += size;
        }
        return result.ToArray();
    }

    public static byte[] Encode(IEnumerable<MetaInstruction> instructions)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var instruction in instructions)
        {
            var size = GetSize(instruction.Opcode);
            writer.Write((ushort) instruction.Opcode);
            if (size == 4 || instruction.Opcode == MetaOpcode.CallIntrinsic) writer.Write(checked((ushort) instruction.A));
            else if (size >= 6) writer.Write(instruction.A);
            if (instruction.Opcode == MetaOpcode.CallIntrinsic) writer.Write(instruction.B);
            else if (size == 10) writer.Write(instruction.B);
            else if (size >= 8) writer.Write(checked((ushort) instruction.B));
            if (size == 12) writer.Write(instruction.C);
        }
        return stream.ToArray();
    }
}
