// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class HybridRuntimeTests
{
    [Test]
    public void ExhaustiveMatchesReturnValuesOrContinueWithoutAnEmptyReturnPath()
    {
        const string source = """
            module test.program;
            import meta.system; import meta.text;
            enum Phase { First, Second }
            fn flag(value:bool)->string {match(value){true=>{return "t";}false=>{return "f";}}}
            fn phase(value:Phase)->string {match(value){Phase.First=>{return "a";}Phase.Second=>{return "b";}}}
            fn optionValue(value:option<int>)->int {match(value){some(number)=>{return number;}none=>{return -1;}}}
            fn resultValue(value:result<int>)->int {match(value){ok(number)=>{return number;}err(error)=>{return -2;}}}
            fn continueAfter(value:bool)->int {var number=0;match(value){true=>{number=1;}false=>{number=2;}}return number+10;}
            async fn identity(value:int)->result<int> {return ok(value);}
            async fn forward(value:result<int>)->result<int> {
                match(value){ok(number)=>{return await identity(number);}err(error)=>{return err(error);}}
            }
            async fn main()->void {
                system.log(flag(true));system.log(flag(false));system.log(phase(Phase.First));system.log(phase(Phase.Second));
                system.log(text.fromInt(optionValue(some(5))));system.log(text.fromInt(optionValue(none)));
                system.log(text.fromInt(resultValue(ok(7))));
                system.log(text.fromInt(resultValue(err(system.error(system.ErrorCode.ActionRejected,"rejected")))));
                let forwarded=await forward(ok(9));system.log(text.fromInt(resultValue(forwarded)));
                let rejected=await forward(err(system.error(system.ErrorCode.ActionRejected,"rejected")));
                match(rejected){ok(number)=>{system.log("wrong");}err(error)=>{system.log("error");}}
                system.log(text.fromInt(continueAfter(true)));system.log(text.fromInt(continueAfter(false)));
            }
            """;
        var process = Process(source, out var host);
        Assert.That(process.TryStartMain(), Is.True);
        Drain(process, 3);
        Assert.That(process.Fault, Is.Null);
        Assert.That(host.Logs, Is.EqualTo(new[] { "t", "f", "a", "b", "5", "-1", "7", "-2", "9", "error", "11", "12" }));
    }

    private static MetaProcess Process(string source, out RecordingHost host, int slice = 7)
    {
        var result = new MetaCompiler().Compile(new([new("test.meta", source)], "test.program"));
        Assert.That(result.Success, Is.True, string.Join('\n', result.Diagnostics.Select(d => d.Code + ":" + string.Join(',', d.Arguments))));
        host = new RecordingHost();
        return new(MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!)), host);
    }
    private static void Drain(MetaProcess process, int gas = 7)
    {
        for (var slice = 0; slice < 10000 && process.State is MetaProcessState.Runnable or MetaProcessState.Collecting; slice++) process.RunSlice(gas, 1000);
    }
    private static long Evaluate(string declarations)
    {
        var process = Process("module test.program; " + declarations, out _);
        Assert.That(process.TryStartMain(), Is.True); Drain(process);
        // This helper starts a private pure entry after globals have been initialized.
        return process.LastResult?.AsInt() ?? long.MinValue;
    }

    [TestCase("hello", "META: программа запущена")]
    [TestCase("math", "250 W")]
    [TestCase("arraystats", "98.5")]
    [TestCase("readings", "Требуется проверка")]
    public void PureNormativeSourceExecutesItsExpectedOutput(string name, string expected)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Content.Meta.Tests", "Fixtures", "Meta", "Examples"))) directory = directory.Parent;
        var source = File.ReadAllText(Path.Combine(directory!.FullName, "Content.Meta.Tests", "Fixtures", "Meta", "Examples", "examples." + name + ".meta"));
        var result = new MetaCompiler().Compile(new([new("example.meta", source)], "examples." + name));
        Assert.That(result.Success, Is.True);
        var host = new RecordingHost(); var process = new MetaProcess(MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!)), host);
        Assert.That(process.TryStartMain(), Is.True); Drain(process, 1);
        Assert.That(process.Fault, Is.Null); Assert.That(host.Logs, Is.EqualTo(new[] { expected }));
    }

    [Test]
    public void PreparedLvaluesEvaluateIndexAndConstructorArgumentsOnceInSourceOrder()
    {
        var source = """
            module test.program;
            import meta.system; import meta.text;
            struct Pair { x:int; y:int; }
            var calls:int=0;
            fn index()->int { calls=calls+1; return 0; }
            fn mark(value:int)->int { calls=calls*10+value; return value; }
            fn main()->void {
                let values:array<Pair>=[new Pair(x:1,y:2)];
                values[index()].x=7;
                system.log(text.fromInt(calls));
                var pair=new Pair(y:mark(2),x:mark(1));
                let copy=pair;
                pair.x=99;
                system.log(text.fromInt(calls));
                system.log(text.fromInt(copy.x));
                system.log(text.fromInt(values[0].x));
            }
            """;
        var process = Process(source, out var host); Assert.That(process.TryStartMain(), Is.True); Drain(process, 1);
        Assert.That(process.Fault, Is.Null); Assert.That(host.Logs, Is.EqualTo(new[] { "1", "121", "1", "7" }));
    }

    [Test]
    public void ShortCircuitAndGlobalDependencyOrderSurviveSmallSlices()
    {
        var source = """
            module test.program;
            import meta.system; import meta.text;
            var first:int=second+1;
            var second:int=3;
            var calls:int=0;
            fn side()->bool { calls=calls+1; return true; }
            fn main()->void {
                let a=false&&side(); let b=true||side();
                system.log(text.fromInt(calls)); system.log(text.fromInt(first));
                let faultAvoided=false&&(1/0==0);
            }
            """;
        var process = Process(source, out var host); Assert.That(process.TryStartMain(), Is.True); Drain(process, 1);
        Assert.That(process.Fault, Is.Null); Assert.That(host.Logs, Is.EqualTo(new[] { "0", "4" }));
    }

    [Test]
    public void ListMutationDuringIterationTrapsAndMapPreservesInsertionOrder()
    {
        var process = Process("module test.program; fn main()->void { let values=new list<int>(); values.push(1); for(let item in values){ values.push(item); } }", out _);
        Assert.That(process.TryStartMain(), Is.True); Drain(process);
        Assert.That(process.Fault?.Code, Is.EqualTo(MetaVmFaultCode.CollectionModified));
        var map = Process("""
            module test.program; import meta.system; import meta.text;
            fn main()->void { let m=new map<string,int>(); m.set("first",1);m.set("second",2);m.set("first",3);
                let removed=m.remove("first");m.set("first",4);for(let key in m.keys()){system.log(key);}
            }
            """, out var host);
        Assert.That(map.TryStartMain(), Is.True); Drain(map);
        Assert.That(map.Fault, Is.Null); Assert.That(host.Logs, Is.EqualTo(new[] { "second", "first" }));
    }

    [Test]
    public void AsyncUserFunctionsUnitValuesAndByteConversionsExecute()
    {
        var process = Process("""
            module test.program; import meta.system; import meta.text; import meta.numeric;
            async fn child()->int { yield; return 7; }
            fn unit()->void { return (); }
            async fn main()->void {
                let empty=unit(); let answer=await child(); system.log(text.fromInt(answer));
                let zero:byte=0; let max:byte=255;
                system.log(text.fromInt(numeric.byteToInt(zero)));system.log(text.fromInt(numeric.byteToInt(max)));
                let tooLarge=numeric.toByte(256);match(tooLarge){ok(v)=>{system.log("bad");}err(e)=>{system.log("range");}}
            }
            """, out var host);
        Assert.That(process.TryStartMain(), Is.True); Drain(process, 1);
        Assert.That(process.Fault, Is.Null); Assert.That(host.Logs, Is.EqualTo(new[] { "7", "0", "255", "range" }));
    }

    [Test]
    public void StructOptionAndResultEqualityUseValues()
    {
        var process = Process("""
            module test.program;import meta.system;
            struct Pair {value:int;}
            fn main()->void {let a=new Pair(value:1);let b=new Pair(value:1);
                let oa:option<Pair>=some(a);let ob:option<Pair>=some(b);
                let ra:result<Pair>=ok(a);let rb:result<Pair>=ok(b);
                if(a==b&&oa==ob&&ra==rb){system.log("equal");}
            }
            """, out var host);
        Assert.That(process.TryStartMain(), Is.True); Drain(process);
        Assert.That(process.Fault, Is.Null); Assert.That(host.Logs, Is.EqualTo(new[] { "equal" }));
    }

    private sealed class RecordingHost : IMetaHost
    {
        public List<string> Logs { get; } = new();
        public MetaValue Read(MetaProcess process, MetaHostRequest request) => throw new InvalidOperationException("Unexpected host call.");
        public MetaHostResult BeginAwait(MetaProcess process, MetaHostRequest request) => throw new InvalidOperationException("Unexpected host call.");
        public void Log(MetaProcess process, string text) => Logs.Add(text);
        public void Cancel(MetaProcess process, long operationId) { }
    }
}
