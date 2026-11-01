// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class BinaryLibraryTests
{
    private static MetaBinaryLibrary Library(string package, string module, string body, params MetaBinaryLibrary[] dependencies)
    {
        var result = new MetaCompiler().Compile(new([new("closed-library.meta", "module " + module + ";" + body)], module, package, BinaryDependencies: dependencies, Library: true));
        Assert.That(result.Success, Is.True, Errors(result));
        MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!));
        return new(package, "1.0.0", result.ArtifactHash!, result.Artifact!, result.Exports!);
    }
    private static string Errors(MetaCompilationResult result) => string.Join('\n', result.Diagnostics.Select(d => d.Code + ":" + string.Join(',', d.Arguments)));
    private static MetaCompilationResult Root(string body, params MetaBinaryLibrary[] libraries) => new MetaCompiler().Compile(new(
        [new("root.meta", "module test.root; import meta.system; import meta.text; " + body)], "test.root", BinaryDependencies: libraries));
    private static IReadOnlyList<string> Run(MetaCompilationResult result)
    {
        Assert.That(result.Success, Is.True, Errors(result));
        var verified = MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!));
        var host = new Host(); var process = new MetaProcess(verified, host); Assert.That(process.TryStartMain(), Is.True);
        for (var slice = 0; slice < 10000 && process.State is MetaProcessState.Runnable or MetaProcessState.Collecting; slice++) process.RunSlice(7, 1000);
        Assert.That(process.Fault, Is.Null); return host.Logs;
    }

    [Test]
    public void ClosedBinaryLinksFunctionsTypesConstantsAndPrivateStateWithoutSourceDebug()
    {
        var library = Library("maker.package", "maker.library", """
            pub struct Reading { value:int; }
            pub const VERSION:string="v1";
            var counter:int=10;
            fn next()->int { counter=counter+1; return counter; }
            pub fn read()->Reading { return new Reading(value:next()); }
            """);
        var result = Root("import maker.library; fn main()->void { let reading:Reading=library.read();system.log(text.fromInt(reading.value));system.log(library.VERSION); }", library);
        Assert.That(Run(result), Is.EqualTo(new[] { "11", "v1" }));
        Assert.That(result.Module!.Debug!.Files.Select(f => f.Path), Is.EqualTo(new[] { "root.meta" }));
        Assert.That(result.Module.Imports.Single(i => i.Kind == "Library").ContentHash, Is.EqualTo(library.ContentHash));
        var again = Root("import maker.library; fn main()->void { let reading:Reading=library.read();system.log(text.fromInt(reading.value));system.log(library.VERSION); }", library);
        Assert.That(again.Artifact, Is.EqualTo(result.Artifact));
    }

    [Test]
    public void BinaryPrivateMembersAndUnauthorizedOrMismatchedLocksAreRejected()
    {
        var library = Library("maker.hidden", "maker.hidden", "struct Secret {value:int;} fn hidden()->int{return 7;} pub fn shown()->int{return hidden();}");
        var hidden = Root("import maker.hidden; fn main()->void {let x=hidden.hidden();}", library);
        Assert.That(hidden.Diagnostics.Select(d => d.Code), Does.Contain("E229"));
        var privateType = Root("import maker.hidden; fn main()->void {let x:Secret=new Secret(value:1);}", library);
        Assert.That(privateType.Success, Is.False);
        var unauthorized = Root("import maker.hidden; fn main()->void {}", library with { Authorized = false });
        Assert.That(unauthorized.Diagnostics.Select(d => d.Code), Does.Contain("E304"));
        var mismatch = Root("import maker.hidden; fn main()->void {}", library with { ContentHash = new string('0', 64) });
        Assert.That(mismatch.Diagnostics.Select(d => d.Code), Does.Contain("E310"));
    }

    [Test]
    public void DiamondDependenciesShareOneMutableLibraryInstanceAndInitializer()
    {
        var foundation = Library("maker.foundation", "maker.foundation", "var count:int=40; pub fn next()->int {count=count+1;return count;}");
        var left = Library("maker.left", "maker.left", "import maker.foundation;pub fn get()->int{return foundation.next();}", foundation);
        var right = Library("maker.right", "maker.right", "import maker.foundation;pub fn get()->int{return foundation.next();}", foundation);
        var root = Root("import maker.left;import maker.right; fn main()->void {system.log(text.fromInt(left.get()));system.log(text.fromInt(right.get()));}", left, right);
        Assert.That(Run(root), Is.EqualTo(new[] { "41", "42" }));
        Assert.That(root.Module!.Functions.Count(f => f.Name == "maker.foundation.next"), Is.EqualTo(1));
    }

    [Test]
    public void ExportCertificateRebuildsFromSourceAndRejectsChangedPublicHeader()
    {
        var request = new MetaCompilationRequest([new("app.meta", "module test.app; pub fn view()->int{return 1;} fn hidden()->int{return 2;}")], "test.app");
        var compiler = new MetaCompiler(); var result = compiler.Compile(request);
        Assert.That(result.Success, Is.True, Errors(result));
        var verified = MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!));
        var proof = compiler.VerifyExports(request, verified);
        Assert.That(proof.ArtifactDigest, Is.EqualTo(verified.Digest)); Assert.That(proof.Functions.Select(f => f.Name), Is.EqualTo(new[] { "test.app.view" }));
        var changed = request with { Sources = [new("app.meta", "module test.app; fn view()->int{return 1;} pub fn hidden()->int{return 2;}")] };
        Assert.Throws<Content.Meta.Bytecode.MetaFormatException>(() => compiler.VerifyExports(changed, verified));
    }

    private sealed class Host : IMetaHost
    {
        public List<string> Logs = new();
        public MetaValue Read(MetaProcess process, MetaHostRequest request) => throw new InvalidOperationException();
        public MetaHostResult BeginAwait(MetaProcess process, MetaHostRequest request) => throw new InvalidOperationException();
        public void Log(MetaProcess process, string text) => Logs.Add(text);
        public void Cancel(MetaProcess process, long operationId) { }
    }
}
