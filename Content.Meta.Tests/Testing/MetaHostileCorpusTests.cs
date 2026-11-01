// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Persistence;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

/// <summary>Reproducible bounded mutation campaigns. Unexpected failures preserve a reduced input and policy identity.</summary>
[TestFixture]
public sealed class MetaHostileCorpusTests
{
    private const string Program = "module fuzz.program; import meta.system; fn main() -> void { let answerValue = (7 + 3) * 2; system.log(\"ready\"); }";
    private static readonly ImmutableDictionary<string, MuiField> Fields = new Dictionary<string, MuiField>
    {
        ["name"] = new(MuiValueKind.String, MaxLength: 64),
        ["enabled"] = new(MuiValueKind.Boolean),
    }.ToImmutableDictionary();
    private static readonly MuiViewContract View = new("view", "ui/main.mui", Fields, []);
    private static readonly MuiAppContract Contract = new("1.0",
        new Dictionary<string, MuiViewContract> { ["main"] = View }.ToImmutableDictionary(),
        ImmutableDictionary<string, MuiActionContract>.Empty, ImmutableHashSet<string>.Empty);

    [TestCase(314159)]
    [TestCase(271828)]
    public void MutatedSourcesAlwaysProduceBoundedDiagnosticsOrVerifiableCode(int seed)
    {
        var random = new Random(seed);
        for (var index = 0; index < 256; index++)
        {
            var input = MutateText(Program, random);
            CheckText("source", seed, index, input, text =>
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var result = new MetaCompiler().Compile(new([new("fuzz.meta", text)], "fuzz.program"), cancellation.Token);
                if (result.Diagnostics.Count > 100 || JsonSerializer.SerializeToUtf8Bytes(result.Diagnostics).Length > 64 * 1024)
                    throw new InvalidOperationException("Compiler diagnostic budget exceeded.");
                if (result.Success) MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!));
            });
        }
    }

    [TestCase(0x4d455441)]
    [TestCase(0x564d)]
    public void ResignedBytecodeMutationsCannotEscapeTheVerifierErrorBoundary(int seed)
    {
        var compiled = new MetaCompiler().Compile(new([new("fuzz.meta", Program)], "fuzz.program"));
        Assert.That(compiled.Success, Is.True);
        var catalog = MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!);
        var random = new Random(seed);
        void Validate(byte[] bytes)
        {
            try { MetaVerifier.Verify(bytes, catalog); }
            catch (MetaFormatException) { }
        }
        for (var index = 0; index < 512; index++)
        {
            var bytes = compiled.Artifact!.ToArray();
            for (var mutation = 0; mutation < 1 + index % 4; mutation++)
                bytes[random.Next(bytes.Length)] ^= (byte) (1 << random.Next(8));
            if (index % 7 == 0) Array.Resize(ref bytes, random.Next(bytes.Length));
            if (bytes.Length >= 64 && index % 2 == 0)
            {
                Array.Clear(bytes, 24, 32);
                SHA256.HashData(bytes).CopyTo(bytes, 24);
            }
            var failure = Failure(() => Validate(bytes));
            if (failure == null) continue;
            var reduced = Reduce(bytes, candidate => Failure(() => Validate(candidate)) != null);
            Save("bytecode", seed, index, bytes, reduced, failure);
            Assert.Fail($"Bytecode seed {seed}, case {index}: {failure}");
        }
    }

    [TestCase(0x4d5549)]
    [TestCase(0x535441)]
    public void MarkupAndStyleMutationsDoNotThrowOrCreatePartialState(int seed)
    {
        string[] corpus = ["<Page id='root'><Text id='text' text='{bind name}'/></Page>",
            "{\"version\":\"1.0\",\"rules\":[]}", "{\"name\":\"Привет 👩‍💻\",\"enabled\":true}",
            "<!DOCTYPE Page [<!ENTITY x SYSTEM 'file:///denied'>]><Page id='root'>&x;</Page>",
            "{\"name\":\"\\ud800\",\"enabled\":true}"];
        var random = new Random(seed);
        for (var index = 0; index < 384; index++)
        {
            var input = MutateText(corpus[index % corpus.Length], random);
            CheckText("mui", seed, index, input, text =>
            {
                MuiParser.TryParse(text, View, Contract, out _, out _);
                MuiStyleSheet.TryParse(text, Contract, out _, out _);
                MuiContractParser.TryParse(text, out _, out _);
                using var session = new MuiSessionState(Guid.NewGuid(), Fields);
                session.TryApplySnapshot(0, "{\"name\":\"original\",\"enabled\":true}");
                var result = session.TryApplyPatch(0, 1, text);
                if (result != MuiUpdateResult.Applied && (session.Revision != 0 || session.Read("name")!.GetValue<string>() != "original"))
                    throw new InvalidOperationException("Rejected patch mutated its projection.");
            });
        }
    }

    [TestCase(90210)]
    public void RandomProtocolSequencesRespectRevisionAndClosedSessionBoundaries(int seed)
    {
        var random = new Random(seed);
        using var session = new MuiSessionState(Guid.NewGuid(), Fields);
        session.TryApplySnapshot(0, "{\"name\":\"initial\",\"enabled\":true}");
        long revision = 0;
        var name = "initial";
        for (var index = 0; index < 2048; index++)
        {
            var before = session.Read("name")!.GetValue<string>();
            var next = "value-" + random.Next(1000);
            var operation = random.Next(5);
            var result = operation switch
            {
                0 => session.TryApplyPatch(revision, revision + 1, JsonSerializer.Serialize(new { set = new { name = next } })),
                1 => session.TryApplyPatch(revision + 7, revision + 8, "{\"set\":{\"name\":\"forged\"}}"),
                2 => session.TryApplyPatch(revision, revision + 1, "{\"set\":{\"name\":12}}"),
                3 => session.TryApplySnapshot(revision, "{\"name\":\"forged\",\"enabled\":true}"),
                _ => session.TryApplyPatch(revision, revision + 1, "{\"remove\":[\"enabled\"]}"),
            };
            if (result == MuiUpdateResult.Applied) { revision++; name = next; }
            else Assert.That(session.Read("name")!.GetValue<string>(), Is.EqualTo(before), $"seed {seed}, step {index}");
            Assert.That(session.Revision, Is.EqualTo(revision), $"seed {seed}, step {index}");
            Assert.That(session.Read("name")!.GetValue<string>(), Is.EqualTo(name));
        }
        session.Dispose();
        Assert.That(session.TryApplySnapshot(revision + 1, "{\"name\":\"reopen\",\"enabled\":true}"), Is.EqualTo(MuiUpdateResult.Closed));
        Assert.That(session.TryApplyPatch(revision, revision + 1, "{}"), Is.EqualTo(MuiUpdateResult.Closed));
        Assert.That(session.Read("name"), Is.Null);
    }

    [TestCase(0x53544154)]
    public void StateMutationSequencesNeverAlterInputOnFailure(int seed)
    {
        var random = new Random(seed);
        string[] paths = ["settings/a.txt", "other.txt", "../escape", "C:/host", "//host/share", "", "a/../b", "é.txt"];
        for (var index = 0; index < 512; index++)
        {
            MetaStateTransformFile[] files = [new("settings/a.txt", [1, 2, 3], 1), new("other.txt", [4, 5], 2)];
            var before = JsonSerializer.Serialize(files);
            var steps = Enumerable.Range(0, random.Next(1, 12)).Select(_ => new MetaStateTransformStep(
                (MetaStateTransformKind) random.Next(5), paths[random.Next(paths.Length)], paths[random.Next(paths.Length)],
                random.Next(4) == 0 ? "\ud800" : "")).ToArray();
            var accepted = MetaStateTransformer.TryTransform(files, steps, random.Next(16), out var result);
            Assert.That(JsonSerializer.Serialize(files), Is.EqualTo(before), $"seed {seed}, step {index}");
            if (!accepted) Assert.That(result, Is.Empty);
            else
            {
                Assert.That(result.Select(file => file.Path).Distinct().Count(), Is.EqualTo(result.Length));
                Assert.That(result.All(file => Content.Meta.Hosting.MetaPaths.IsValid(file.Path)), Is.True);
            }
        }
    }

    private static string MutateText(string source, Random random)
    {
        const string alphabet = "<>/'\"{}[]=!&;:abc012 \n\0Ж\ud800\udc00";
        var mutations = random.Next(1, 9);
        for (var mutation = 0; mutation < mutations; mutation++)
        {
            var position = random.Next(source.Length + 1);
            if (source.Length > 0 && random.Next(3) == 0)
            {
                position = Math.Min(position, source.Length - 1);
                source = source.Remove(position, Math.Min(random.Next(1, 8), source.Length - position));
            }
            else source = source.Insert(position, alphabet[random.Next(alphabet.Length)].ToString());
        }
        return source;
    }

    private static void CheckText(string kind, int seed, int index, string input, Action<string> check)
    {
        var failure = Failure(() => check(input));
        if (failure == null) return;
        var bytes = RawUtf16(input);
        // UTF-16 preserves hostile lone surrogate code units in the reproduction artifact.
        var reduced = Reduce(bytes, candidate => candidate.Length % 2 == 0 &&
            Failure(() => check(ReadRawUtf16(candidate)))?.GetType() == failure.GetType());
        Save(kind, seed, index, bytes, reduced, failure);
        Assert.Fail($"{kind} seed {seed}, case {index}: {failure}");
    }

    private static Exception? Failure(Action action)
    {
        try { action(); return null; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { return exception; }
    }

    private static byte[] RawUtf16(string text)
    {
        var bytes = new byte[text.Length * 2];
        for (var index = 0; index < text.Length; index++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2), text[index]);
        return bytes;
    }

    private static string ReadRawUtf16(byte[] bytes)
    {
        var text = new char[bytes.Length / 2];
        for (var index = 0; index < text.Length; index++) text[index] = (char) BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index * 2));
        return new string(text);
    }

    private static byte[] Reduce(byte[] input, Func<byte[], bool> fails)
    {
        var result = input;
        var attempts = 0;
        for (var chunk = Math.Max(1, result.Length / 2); chunk > 0 && attempts < 48; chunk /= 2)
        {
            for (var offset = 0; offset + chunk <= result.Length && attempts++ < 48; offset += chunk)
            {
                var candidate = result[..offset].Concat(result[(offset + chunk)..]).ToArray();
                if (!fails(candidate)) continue;
                result = candidate;
                break;
            }
        }
        return result;
    }

    private static void Save(string kind, int seed, int index, byte[] input, byte[] reduced, Exception failure)
    {
        var directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "meta-counterexamples");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"hostile-{kind}-{seed}-{index}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            Kind = kind, Seed = seed, Case = index, CompilerBuild = MetaCompiler.BuildId,
            CoreSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(MetaCompiler).Assembly.Location))),
            CatalogHash = MetaApiCatalog.Default.Hash, VerifierPolicy = MetaVerifier.PolicyVersion,
            GasPolicy = new MetaManifest().GasPolicyVersion, RuntimeAbi = new MetaManifest().RuntimeAbi,
            InputBase64 = Convert.ToBase64String(input), MinimizedBase64 = Convert.ToBase64String(reduced), Failure = failure.ToString(),
        }, new JsonSerializerOptions { WriteIndented = true }));
        TestContext.AddTestAttachment(path, "Reduced hostile META input with reproducible policy/build identity");
    }
}
