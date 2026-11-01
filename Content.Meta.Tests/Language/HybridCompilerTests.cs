// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class HybridCompilerTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Content.Meta.Tests", "Fixtures", "Meta", "Examples"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Cannot locate META source fixtures.");
        }
    }
    private static MetaCompilationResult Compile(string body, string module = "test.program") => new MetaCompiler().Compile(new([new("test.meta", body)], module));

    [TestCase("hello")]
    [TestCase("math")]
    [TestCase("arraystats")]
    [TestCase("readings")]
    [TestCase("files")]
    [TestCase("pong")]
    [TestCase("doorpanel")]
    [TestCase("motion")]
    [TestCase("sensorfilter")]
    [TestCase("diagnostics")]
    [TestCase("overheat")]
    [TestCase("patrol")]
    [TestCase("conveyor")]
    [TestCase("optics")]
    [TestCase("netprobe")]
    public void NormativeProgramsCompileAndVerify(string name)
    {
        var path = Path.Combine(Root, "Content.Meta.Tests", "Fixtures", "Meta", "Examples", "examples." + name + ".meta");
        var result = Compile(File.ReadAllText(path), "examples." + name);
        Assert.That(result.Diagnostics.Where(d => d.Severity == MetaDiagnosticSeverity.Error), Is.Empty, string.Join('\n', result.Diagnostics.Select(d => d.Code + ":" + string.Join(',', d.Arguments))));
        Assert.That(result.Success, Is.True);
        var module = MetaBytecodeLoader.Load(result.Artifact!);
        var verified = MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(module));
        Assert.That(verified.Digest, Is.EqualTo(result.ArtifactHash));
        var again = Compile(File.ReadAllText(path), "examples." + name);
        Assert.That(again.Artifact, Is.EqualTo(result.Artifact));
    }

    [Test]
    public void LibraryImportAndMutualRecursionCompile()
    {
        var result = new MetaCompiler().Compile(new([
            new("root.meta", "module test.root; import maker.filters; import meta.system; import meta.text; fn main() -> void { system.log(text.fromFloat(filters.smooth(90.0,110.0,0.25))); }"),
            new("library.meta", "module maker.filters; pub fn smooth(previous:float,current:float,alpha:float)->float{return previous*(1.0-alpha)+current*alpha;}")
        ], "test.root"));
        Assert.That(result.Success, Is.True, string.Join(',', result.Diagnostics.Select(d => d.Code)));
        Assert.DoesNotThrow(() => MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!)));
        var recursive = Compile("module test.program; fn a(n:int)->int { if(n == 0){ return 0; } return b(n-1); } fn b(n:int)->int { return a(n); } fn main()->void { let value = a(4); }");
        Assert.That(recursive.Success, Is.True);
    }

    [TestCase("var t:float=110;", "E201")]
    [TestCase("fn main()->void { if(1){} }", "E202")]
    [TestCase("import meta.doors; link d:Door = entityId;", "E103")]
    [TestCase("import meta.doors; link d:Door=\"x\"; fn main()->void { let x=await d.setOpen(true); }", "E231")]
    [TestCase("import meta.doors; link d:Door=\"x\"; async fn main()->void { d.setOpen(true); }", "E232")]
    [TestCase("import meta.doors; link d:Door=\"x\"; fn main()->void { let x=d.GetType(); }", "E225")]
    [TestCase("fn main()->void { let x=none; }", "E203")]
    [TestCase("fn main()->void { let x:option<int>=some(1); match(x){some(y)=>{}} }", "E241")]
    [TestCase("import meta.doors; fn main()->void { let x=new Door(id:17); }", "E226")]
    [TestCase("fn f()->int { if(true){return 1;} }", "E214")]
    [TestCase("const a:array<int>=[1,2];", "E216")]
    [TestCase("fn main()->void { let x:int=1; x=2; }", "E210")]
    [TestCase("fn main()->void { let x=1; {let x=2;} }", "E211")]
    [TestCase("struct Node { children:list<Node>; }", "E219")]
    public void NormativeNegativeProgramsHaveStableDiagnostics(string body, string diagnostic)
    {
        var result = Compile("module test.program; " + body);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Diagnostics.Select(d => d.Code), Does.Contain(diagnostic));
    }

    [Test]
    public void NestedGenericsAndShiftHaveDifferentContexts()
    {
        var result = Compile("module test.program; fn main()->void { let m=new map<string,list<option<int>>>(); var x:int=8; x=x>>1; let l=new list<int>(); l.push(x); for(let n in l){ yield; } }");
        Assert.That(result.Success, Is.True, string.Join(',', result.Diagnostics.Select(d => d.Code)));
        Assert.DoesNotThrow(() => MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!)));
    }

    [Test]
    public void UnicodeSpansUseNormalizedUtf8ScalarBoundaries()
    {
        var source = new SourceText("test.meta", "\uFEFFmodule test.program;\r\nfn main()->void { let x=\"Київ 🚀 e\u0301\"; }\r\n");
        var parsed = MetaParser.Parse(source);
        Assert.That(parsed.Diagnostics, Is.Empty);
        Assert.That(source.Text, Does.Not.Contain('\r'));
        var token = parsed.Tokens.Single(t => t.Kind == MetaTokenKind.String);
        Assert.That(source.IsScalarBoundary(token.Span.StartUtf8Byte), Is.True);
        Assert.That(source.GetPosition(token.Span.StartUtf8Byte).Line, Is.EqualTo(1));
        Assert.That(token.Span.Length, Is.GreaterThan(token.Text.Length));
        var emoji = source.Text.IndexOf("🚀", StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ByteOffset(emoji + 1));
        var escaped = MetaLexer.Lex(new("escape.meta", "\"\\u{1F680}\""));
        Assert.That(escaped.Tokens[0].Value, Is.EqualTo("🚀"));
        Assert.That(MetaCompiler.InferRootModule(new("root.meta", "/* copyright */\n // hello\n module owner.program; fn main()->void {}")), Is.EqualTo("owner.program"));
    }

    [Test]
    public void LimitsAndCancellationAreBounded()
    {
        var result = new MetaCompiler().Compile(new([new("test.meta", "module test.program; fn main()->void {" + new string('(', 130) + "1" + new string(')', 130) + ";}")], "test.program"));
        Assert.That(result.Success, Is.False);
        Assert.That(result.Diagnostics.Select(d => d.Code), Does.Contain("E403"));
        var source = new SourceText("test.meta", "module test.program; " + new string('?', 10000));
        Assert.That(MetaParser.Parse(source).Diagnostics.Count, Is.LessThanOrEqualTo(100));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => new MetaCompiler().Compile(new([new("test.meta", "module test.program;")], "test.program"), cancel.Token));
    }

    [Test]
    public void ConstantArithmeticIsCheckedAndPreservesDefinedRemainder()
    {
        var overflow = Compile("module test.program; const n:int=9223372036854775807+1;");
        Assert.That(overflow.Diagnostics.Select(d => d.Code), Does.Contain("E204"));
        var valid = Compile("module test.program; const n:int=-9223372036854775808 % -1; fn main()->void { let value=n; }");
        Assert.That(valid.Success, Is.True);
        Assert.That(valid.Module!.Constants.Any(c => c.Kind == MetaConstantKind.Int && c.Value == "0"), Is.True);
        var byteContext = Compile("module test.program; import meta.numeric; fn main()->void {let b:byte=255; let n=numeric.byteToInt(b);}");
        Assert.That(byteContext.Success, Is.True);
        var invalidByte = Compile("module test.program; fn main()->void {let b:byte=255; let n:int=b;}");
        Assert.That(invalidByte.Diagnostics.Select(d => d.Code), Does.Contain("E201"));
        var invalidUnreachable = Compile("module test.program; const b:bool=false&&42;");
        Assert.That(invalidUnreachable.Success, Is.False);
        var shortCircuit = Compile("module test.program; const b:bool=false&&(1/0==0); const order:bool=\"\\u{1F600}\">\"\\u{FFFF}\"; fn main()->void {let value=b;let greater=order;let minimum=-0x8000000000000000;}");
        Assert.That(shortCircuit.Success, Is.True);
        Assert.That(shortCircuit.Module!.Constants.Any(c => c.Kind == MetaConstantKind.Bool && c.Value == "true"), Is.True);
    }
}
