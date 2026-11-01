// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaProjectTemplateTests
{
    [Test]
    public void GameResourcesDoNotShipDemonstrationProgramsOrScenes()
    {
        var resources = Path.Combine(Root, "Resources");
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(resources, "Meta", "Projects", "catalog.json")));
        Assert.That(catalog.RootElement.GetProperty("projects").GetArrayLength(), Is.Zero);
        foreach (var folder in new[] { "Examples", "Garage", "Dashboard", "Smoke" })
            Assert.That(Directory.Exists(Path.Combine(resources, "Meta", folder)), Is.False, folder);
        Assert.That(File.Exists(Path.Combine(resources, "Prototypes", "_NC", "Meta", "demo-scenes.yml")), Is.False);
    }

    [Test]
    public void CurrentLanguageCatalogDoesNotExposeTheRemovedExampleOrderService()
    {
        Assert.That(MetaApiCatalog.Default.Modules, Does.Not.Contain("meta.services"));
        Assert.That(MetaApiCatalog.Default.Members.Any(member => member.Owner.StartsWith("meta.services.", StringComparison.Ordinal)), Is.False);
    }

    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Content.Meta.Tests", "Fixtures", "Meta", "Projects"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }
    public static IEnumerable<string> Packages()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "Content.Meta.Tests", "Fixtures", "Meta", "Projects", "catalog.json")));
        return catalog.RootElement.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToArray();
    }

    [TestCaseSource(nameof(Packages))]
    public void PackagedTemplateHashesBindingsAndFixtureExecuteAfterPrivateClone(string id)
    {
        var folder = Path.Combine(Root, "Content.Meta.Tests", "Fixtures", "Meta", "Projects", id);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        var root = manifest.RootElement;
        Assert.That(root.GetProperty("version").GetString(), Is.EqualTo("1.0"));
        Assert.That(root.GetProperty("id").GetString(), Is.EqualTo(id));
        var files = new List<MetaSourceFile>();
        var resources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("files").EnumerateArray())
        {
            var path = entry.GetProperty("path").GetString()!;
            Assert.That(path.Split('/'), Has.None.EqualTo(".."));
            Assert.That(Path.IsPathFullyQualified(path), Is.False);
            var source = new SourceText(path, File.ReadAllText(Path.Combine(folder, path)));
            Assert.That(source.Revision, Is.EqualTo(entry.GetProperty("sha256").GetString()));
            var canonical = File.ReadAllText(Path.Combine(Root, entry.GetProperty("sourcePath").GetString()!.Replace("Resources/Meta/", "Content.Meta.Tests/Fixtures/Meta/", StringComparison.Ordinal)));
            Assert.That(new SourceText(path, canonical).Revision, Is.EqualTo(source.Revision));
            if (path.EndsWith(".meta", StringComparison.Ordinal)) files.Add(new(path, source.Text));
            else resources.Add(path, source.Text);
        }
        var compiled = new MetaCompiler().Compile(new(files, root.GetProperty("rootModule").GetString()!, root.GetProperty("packageName").GetString()!, root.GetProperty("packageVersion").GetString()!));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        var bindings = root.GetProperty("bindings").EnumerateArray().ToArray();
        Assert.That(bindings.Length, Is.EqualTo(compiled.Module!.Links.Length));
        foreach (var link in compiled.Module.Links)
        {
            var binding = bindings.Single(b => b.GetProperty("slot").GetString() == link.SlotName);
            Assert.That(binding.GetProperty("capability").GetString(), Is.EqualTo(compiled.Module.Types[link.CapabilityTypeId].Name));
            Assert.That(binding.GetProperty("contractHash").GetString(), Is.EqualTo(link.ContractHash));
            var expectedScopes = binding.GetProperty("requiredScopes").EnumerateArray().Select(s => s.GetString()).ToArray();
            var actualScopes = MetaApiCatalog.Default.Members.Where(m => m.Owner == compiled.Module.Types[link.CapabilityTypeId].Name &&
                compiled.Module.Imports.Any(import => import.Name == m.QualifiedName)).Select(m => m.Scope).Distinct().ToArray();
            Assert.That(actualScopes.All(compiled.Module.Manifest.RequiredScopes.Contains), Is.True);
            Assert.That(expectedScopes, Is.EquivalentTo(actualScopes));
        }
        var fixture = MetaTestHost.ParseFixture(File.ReadAllText(Path.Combine(folder, root.GetProperty("testFixture").GetString()!)));
        var verified = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module));
        if (resources.TryGetValue("app.json", out var contract))
        {
            Assert.That(MuiContractParser.TryParse(contract, out var schema, out var schemaError), Is.True, schemaError);
            var documents = resources.Where(value => value.Key.EndsWith(".mui", StringComparison.Ordinal))
                .ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
            var styles = resources.Where(value => value.Key.EndsWith(".mstyle", StringComparison.Ordinal)).ToArray();
            foreach (var style in styles)
                Assert.That(MuiStyleSheet.TryParse(style.Value, schema!, out _, out var styleError), Is.True, style.Key + ":" + styleError);
            var styleText = styles.FirstOrDefault().Value ?? "";
            var exports = new MetaCompiler().VerifyExports(new(files, root.GetProperty("rootModule").GetString()!,
                root.GetProperty("packageName").GetString()!, root.GetProperty("packageVersion").GetString()!), verified);
            var digest = MuiVerifiedApplication.ComputePackageDigest(verified.Digest, contract, documents, styleText);
            Assert.That(MuiVerifiedApplication.TryAdmit(verified, exports, contract, documents, styleText, digest,
                out _, out var admissionError), Is.True, admissionError);
        }
        else Assert.That(resources.Keys.Any(path => path.EndsWith(".mui", StringComparison.Ordinal) ||
            path.EndsWith(".mstyle", StringComparison.Ordinal)), Is.False, "Shipped app markup requires its declared app contract.");
        var run = MetaTestHost.Run(verified, fixture);
        Assert.That(run.Passed, Is.True, run.FailureCode + ":" + run.Fault);
    }
}
