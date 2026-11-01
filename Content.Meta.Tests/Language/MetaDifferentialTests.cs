// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

/// <summary>A bounded AST oracle independent of parser, lowering, bytecode and the VM arithmetic implementation.</summary>
[TestFixture]
public sealed class MetaDifferentialTests
{
    private static readonly string[] Operators = ["+", "-", "*", "/", "%", "&", "|", "^", "<<", ">>"];
    private static readonly long[] Edges = [long.MinValue, long.MaxValue, -1, 0, 1, 63, 64, -64];

    [TestCase(1u)]
    [TestCase(90210u)]
    [TestCase(0x5a5a5a5au)]
    public void SeededExpressionsAgreeOnValuesFaultsAndEvaluationOrderAcrossSlices(uint seed)
    {
        var random = new Generator(seed);
        for (var index = 0; index < 128; index++)
        {
            var expression = Generate(random, 1 + random.Next(5));
            var failure = Check(expression);
            if (failure == null) continue;
            var reduced = expression;
            for (var step = 0; step < 64; step++)
            {
                var candidate = reduced.Children.Concat(new Expression[] { new Literal(0), new Literal(1) })
                    .FirstOrDefault(value => value.Weight < reduced.Weight && Check(value) != null);
                if (candidate == null) break;
                reduced = candidate;
            }
            var directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "meta-counterexamples");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"differential-{seed}-{index}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                Seed = seed, Case = index, CompilerBuild = MetaCompiler.BuildId,
                CoreSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(MetaCompiler).Assembly.Location))),
                CatalogHash = MetaApiCatalog.Default.Hash, VerifierPolicy = MetaVerifier.PolicyVersion,
                GasPolicy = new MetaManifest().GasPolicyVersion, RuntimeAbi = new MetaManifest().RuntimeAbi, Source = Program(expression),
                MinimizedSource = Program(reduced), Failure = failure,
            }, new JsonSerializerOptions { WriteIndented = true }));
            TestContext.AddTestAttachment(path, "Reproducible, reduced META differential counterexample");
            Assert.Fail($"Seed {seed}, case {index}: {failure}. Reduced source: {Program(reduced)}");
        }
    }

    private static string? Check(Expression expression)
    {
        var expected = new List<string>();
        var numericFault = false;
        try { expected.Add("answer:" + expression.Evaluate(expected).ToString(CultureInfo.InvariantCulture)); }
        catch (ArithmeticException) { numericFault = true; }
        var source = Program(expression);
        MetaCompilationResult compiled;
        try { compiled = new MetaCompiler().Compile(new([new("oracle.meta", source)], "oracle.program")); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { return "Compiler exception: " + exception; }
        if (!compiled.Success) return "Compiler rejected generated valid source: " + string.Join(';', compiled.Diagnostics.Select(value =>
            value.Code + ":" + string.Join(',', value.Arguments)));
        VerifiedArtifact artifact;
        try { artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!)); }
        catch (Exception exception) { return "Compiler output was rejected: " + exception.Message; }
        foreach (var gas in new[] { 1, 7, 1024 })
        {
            var host = new RecordingHost();
            var process = new MetaProcess(artifact, host);
            if (!process.TryStartMain()) return "Admitted oracle program could not start.";
            var slices = 0;
            while ((process.State is MetaProcessState.Runnable or MetaProcessState.Collecting) && slices++ < 100000)
                process.RunSlice(gas, 1000);
            if (slices >= 100000) return $"No bounded completion at gas {gas}.";
            if (numericFault ? process.Fault?.Code != MetaVmFaultCode.NumericFault : process.Fault != null)
                return $"Fault mismatch at gas {gas}: oracleNumeric={numericFault}, VM={process.Fault}.";
            if (!host.Logs.SequenceEqual(expected, StringComparer.Ordinal))
                return $"Trace/value mismatch at gas {gas}: expected {JsonSerializer.Serialize(expected)}, actual {JsonSerializer.Serialize(host.Logs)}.";
        }
        return null;
    }

    private static string Program(Expression expression) => $$"""
        module oracle.program;
        import meta.system; import meta.text;
        fn mark(value:int)->int {system.log(text.fromInt(value));return value;}
        fn main()->void {let answerValue={{expression.Source}};system.log("answer:"+text.fromInt(answerValue));}
        """;

    private static Expression Generate(Generator random, int depth)
    {
        if (depth == 0 || random.Next(3) == 0)
            return new Literal(random.Next(4) == 0 ? Edges[random.Next(Edges.Length)] : random.Next(201) - 100);
        if (random.Next(5) == 0) return new Unary(random.Next(2) == 0 ? "-" : "~", Generate(random, depth - 1));
        return new Binary(Operators[random.Next(Operators.Length)], Generate(random, depth - 1), Generate(random, depth - 1));
    }

    private abstract record Expression
    {
        public abstract string Source { get; }
        public abstract long Evaluate(List<string> trace);
        public virtual IEnumerable<Expression> Children => [];
        public virtual int Weight => 1;
    }

    private sealed record Literal(long Value) : Expression
    {
        public override string Source => "mark(" + (Value == long.MinValue ? "(-9223372036854775807-1)" : Value.ToString(CultureInfo.InvariantCulture)) + ")";
        public override long Evaluate(List<string> trace) { trace.Add(Value.ToString(CultureInfo.InvariantCulture)); return Value; }
    }

    private sealed record Unary(string Operator, Expression Operand) : Expression
    {
        public override string Source => "(" + Operator + "(" + Operand.Source + "))";
        public override IEnumerable<Expression> Children => [Operand];
        public override int Weight => 1 + Operand.Weight;
        public override long Evaluate(List<string> trace)
        {
            var operand = new BigInteger(Operand.Evaluate(trace));
            return Checked(Operator == "-" ? -operand : ~operand);
        }
    }

    private sealed record Binary(string Operator, Expression Left, Expression Right) : Expression
    {
        public override string Source => "(" + Left.Source + " " + Operator + " " + Right.Source + ")";
        public override IEnumerable<Expression> Children => [Left, Right];
        public override int Weight => 1 + Left.Weight + Right.Weight;
        public override long Evaluate(List<string> trace)
        {
            var left = new BigInteger(Left.Evaluate(trace));
            var right = new BigInteger(Right.Evaluate(trace));
            if (Operator is "<<" or ">>" && (right < 0 || right > 63)) throw new ArithmeticException("Shift range.");
            var value = Operator switch
            {
                "+" => left + right, "-" => left - right, "*" => left * right, "/" => left / right, "%" => left % right,
                "&" => left & right, "|" => left | right, "^" => left ^ right, ">>" => left >> (int)right,
                "<<" => Wrap(left << (int)right), _ => throw new InvalidOperationException(),
            };
            return Checked(value);
        }
    }

    private static long Checked(BigInteger value) => value < long.MinValue || value > long.MaxValue
        ? throw new ArithmeticException("Integer range.") : (long)value;
    private static BigInteger Wrap(BigInteger value)
    {
        var modulus = BigInteger.One << 64;
        var bits = value & (modulus - 1);
        return bits >= (BigInteger.One << 63) ? bits - modulus : bits;
    }

    private sealed class Generator(uint state)
    {
        public int Next(int bound)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (int)(state % (uint)bound);
        }
    }

    private sealed class RecordingHost : IMetaHost
    {
        public List<string> Logs { get; } = [];
        public MetaValue Read(MetaProcess process, MetaHostRequest request) => throw new InvalidOperationException("Unexpected oracle host read.");
        public MetaHostResult BeginAwait(MetaProcess process, MetaHostRequest request) => throw new InvalidOperationException("Unexpected oracle async host call.");
        public void Log(MetaProcess process, string text) => Logs.Add(text);
        public void Cancel(MetaProcess process, long operationId) { }
    }
}
