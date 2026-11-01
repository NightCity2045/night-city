// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Content.Meta.Bytecode;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Bytecode;

[TestFixture]
public sealed class MetaBytecodeTests
{
    internal static MetaManifest Manifest() => new() { PackageName = "test", RootModule = "test", CompilerBuild = "test-1" };
    internal static MetaConstantDef Int(long value) => new() { Kind = MetaConstantKind.Int, TypeId = 3, Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    internal static MetaModule Module(MetaInstruction[] instructions, MetaConstantDef[]? constants = null, int result = 0, int maximum = 0, MetaLocalDef[]? locals = null)
    {
        var code = MetaInstructionCodec.Encode(instructions);
        return new MetaModule
        {
            Manifest = Manifest(), Code = code, Constants = constants ?? [],
            Functions = [new MetaFunctionDef { Id = 0, Name = "test.main", ReturnType = result, CodeLength = code.Length, MaxStack = maximum, Locals = locals ?? [] }]
        };
    }
    internal static VerifiedArtifact Verify(MetaModule module) => MetaVerifier.Verify(MetaBytecodeWriter.Write(module));

    [Test]
    public void ContainerUsesNormativeHeaderDirectoryAndDigest()
    {
        var module = Module([new(0, MetaOpcode.Return)]);
        var bytes = MetaBytecodeWriter.Write(module);
        Assert.Multiple(() =>
        {
            Assert.That(bytes[..4], Is.EqualTo("META"u8.ToArray()));
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)), Is.EqualTo(1));
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)), Is.EqualTo(9));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(68)), Is.EqualTo(64 + 9 * 16));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)), Is.EqualTo(bytes.Length));
        });
        var digest = bytes.AsSpan(24, 32).ToArray(); Array.Clear(bytes, 24, 32);
        Assert.That(digest, Is.EqualTo(SHA256.HashData(bytes)));
        digest.CopyTo(bytes, 24);
        Assert.That(MetaBytecodeWriter.Write(MetaBytecodeLoader.Load(bytes)), Is.EqualTo(bytes));
        Assert.That(MetaVerifier.Verify(bytes).FunctionCount, Is.EqualTo(1));
    }

    [Test]
    public void CanonicalJsonUsesSortedKeysLiteralUnicodeAndLongControlEscapes()
    {
        var bytes = MetaCanonicalJson.Serialize(new MetaFieldDef("Я\n🚀", 3, false));
        Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("{\"name\":\"Я\\u000a🚀\",\"readonly\":false,\"typeId\":3}"));
        Assert.Throws<MetaFormatException>(() => MetaCanonicalJson.Deserialize<MetaFieldDef>("{\"name\":\"a\",\"name\":\"b\",\"readonly\":false,\"typeId\":3}"u8.ToArray()));
        Assert.Throws<MetaFormatException>(() => MetaCanonicalJson.Deserialize<MetaFieldDef>("{\"name\":\"a\",\"readonly\":false}"u8.ToArray()));
    }

    [TestCase(0)] [TestCase(4)] [TestCase(6)] [TestCase(8)] [TestCase(10)] [TestCase(12)] [TestCase(16)] [TestCase(20)] [TestCase(22)] [TestCase(56)] [TestCase(64)] [TestCase(66)] [TestCase(68)] [TestCase(72)] [TestCase(76)]
    public void HeaderAndDirectoryMutationsAreRejected(int offset)
    {
        var bytes = MetaBytecodeWriter.Write(Module([new(0, MetaOpcode.Return)]));
        bytes[offset] ^= 0xff; Rehash(bytes);
        Assert.Throws<MetaFormatException>(() => MetaVerifier.Verify(bytes));
    }

    [Test]
    public void EveryRegistryInstructionRoundTripsAndUnknownOpcodeIsRejected()
    {
        var instructions = Enum.GetValues<MetaOpcode>().Select(op => new MetaInstruction(0, op, 1, 2, 3)).ToArray();
        var bytes = MetaInstructionCodec.Encode(instructions);
        var decoded = MetaInstructionCodec.Decode(bytes);
        Assert.That(decoded.Select(i => i.Opcode), Is.EqualTo(instructions.Select(i => i.Opcode)));
        foreach (var instruction in decoded) Assert.That(instruction.Size, Is.EqualTo(MetaInstructionCodec.GetSize(instruction.Opcode)));
        Assert.Throws<MetaFormatException>(() => MetaInstructionCodec.Decode(new byte[] { 0, 0 }));
        Assert.Throws<MetaFormatException>(() => MetaInstructionCodec.Decode(new byte[] { 1, 0, 0 }));
    }

    [Test]
    public void VerifierRejectsBranchIntoOperandAndUnreachableUnknownOpcode()
    {
        Assert.Throws<MetaFormatException>(() => Verify(Module([new(0, MetaOpcode.Br, 3), new(0, MetaOpcode.Return)])));
        var module = Module([new(0, MetaOpcode.Return), new(0, MetaOpcode.Unit)]);
        var bytes = MetaBytecodeWriter.Write(module);
        var codeOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(64 + 4 * 16 + 4));
        bytes[(int) codeOffset + 2] = 0; bytes[(int) codeOffset + 3] = 0; Rehash(bytes);
        Assert.Throws<MetaFormatException>(() => MetaVerifier.Verify(bytes));
    }

    [Test]
    public void DefiniteAssignmentAndImmutableLifetimeAreIndependentlyChecked()
    {
        var local = new MetaLocalDef(3, false, false);
        Assert.Throws<MetaFormatException>(() => Verify(Module([new(0, MetaOpcode.LoadLocal), new(0, MetaOpcode.Return)], result: 3, maximum: 1, locals: [local])));
        var twice = Module([new(0, MetaOpcode.Const), new(0, MetaOpcode.StoreLocal), new(0, MetaOpcode.Const), new(0, MetaOpcode.StoreLocal), new(0, MetaOpcode.Return)], [Int(7)], maximum: 1, locals: [local]);
        Assert.Throws<MetaFormatException>(() => Verify(twice));
        var reset = Module([new(0, MetaOpcode.Const), new(0, MetaOpcode.StoreLocal), new(0, MetaOpcode.ResetLocal), new(0, MetaOpcode.Const), new(0, MetaOpcode.StoreLocal), new(0, MetaOpcode.Return)], [Int(7)], maximum: 1, locals: [local]);
        Assert.DoesNotThrow(() => Verify(reset));
    }

    [Test]
    public void VerifierRejectsForgedMaximumAndCollectionTypeCycle()
    {
        Assert.Throws<MetaFormatException>(() => Verify(Module([new(0, MetaOpcode.Unit), new(0, MetaOpcode.Pop), new(0, MetaOpcode.Return)], maximum: 2)));
        var module = Module([new(0, MetaOpcode.Return)]) with
        {
            Types = [.. MetaTypeDef.Primitives,
                new MetaTypeDef { Id = 6, Name = "Node", Kind = MetaTypeKind.Struct, Fields = [new MetaFieldDef("children", 7, false)] },
                new MetaTypeDef { Id = 7, Name = "list<Node>", Kind = MetaTypeKind.List, Args = [6] }]
        };
        Assert.Throws<MetaFormatException>(() => Verify(module));
    }

    [Test]
    public void MutatingSourceBytesOrMetadataCannotChangeVerifiedArtifact()
    {
        var module = Module([new(0, MetaOpcode.Return)]);
        var bytes = MetaBytecodeWriter.Write(module); var verified = MetaVerifier.Verify(bytes);
        Array.Fill(bytes, (byte) 0); module.Functions[0] = module.Functions[0] with { ReturnType = 3 };
        var function = verified.GetFunction(0); function.ParameterTypes.AsSpan().Clear();
        Assert.That(verified.GetFunction(0).ReturnType, Is.Zero);
    }

    private static void Rehash(byte[] bytes) { Array.Clear(bytes, 24, 32); SHA256.HashData(bytes).CopyTo(bytes, 24); }
}
