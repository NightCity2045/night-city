// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

/// <summary>
/// The Night City Pulse showcase site actually renders: its view runs in the VM for both locales and its two actions
/// change the state the next view reports. The template test only proves the package compiles and is admitted.
/// </summary>
[TestFixture]
public sealed class MetaCityPulseSiteTests
{
    private const string Id = "meta-example-citypulse";

    private static string Folder
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Content.Meta.Tests", "Fixtures", "Meta", "Projects"))) directory = directory.Parent;
            return Path.Combine(directory!.FullName, "Content.Meta.Tests", "Fixtures", "Meta", "Projects", Id);
        }
    }

    private sealed class Site
    {
        public required VerifiedArtifact Artifact { get; init; }
        public required MuiVerifiedApplication App { get; init; }
        public required MetaProcess Process { get; init; }
    }

    private static Site Boot()
    {
        var source = File.ReadAllText(Path.Combine(Folder, "main.meta"));
        var contract = File.ReadAllText(Path.Combine(Folder, "app.json"));
        var document = File.ReadAllText(Path.Combine(Folder, "ui", "main.mui"));
        var style = File.ReadAllText(Path.Combine(Folder, "ui", "style.mstyle"));
        var compiler = new MetaCompiler();
        var request = new MetaCompilationRequest([new("main.meta", source)], "examples.citypulse", "examples.citypulse", "1.0.0");
        var compiled = compiler.Compile(request);
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(x => x.Code)));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var exports = compiler.VerifyExports(request, artifact);
        var documents = new Dictionary<string, string> { ["ui/main.mui"] = document };
        var digest = MuiVerifiedApplication.ComputePackageDigest(artifact.Digest, contract, documents, style);
        Assert.That(MuiVerifiedApplication.TryAdmit(artifact, exports, contract, documents, style, digest, out var app, out var error), Is.True, error);
        var process = new MetaProcess(artifact, limits: new MetaProcessLimits { RetainForInvocations = true });
        Assert.That(process.TryStartMain(), Is.True);
        Drain(process);
        return new() { Artifact = artifact, App = app!, Process = process };
    }

    private static void Drain(MetaProcess process)
    {
        for (var slice = 0; slice < 64 && process.State is MetaProcessState.Runnable or MetaProcessState.Collecting; slice++)
            process.RunSlice(1_000_000, 1000);
        Assert.That(process.State, Is.Not.EqualTo(MetaProcessState.Faulted), process.Fault?.ToString());
    }

    private static MetaValue Context(Site site, string locale) => site.Process.CreateRecord(site.App.ContextTypeId,
        [site.Process.CreateOpaque(site.App.UserRefTypeId, 1), site.Process.CreateOpaque(site.App.SessionRefTypeId, 2),
         site.Process.CreateString("main"), site.Process.CreateString("website"), site.Process.CreateString(locale)]);

    private static JsonElement View(Site site, string locale)
    {
        var view = site.App.Views["main"];
        Assert.That(site.Process.TryStart(view.FunctionId, [Context(site, locale)]), Is.True);
        Drain(site.Process);
        var result = site.Process.LastResult;
        Assert.That(result, Is.Not.Null, "The view returned nothing.");
        Assert.That(MuiDtoCodec.TryProject(site.Process, site.Artifact, view.StateTypeId, result!.Value, site.App.Contract.Views["main"].Fields, out var json), Is.True,
            "The state does not fit the contract or the size budget.");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static void Act(Site site, string action, string input)
    {
        var contract = site.App.Contract.Actions[action];
        var decoded = MuiDtoCodec.DecodeInput(site.Process, site.Artifact, site.App.Actions[action].InputTypeId, input, contract.Inputs);
        Assert.That(site.Process.TryStart(site.App.Actions[action].FunctionId, [Context(site, "en-US"), decoded]), Is.True);
        Drain(site.Process);
        Assert.That(site.Process.State, Is.Not.EqualTo(MetaProcessState.Faulted));
    }

    [Test]
    public void TheProjectedStateIsSmallEnoughForALiveSession()
    {
        var site = Boot();
        var view = site.App.Views["main"];
        Assert.That(site.Process.TryStart(view.FunctionId, [Context(site, "ru-RU")]), Is.True);
        Drain(site.Process);
        Assert.That(MuiDtoCodec.TryProject(site.Process, site.Artifact, view.StateTypeId, site.Process.LastResult!.Value, site.App.Contract.Views["main"].Fields, out var json), Is.True);
        Assert.That(System.Text.Encoding.UTF8.GetByteCount(json), Is.LessThan(16 * 1024));
    }

    [TestCase("en-US", "Overview")]
    [TestCase("ru-RU", "Обзор")]
    public void TheViewRendersInBothLocalesWithFullData(string locale, string overviewTab)
    {
        var state = View(Boot(), locale);
        Assert.That(state.GetProperty("tabOverview").GetString(), Is.EqualTo(overviewTab));
        Assert.That(state.GetProperty("chartEddies").GetArrayLength(), Is.EqualTo(24));
        Assert.That(state.GetProperty("chartThreat").GetArrayLength(), Is.EqualTo(24));
        Assert.That(state.GetProperty("chartPower").GetArrayLength(), Is.EqualTo(24));
        Assert.That(state.GetProperty("districts").GetArrayLength(), Is.EqualTo(7));
        Assert.That(state.GetProperty("market").GetArrayLength(), Is.EqualTo(6));
        Assert.That(state.GetProperty("news").GetArrayLength(), Is.EqualTo(6));
        Assert.That(state.GetProperty("powerNodes").GetArrayLength(), Is.EqualTo(9));
        Assert.That(state.GetProperty("hotspots").GetArrayLength(), Is.EqualTo(7));
    }

    [Test]
    public void ChartValuesStayInsideTheRangeTheMarkupDeclares()
    {
        var state = View(Boot(), "en-US");
        foreach (var name in new[] { "chartEddies", "chartThreat", "chartPower" })
            foreach (var value in state.GetProperty(name).EnumerateArray())
                Assert.That(value.GetDouble(), Is.InRange(0, 100), name);
        foreach (var name in new[] { "hotspots", "powerNodes" })
            foreach (var point in state.GetProperty(name).EnumerateArray())
            {
                Assert.That(point.GetProperty("x").GetDouble(), Is.InRange(0, 1), name);
                Assert.That(point.GetProperty("y").GetDouble(), Is.InRange(0, 1), name);
            }
    }

    [Test]
    public void RefreshAdvancesTheCityAndTheFilterNarrowsTheReport()
    {
        var site = Boot();
        var before = View(site, "en-US");
        Act(site, "refresh", "{}");
        var after = View(site, "en-US");
        Assert.That(after.GetProperty("clock").GetString(), Is.Not.EqualTo(before.GetProperty("clock").GetString()));
        Assert.That(after.GetProperty("chartEddies").GetRawText(), Is.Not.EqualTo(before.GetProperty("chartEddies").GetRawText()));

        Act(site, "setFilter", "{\"district\":\"Watson\",\"danger\":false}");
        var filtered = View(site, "en-US");
        Assert.That(filtered.GetProperty("districts").GetArrayLength(), Is.EqualTo(1));
        Assert.That(filtered.GetProperty("districts")[0].GetProperty("name").GetString(), Is.EqualTo("Watson"));
        Assert.That(filtered.GetProperty("hotspots").GetArrayLength(), Is.EqualTo(1));
        Assert.That(filtered.GetProperty("filterDistrict").GetString(), Is.EqualTo("Watson"));

        Act(site, "setFilter", "{\"district\":\"all\",\"danger\":true}");
        var dangerous = View(site, "en-US");
        Assert.That(dangerous.GetProperty("filterDanger").GetBoolean(), Is.True);
        Assert.That(dangerous.GetProperty("districts").GetArrayLength(), Is.LessThan(7));
    }

    [Test]
    public void AnUnknownDistrictIsRejectedAndTheFilterIsKept()
    {
        var site = Boot();
        Act(site, "setFilter", "{\"district\":\"Atlantis\",\"danger\":false}");
        var state = View(site, "en-US");
        Assert.That(state.GetProperty("filterDistrict").GetString(), Is.EqualTo("all"));
        Assert.That(state.GetProperty("districts").GetArrayLength(), Is.EqualTo(7));
    }
}
