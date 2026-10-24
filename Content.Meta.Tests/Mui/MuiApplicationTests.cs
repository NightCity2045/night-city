// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Mui;

[TestFixture]
public sealed class MuiApplicationTests
{
    private const string Source = """
        module sample.app;
        import meta.app;
        pub struct State { name:string; enabled:bool; rows:array<int>; }
        pub struct Input { name:string; }
        pub fn view(context:app.AppContext)->State { return new State(name:"Alice",enabled:true,rows:[1,2]); }
        pub fn submit(context:app.AppContext,input:Input)->result<void> { return ok(()); }
        fn main()->void {}
        """;
    private const string Contract = """
        {"version":"1.0","views":{"main":{"function":"sample.app.view","document":"ui/main.mui","stateType":"sample.app.State",
          "fields":{"name":{"type":"string","maxLength":32},"enabled":{"type":"boolean"},"rows":{"type":"array","maxLength":64}},"actions":["submit"]}},
         "actions":{"submit":{"function":"sample.app.submit","inputType":"sample.app.Input","inputs":{"name":{"type":"string","maxLength":32}},"scopes":[]}},"resources":[]}
        """;
    private static readonly Dictionary<string, string> Documents = new()
    {
        ["ui/main.mui"] = "<Page id='page'><Column id='column'><Text id='name' text='{bind name}'/><Button id='submit' action='submit' text='Submit'/></Column></Page>",
    };

    private static (VerifiedArtifact Artifact, MetaExportCertificate Exports) Compile()
    {
        var compiler = new MetaCompiler();
        var request = new MetaCompilationRequest([new("src/main.meta", Source)], "sample.app");
        var compiled = compiler.Compile(request);
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(x => x.Code)));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        return (artifact, compiler.VerifyExports(request, artifact));
    }

    [Test]
    public void AdmissionBindsPackageDigestAndExactPublicSignatures()
    {
        var (artifact, exports) = Compile();
        var digest = MuiVerifiedApplication.ComputePackageDigest(artifact.Digest, Contract, Documents, "");
        Assert.That(MuiVerifiedApplication.TryAdmit(artifact, exports, Contract, Documents, "", digest, out var app, out _), Is.True);
        Assert.That(app!.Views["main"].FunctionId, Is.GreaterThanOrEqualTo(0));
        Assert.That(MuiVerifiedApplication.TryAdmit(artifact, exports with { ArtifactDigest = new('0', 64) }, Contract, Documents, "", digest, out _, out _), Is.False);
        Assert.That(MuiVerifiedApplication.TryAdmit(artifact, exports, Contract, Documents, "", new('0', 64), out _, out _), Is.False);
        var removed = exports with { Functions = exports.Functions.Where(x => !x.Name.EndsWith(".submit", StringComparison.Ordinal)).ToArray() };
        Assert.That(MuiVerifiedApplication.TryAdmit(artifact, removed, Contract, Documents, "", digest, out _, out _), Is.False);
    }

    [Test]
    public void ActionInputValidatesBeforeVmAllocationAndStateProjectsWithoutAuthority()
    {
        var (artifact, exports) = Compile();
        var digest = MuiVerifiedApplication.ComputePackageDigest(artifact.Digest, Contract, Documents, "");
        Assert.That(MuiVerifiedApplication.TryAdmit(artifact, exports, Contract, Documents, "", digest, out var app, out _), Is.True);
        var action = app!.Actions["submit"];
        Assert.That(MuiDtoCodec.CanDecodeInput(artifact, action.InputTypeId, "{\"name\":17}", app.Contract.Actions["submit"].Inputs), Is.False);
        var process = new MetaProcess(artifact);
        var input = MuiDtoCodec.DecodeInput(process, artifact, action.InputTypeId, "{\"name\":\"Alice\"}", app.Contract.Actions["submit"].Inputs);
        Assert.That(process.GetString(process.GetField(input, 0)), Is.EqualTo("Alice"));
        var authority = process.CreateOpaque(app.UserRefTypeId, 42);
        Assert.That(MuiDtoCodec.TryProject(process, artifact, app.UserRefTypeId, authority, app.Contract.Views["main"].Fields, out _), Is.False);
        Assert.That(process.TryReleaseOpaque(authority), Is.True);
    }

    [TestCase("citi://a45b6b15-7d02-4c44-ae27-c10535495264/main", true)]
    [TestCase("citi://a45b6b15-7d02-4c44-ae27-c10535495264", true)]
    [TestCase("https://example.com", false)]
    [TestCase("citi://a45b6b15-7d02-4c44-ae27-c10535495264/../secret", false)]
    [TestCase("citi://a45b6b15-7d02-4c44-ae27-c10535495264/main?admin=true", false)]
    [TestCase("citi://user@a45b6b15-7d02-4c44-ae27-c10535495264/main", false)]
    [TestCase("citi://a45b6b15-7d02-4c44-ae27-c10535495264/%2e%2e", false)]
    public void CitiNetRoutesCannotBecomeExternalOrAmbiguousAddresses(string address, bool accepted) =>
        Assert.That(MuiCitiNetAddress.TryParse(address, out _), Is.EqualTo(accepted));
}
