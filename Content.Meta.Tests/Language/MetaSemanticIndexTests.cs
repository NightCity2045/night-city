// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using Content.Meta.Language;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class MetaSemanticIndexTests
{
    private const string Library = """
        module library;
        pub struct Engine { rpm:int; }
        struct Secret { pin:int; }
        pub const answer:int=42;
        pub fn make()->Engine {return new Engine(rpm:42);}
        fn hidden()->int {return 7;}
        """;
    private const string Main = """
        module editor; import library as lib; import meta.doors;
        link door:doors.Door="door";
        fn first(input:lib.Engine)->int {
            let reading=lib.make(); var local:int=reading.rpm;
            local=local+1; return local;
        }
        fn second(input:lib.Engine)->int {let local:int=input.rpm;return local;}
        async fn main()->void {
            match(await door.setOpen(true)){ok(state)=>{let phase:bool=state.desiredOpen;}err(problem)=>{}}
        }
        """;

    private static MetaCompilationRequest Request(string text = Main) => new([new("main.meta", text), new("library.meta", Library)], "editor");
    private static SourceText Source(string text = Main) => new("main.meta", text);
    private static int At(string text, string fragment, int ordinal = 0)
    {
        var index = -1;
        for (var i = 0; i <= ordinal; i++) index = text.IndexOf(fragment, index + 1, StringComparison.Ordinal);
        Assert.That(index, Is.GreaterThanOrEqualTo(0), fragment);
        return Encoding.UTF8.GetByteCount(text.AsSpan(0, index));
    }

    [Test]
    public void CompilerBoundLocalsInDifferentFunctionsHaveDifferentDefinitionsAndExactReferences()
    {
        var analysis = new MetaCompiler().AnalyzeSemantics(Request());
        Assert.That(analysis.Diagnostics.Where(value => value.Severity == MetaDiagnosticSeverity.Error), Is.Empty);
        var source = Source();
        var first = analysis.Index.SymbolAt(source.FileId, source.Revision, At(Main, "local=local") + 1)!;
        var second = analysis.Index.SymbolAt(source.FileId, source.Revision, At(Main, "return local", 1) + 8)!;
        Assert.That(first.Kind, Is.EqualTo(MetaSemanticKind.Local));
        Assert.That(first.Id, Is.Not.EqualTo(second.Id));
        Assert.That(first.Definition!.Value.StartUtf8Byte, Is.EqualTo(At(Main, "local:int=reading")));
        Assert.That(second.Definition!.Value.StartUtf8Byte, Is.EqualTo(At(Main, "local:int=input")));
        Assert.That(analysis.Index.ReferencesTo(first.Id).Length, Is.EqualTo(3));
        Assert.That(analysis.Index.ReferencesTo(second.Id).Length, Is.EqualTo(1));
    }

    [Test]
    public void ImportedFunctionAndNominalFieldNavigateToTheirActualAuthorizedSourceDefinitions()
    {
        var index = new MetaCompiler().AnalyzeSemantics(Request()).Index;
        var source = Source();
        var function = index.SymbolAt(source.FileId, source.Revision, At(Main, "lib.make()") + 5)!;
        var field = index.SymbolAt(source.FileId, source.Revision, At(Main, "reading.rpm") + 9)!;
        Assert.That(function.QualifiedName, Is.EqualTo("library.make"));
        Assert.That(function.Definition!.Value.FileId, Is.EqualTo("library.meta"));
        Assert.That(field.Kind, Is.EqualTo(MetaSemanticKind.Field));
        Assert.That(field.QualifiedName, Is.EqualTo("library.Engine.rpm"));
        Assert.That(field.Definition!.Value.StartUtf8Byte, Is.EqualTo(At(Library, "rpm:int")));
        Assert.That(index.ReferencesTo(field.Id).Select(value => value.FileId), Does.Contain("main.meta"));
    }

    [TestCase("reading.", "rpm", "setOpen")]
    [TestCase("door.", "setOpen", "setActive")]
    [TestCase("lib.", "make", "hidden")]
    public void CompletionUsesTheCompilerResolvedReceiverAndPublicImportSurface(string receiver, string expected, string excluded)
    {
        var main = $$"""
            module editor; import library as lib; import meta.doors;
            link door:doors.Door="door";
            fn work()->void {let reading=lib.make(); {{receiver}};}
            """;
        var source = Source(main);
        var offset = At(main, receiver) + receiver.Length;
        var analysis = new MetaCompiler().AnalyzeSemantics(Request(main), new(source.FileId, source.Revision, offset));
        var candidates = analysis.Index.CompleteAt(source.FileId, source.Revision, offset);
        Assert.That(candidates.Select(value => value.Name), Does.Contain(expected));
        Assert.That(candidates.Select(value => value.Name), Does.Not.Contain(excluded));
        Assert.That(candidates.Select(value => value.Name), Does.Not.Contain("Secret"));
        Assert.That(analysis.Index.Documents.Single(value => value.FileId == source.FileId).Revision, Is.EqualTo(source.Revision));
    }

    [Test]
    public void FieldNamesFromDifferentNominalTypesDoNotBecomeTextualReferences()
    {
        const string main = """
            module editor; struct A {value:int;} struct B {value:int;}
            fn get(a:A,b:B)->int {return a.value+b.value;}
            """;
        var source = Source(main);
        var index = new MetaCompiler().AnalyzeSemantics(Request(main)).Index;
        var a = index.SymbolAt(source.FileId, source.Revision, At(main, "a.value") + 3)!;
        var b = index.SymbolAt(source.FileId, source.Revision, At(main, "b.value") + 3)!;
        Assert.That(a.Id, Is.Not.EqualTo(b.Id));
        Assert.That(index.ReferencesTo(a.Id), Has.Length.EqualTo(1));
        Assert.That(index.ReferencesTo(b.Id), Has.Length.EqualTo(1));
    }

    [Test]
    public void ShadowingUsesTheCompilerDiagnosticAndNeverInventsASecondBoundLocal()
    {
        const string main = "module editor; fn work()->void {let value:int=1; {let value:int=2;}}";
        var analysis = new MetaCompiler().AnalyzeSemantics(Request(main));
        Assert.That(analysis.Diagnostics.Select(value => value.Code), Does.Contain("E211"));
        Assert.That(analysis.Index.Symbols.Count(value => value.Kind == MetaSemanticKind.Local && value.Name == "value"), Is.EqualTo(1));
        Assert.That(analysis.Index.Complete, Is.False);
    }

    [Test]
    public void StaleRevisionCannotResolveDefinitionsReferencesOrCompletionAfterAnEdit()
    {
        var source = Source();
        var index = new MetaCompiler().AnalyzeSemantics(Request()).Index;
        var changed = Source(Main.Replace("local+1", "local+2", StringComparison.Ordinal));
        Assert.That(index.IsCurrent(source.FileId, changed.Revision), Is.False);
        Assert.That(index.SymbolAt(source.FileId, changed.Revision, At(Main, "local=local")), Is.Null);
        Assert.That(index.CompleteAt(source.FileId, changed.Revision, At(Main, "reading.rpm") + 8), Is.Empty);
    }

    [Test]
    public void UnqualifiedCompletionIncludesImportedTypesButRequiresAnAliasForImportedFunctionsAndConstants()
    {
        const string main = "module editor;import library as lib;fn work()->void{ ;}";
        var text = Source(main); var offset = At(main, " ;}") + 1;
        var index = new MetaCompiler().AnalyzeSemantics(Request(main)).Index;
        var names = index.CompleteAt(text.FileId, text.Revision, offset).Select(value => value.Name).ToArray();
        Assert.That(names, Does.Contain("Engine"));
        Assert.That(names, Does.Contain("lib"));
        Assert.That(names, Does.Not.Contain("make"));
        Assert.That(names, Does.Not.Contain("answer"));
        Assert.That(names, Does.Contain("return"));
        Assert.That(names, Does.Contain("int"));
        Assert.That(names, Does.Not.Contain("unsafe"));
    }

    [Test]
    public void TypeAnnotationsBindTheirImportAliasInsteadOfOnlyTheNominalType()
    {
        var source = Source(); var index = new MetaCompiler().AnalyzeSemantics(Request()).Index;
        var alias = index.SymbolAt(source.FileId, source.Revision, At(Main, "lib.Engine") + 1)!;
        Assert.That(alias.Kind, Is.EqualTo(MetaSemanticKind.Import));
        Assert.That(alias.QualifiedName, Is.EqualTo("library"));
        Assert.That(index.ReferencesTo(alias.Id).Select(value => value.StartUtf8Byte), Does.Contain(At(Main, "lib.Engine")));
    }

    [Test]
    public void UnauthorizedSourceDependencyIsNeverIndexedEvenWhenTheCallerKnowsItsModuleName()
    {
        const string main = "module editor; import privatePackage as secret; fn work()->void{}";
        var request = new MetaCompilationRequest([new("main.meta", main)], "editor", Dependencies:
            [new("private-package", "1.0.0", new string('a', 64), [new("private.meta", "module privatePackage; pub fn forbidden()->int{return 1;}")], Authorized: false)]);
        var analysis = new MetaCompiler().AnalyzeSemantics(request);
        Assert.That(analysis.Index.Documents.Select(value => value.FileId), Does.Not.Contain("private.meta"));
        Assert.That(analysis.Index.Symbols.Select(value => value.Name), Does.Not.Contain("forbidden"));
        Assert.That(analysis.Diagnostics.Select(value => value.Code), Does.Contain("E304"));
    }

    [Test]
    public void UnicodeBeforeAReferenceDoesNotShiftUtf8DefinitionOrBindingOffsets()
    {
        const string main = "module editor; fn work()->int {let text:string=\"е́😀\";let number:int=3;return number;}";
        var source = Source(main);
        var index = new MetaCompiler().AnalyzeSemantics(Request(main)).Index;
        var symbol = index.SymbolAt(source.FileId, source.Revision, At(main, "return number") + 8)!;
        Assert.That(symbol.Definition!.Value.StartUtf8Byte, Is.EqualTo(At(main, "number:int")));
        Assert.That(source.Utf16Offset(At(main, "return number")), Is.EqualTo(main.IndexOf("return number", StringComparison.Ordinal)));
    }

    [Test]
    public void SemanticAnalysisRejectsOversizedBundlesBeforeBuildingAnIndex()
    {
        var compiler = new MetaCompiler();
        Assert.Throws<ArgumentException>(() => compiler.AnalyzeSemantics(new([new("main.meta", new string('x', 256 * 1024 + 1))], "editor")));
        Assert.Throws<ArgumentException>(() => compiler.AnalyzeSemantics(new(Enumerable.Range(0, 65)
            .Select(value => new MetaSourceFile(value + ".meta", "module editor;" )).ToArray(), "editor")));
    }

    [Test]
    public void LocalsOutsideTheirLexicalBlockNeverAppearInCompletion()
    {
        const string main = "module editor; fn work()->void {if(true){let expired:int=1;}let current:int=2;cur;}";
        var source = Source(main); var offset = At(main, "cur;}") + 3;
        var index = new MetaCompiler().AnalyzeSemantics(Request(main), new(source.FileId, source.Revision, offset)).Index;
        var candidates = index.CompleteAt(source.FileId, source.Revision, offset);
        Assert.That(candidates.Select(value => value.Name), Does.Contain("current"));
        Assert.That(candidates.Select(value => value.Name), Does.Not.Contain("expired"));
    }

    [Test]
    public void WatchParameterDefinitionPointsIntoTheParameterListRatherThanTheEventName()
    {
        const string main = """
            module editor; import meta.doors; link door:doors.Door="door";
            watch door.stateChanged(stateChanged){let desired:bool=stateChanged.desiredOpen;}
            """;
        var source = Source(main);
        var index = new MetaCompiler().AnalyzeSemantics(Request(main)).Index;
        var parameter = index.SymbolAt(source.FileId, source.Revision, At(main, "stateChanged.desiredOpen") + 1)!;
        Assert.That(parameter.Kind, Is.EqualTo(MetaSemanticKind.Parameter));
        Assert.That(parameter.Definition!.Value.StartUtf8Byte, Is.EqualTo(At(main, "stateChanged){")));
    }

    [Test]
    public void SemanticRecordQuotaBoundsAnOtherwiseValidLargeProject()
    {
        var declarations = string.Concat(Enumerable.Range(0, 2800).Select(value => "let local" + value + ":int=0;"));
        var files = Enumerable.Range(0, 3).Select(value => new MetaSourceFile(value + ".meta",
            "module module" + value + "; fn work()->void{" + declarations + "}")).ToArray();
        var result = new MetaCompiler().AnalyzeSemantics(new(files, "module0"));
        Assert.That(result.Index.Symbols.Length, Is.EqualTo(MetaSemanticIndex.MaximumSymbols));
        Assert.That(result.Index.References.Length, Is.LessThanOrEqualTo(MetaSemanticIndex.MaximumReferences));
        Assert.That(result.Index.Complete, Is.False, "Truncating language-service records never claims a complete semantic index.");
    }

    [Test]
    public void AnExplicitlyCancelledSemanticJobDoesNotProduceALateIndex()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new MetaCompiler().AnalyzeSemantics(Request(), cancellation: cancellation.Token));
    }
}
