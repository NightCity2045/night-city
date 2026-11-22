// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class MetaDoorAccessTests
{
    private const string AccessProgram = """
        module test.dooraccess;
        import meta.system;
        import meta.doors;

        link door: Door = "front-door";

        async watch door.accessRequested(request) {
            let allow: bool = request.subject.name == "Alice";
            let answered = await door.answerAccess(request.requestId, allow);
            match (answered) {
                ok(state) => { system.log("answered"); }
                err(problem) => { system.log(problem.message); }
            }
        }

        watch door.accessResolved(resolution) {
            if (resolution.outcome == AccessOutcome.TimedOut) {
                system.log("program was too slow");
            }
        }
        """;

    private static MetaCompilationResult Compile(string source) =>
        new MetaCompiler().Compile(new([new("test.meta", source)], "test.dooraccess"));

    [Test]
    public void ProgramAnswersDoorAccessRequestsAndVerifies()
    {
        var result = Compile(AccessProgram);
        Assert.That(result.Success, Is.True, string.Join(',', result.Diagnostics.Select(d => d.Code)));
        var verified = MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!));
        Assert.That(verified.Manifest.ApiCatalogHash, Is.EqualTo(MetaApiCatalog.Default.Hash));
    }

    [Test]
    public void ProgramCannotForgeTheSubjectThatTheServerSupplies()
    {
        var result = Compile("""
            module test.dooraccess;
            import meta.system;
            import meta.access;
            fn main() -> void {
                let forged = new Subject(name: "Alice", key: "admin");
                system.log(forged.name);
            }
            """);
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public void CatalogBeforeDoorAccessStaysTrustedAndLacksTheNewApis()
    {
        var catalog = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.PreDoorAccessCatalogHash);
        Assert.That(catalog.Hash, Is.EqualTo(MetaApiCatalog.PreDoorAccessCatalogHash));
        Assert.That(catalog.Types.Any(type => type.Name == "meta.access.Subject"), Is.False);
        Assert.That(catalog.Members.Any(member => member.QualifiedName == "meta.doors.Door.answerAccess"), Is.False);
        Assert.That(MetaApiCatalog.Default.Members.Any(member => member.QualifiedName == "meta.doors.Door.answerAccess"), Is.True);
    }

    [Test]
    public void ProgramCompiledBeforeDoorAccessStillVerifiesAgainstTheCurrentCatalog()
    {
        var old = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.PreDoorAccessCatalogHash);
        var result = new MetaCompiler(old).Compile(new([new("old.meta",
            "module test.old;import meta.system;import meta.doors;link door: Door = \"front-door\";" +
            "async fn main()->void{let r = await door.setOpen(true);match(r){ok(_)=>{}err(e)=>{system.log(e.message);}}}")], "test.old"));
        Assert.That(result.Success, Is.True, string.Join(',', result.Diagnostics.Select(d => d.Code)));
        var verified = MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!));
        Assert.That(verified.Manifest.ApiCatalogHash, Is.EqualTo(MetaApiCatalog.PreDoorAccessCatalogHash));
    }
}
