// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class MetaSemanticHeaderTests
{
    private static (MetaSemanticLibraryHeader Header, VerifiedArtifact Artifact, MetaExportCertificate Exports) Header()
    {
        const string source = """
            module maker.library;
            pub enum Mode {Off,On}
            pub struct Reading {rpm:int;mode:Mode;}
            pub const LIMIT:int=42;
            pub const ACTIVE:Mode=Mode.On;
            var privateCounter:int=0;
            struct PrivateSecret {pin:int;}
            fn privateHelper()->int {privateCounter=privateCounter+1;return privateCounter;}
            pub fn read()->Reading {return new Reading(rpm:privateHelper(),mode:Mode.On);}
            """;
        var result = new MetaCompiler().Compile(new([new("private-source.meta", source)], "maker.library", "maker.package", Library: true));
        Assert.That(result.Success, Is.True, string.Join('\n', result.Diagnostics));
        var artifact = MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!));
        return (MetaSemanticHeaders.Project(artifact, result.Exports!), artifact, result.Exports!);
    }

    [Test]
    public void PublicProjectionContainsOnlyDigestBoundAbiWithoutCodePrivateSymbolsOrSource()
    {
        var (header, artifact, certificate) = Header();
        var json = JsonSerializer.Serialize(header);
        Assert.That(header.ArtifactDigest, Is.EqualTo(artifact.Digest));
        Assert.That(header.Functions.Select(value => value.Name), Is.EqualTo(new[] { "maker.library.read" }));
        Assert.That(header.Types.Select(value => value.Name), Is.EquivalentTo(new[] { "maker.library.Reading", "maker.library.Mode" }));
        foreach (var secret in new[] { "privateCounter", "privateHelper", "PrivateSecret", "private-source.meta", "Instruction", "Debug" })
            Assert.That(json, Does.Not.Contain(secret));
        Assert.Throws<MetaFormatException>(() => MetaSemanticHeaders.Project(artifact, certificate with { ArtifactDigest = new string('0', 64) }));
        Assert.Throws<MetaFormatException>(() => MetaSemanticHeaders.Project(artifact, certificate with { SourceTreeHash = new string('0', 64) }));
    }

    [Test]
    public void HeaderOnlyBindingUsesRealImportedConstantsTypesAndReceiverMembersWithoutExecutableBodies()
    {
        var header = Header().Header;
        const string source = """
            module editor; import maker.library as lib;
            const CHECK:int=84/lib.LIMIT;
            fn work()->int {let reading=lib.read();return reading.rpm+CHECK;}
            """;
        var result = new MetaCompiler().AnalyzeSemantics(new([new("editor.meta", source)], "editor"), publicDependencies: [header]);
        Assert.That(result.Diagnostics.Where(value => value.Severity == MetaDiagnosticSeverity.Error), Is.Empty);
        Assert.That(result.Index.Complete, Is.True);
        Assert.That(result.Index.Symbols.Single(value => value.QualifiedName == "maker.library.read").Definition, Is.Null);
        Assert.That(result.Index.Symbols.Single(value => value.QualifiedName == "maker.library.Reading.rpm").TypeName, Is.EqualTo("int"));
        Assert.That(result.Index.Documents.Select(value => value.FileId), Is.EqualTo(new[] { "editor.meta" }));
        var changed = source.Replace("return reading.rpm+CHECK", "return reading.", StringComparison.Ordinal);
        var text = new SourceText("editor.meta", changed); var offset = changed.IndexOf("reading.;", StringComparison.Ordinal) + "reading.".Length;
        var completion = new MetaCompiler().AnalyzeSemantics(new([new(text.FileId, text.Text)], "editor"),
            new(text.FileId, text.Revision, offset), publicDependencies: [header]);
        Assert.That(completion.Index.CompleteAt(text.FileId, text.Revision, offset).Select(value => value.Name), Does.Contain("rpm"));
        var zero = header with { Constants = [new("maker.library.LIMIT", 0)], Values = [new("int", MetaConstantKind.Int, "0", [])] };
        var invalid = new MetaCompiler().AnalyzeSemantics(new([new("editor.meta", source)], "editor"), publicDependencies: [zero]);
        Assert.That(invalid.Diagnostics.Any(value => value.Severity == MetaDiagnosticSeverity.Error), Is.True,
            "Header constants retain their actual value instead of invented declaration defaults.");
    }

    [Test]
    public void RevokedOrAbsentDependencyCannotLeavePublicSymbolsInTheNextRevisionIndex()
    {
        const string source = "module editor;import maker.library as lib;fn work()->int{return lib.LIMIT;}";
        var request = new MetaCompilationRequest([new("editor.meta", source)], "editor");
        var authorized = new MetaCompiler().AnalyzeSemantics(request, publicDependencies: [Header().Header]);
        Assert.That(authorized.Index.Symbols.Any(value => value.QualifiedName == "maker.library.LIMIT"), Is.True);
        var revoked = new MetaCompiler().AnalyzeSemantics(request);
        Assert.That(revoked.Index.Symbols.Any(value => value.Module == "maker.library"), Is.False);
        Assert.That(revoked.Diagnostics.Select(value => value.Code), Does.Contain("E303"));
    }

    [Test]
    public void PublicHeaderMalformedConstantGraphFailsClosedAndNeverCompletesTheIndex()
    {
        var header = Header().Header;
        header = header with { Values = [new("int", MetaConstantKind.Int, "garbage", [])], Constants = [new("maker.library.LIMIT", 0)] };
        var result = new MetaCompiler().AnalyzeSemantics(new([new("editor.meta", "module editor;import maker.library;")], "editor"), publicDependencies: [header]);
        Assert.That(result.Index.Complete, Is.False);
        Assert.That(result.Diagnostics.Select(value => value.Code), Does.Contain("E310"));
    }

    [Test]
    public void VerifiedLargeStringConstantsKeepTheSameLimitInHeaderOnlyAnalysis()
    {
        var source = "module maker.large;pub const TEXT:string=\"" + new string('ж', 8192) + "\";";
        var compiled = new MetaCompiler().Compile(new([new("large.meta", source)], "maker.large", Library: true));
        Assert.That(compiled.Success, Is.True, string.Join('\n', compiled.Diagnostics));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var header = MetaSemanticHeaders.Project(artifact, compiled.Exports!);
        var analysis = new MetaCompiler().AnalyzeSemantics(new([new("editor.meta",
            "module editor;import maker.large as lib;const text:string=lib.TEXT;")], "editor"), publicDependencies: [header]);
        Assert.That(analysis.Diagnostics.Where(value => value.Severity == MetaDiagnosticSeverity.Error), Is.Empty);
        Assert.That(analysis.Index.Complete, Is.True);
    }

    [Test]
    public void ProjectRevisionStampChangesForLockContentPathBaseRevisionAndMembership()
    {
        MetaSemanticRevision[] files = [new("1", "main.meta", 3, new string('a',64)),new("2", "meta.lock.json", 1,new string('b',64))];
        var stamp = MetaSemanticHeaders.RevisionStamp(files);
        Assert.That(MetaSemanticHeaders.RevisionStamp(files.Reverse()), Is.EqualTo(stamp));
        foreach (var changed in new[] { files[1] with { Revision = 2 }, files[1] with { Path = "other.json" }, files[1] with { Digest = new string('c',64) } })
            Assert.That(MetaSemanticHeaders.RevisionStamp([files[0],changed]), Is.Not.EqualTo(stamp));
        Assert.That(MetaSemanticHeaders.RevisionStamp([files[0]]), Is.Not.EqualTo(stamp));
    }
}
