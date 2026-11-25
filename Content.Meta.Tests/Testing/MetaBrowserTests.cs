// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

/// <summary>The CitiNet browser written in META: it compiles, is admitted, and its page and bookmarks behave.</summary>
[TestFixture]
public sealed class MetaBrowserTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Resources", "Meta", "Browser"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }

    private static string Folder => Path.Combine(Root, "Resources", "Meta", "Browser");
    private static string Project => Path.Combine(Root, "Resources", "Meta", "Projects", "meta-stock-citinet-browser");

    private sealed class Site
    {
        public required VerifiedArtifact Artifact { get; init; }
        public required MuiVerifiedApplication App { get; init; }
        public required MetaProcess Process { get; init; }
        public readonly Dictionary<int, MetaValue> Sessions = [];
    }

    private static Site Boot()
    {
        var source = File.ReadAllText(Path.Combine(Folder, "main.meta"));
        var contract = File.ReadAllText(Path.Combine(Folder, "app.json"));
        var document = File.ReadAllText(Path.Combine(Folder, "main.mui"));
        var style = File.ReadAllText(Path.Combine(Folder, "style.mstyle"));
        var compiler = new MetaCompiler();
        var request = new MetaCompilationRequest([new("main.meta", source)], "citinet.browser", "citinet.browser", "1.0.0");
        var compiled = compiler.Compile(request);
        Assert.That(compiled.Success, Is.True, string.Join(';', compiled.Diagnostics.Select(d => d.Code + ":" + d)));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var exports = compiler.VerifyExports(request, artifact);
        var documents = new Dictionary<string, string> { ["ui/main.mui"] = document };
        var digest = MuiVerifiedApplication.ComputePackageDigest(artifact.Digest, contract, documents, style);
        Assert.That(MuiVerifiedApplication.TryAdmit(artifact, exports, contract, documents, style, digest, out var app, out var error), Is.True, error);
        var process = new MetaProcess(artifact, limits: new MetaProcessLimits { RetainForInvocations = true });
        // Only the initializer runs: main reads the saved bookmarks from a file device, which does not exist in this isolated process.
        Assert.That(process.TryStart(artifact.Manifest.InitializerFunctionId!.Value, []), Is.True);
        Drain(process);
        return new() { Artifact = artifact, App = app!, Process = process };
    }

    private static void Drain(MetaProcess process)
    {
        for (var slice = 0; slice < 64 && process.State is MetaProcessState.Runnable or MetaProcessState.Collecting; slice++)
            process.RunSlice(1_000_000, 1000);
        Assert.That(process.State, Is.Not.EqualTo(MetaProcessState.Faulted), process.Fault?.ToString());
    }

    private static MetaValue Context(Site site, int session, string locale)
    {
        if (!site.Sessions.TryGetValue(session, out var handle))
            site.Sessions[session] = handle = site.Process.CreateOpaque(site.App.SessionRefTypeId, (ulong) session);
        return site.Process.CreateRecord(site.App.ContextTypeId,
            [site.Process.CreateOpaque(site.App.UserRefTypeId, 1), handle,
             site.Process.CreateString("main"), site.Process.CreateString("website"), site.Process.CreateString(locale)]);
    }

    private static JsonElement View(Site site, string locale = "en-US")
    {
        var view = site.App.Views["main"];
        Assert.That(site.Process.TryStart(view.FunctionId, [Context(site, 1, locale)]), Is.True);
        Drain(site.Process);
        Assert.That(MuiDtoCodec.TryProject(site.Process, site.Artifact, view.StateTypeId, site.Process.LastResult!.Value,
            site.App.Contract.Views["main"].Fields, out var json), Is.True);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static void Act(Site site, string action, string input)
    {
        var contract = site.App.Contract.Actions[action];
        var decoded = MuiDtoCodec.DecodeInput(site.Process, site.Artifact, site.App.Actions[action].InputTypeId, input, contract.Inputs);
        Assert.That(site.Process.TryStart(site.App.Actions[action].FunctionId, [Context(site, 1, "en-US"), decoded]), Is.True);
        Drain(site.Process);
    }

    [Test]
    public void TheStudioTestPassesWithTheFixtureThatAnswersTheBookmarkRead()
    {
        var compiler = new MetaCompiler();
        var request = new MetaCompilationRequest([new("main.meta", File.ReadAllText(Path.Combine(Folder, "main.meta")))], "citinet.browser", "citinet.browser", "1.0.0");
        var compiled = compiler.Compile(request);
        Assert.That(compiled.Success, Is.True);
        var verified = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var fixture = MetaTestHost.ParseFixture(File.ReadAllText(Path.Combine(Project, "meta.test.json")));
        var run = MetaTestHost.Run(verified, fixture);
        Assert.That(run.Passed, Is.True, run.FailureCode + ":" + run.Fault);
    }

    [Test]
    public void ABrandNewBrowserShowsAnEmptyPageAndNoBookmarks()
    {
        var state = View(Boot());
        Assert.That(state.GetProperty("page").GetString(), Is.Empty);
        Assert.That(state.GetProperty("history").GetArrayLength(), Is.Zero);
        Assert.That(state.GetProperty("marks").GetArrayLength(), Is.Zero);
        Assert.That(state.GetProperty("openLabel").GetString(), Is.EqualTo("Open"));
    }

    [Test]
    public void TheBrowserSpeaksRussianToARussianVisitor()
    {
        Assert.That(View(Boot(), "ru-RU").GetProperty("openLabel").GetString(), Is.EqualTo("Открыть"));
    }

    [Test]
    public void ABrandNewBrowserHasASitesListAndOneBarForSearchAndAddresses()
    {
        var state = View(Boot());
        Assert.That(state.GetProperty("sites").GetArrayLength(), Is.Zero);
        Assert.That(state.GetProperty("sitesHeading").GetString(), Is.EqualTo("Sites on the network"));
        Assert.That(state.GetProperty("hint").GetString(), Does.Contain("search"));
    }

    [Test]
    public void BookmarkingWithoutAnOpenPageChangesNothing()
    {
        var site = Boot();
        Act(site, "mark", "{}");
        Assert.That(View(site).GetProperty("marks").GetArrayLength(), Is.Zero);
    }

    [Test]
    public void TheStockManifestHashesMatchTheShippedFiles()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Project, "manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = Path.Combine(Root, file.GetProperty("sourcePath").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            Assert.That(new SourceText(file.GetProperty("path").GetString()!, File.ReadAllText(path)).Revision,
                Is.EqualTo(file.GetProperty("sha256").GetString()), file.GetProperty("path").GetString());
        }
    }

    [Test]
    public void EveryPermissionOfTheCatalogHasAPlainNameInBothLanguagesInTheOfferCard()
    {
        var source = File.ReadAllText(Path.Combine(Folder, "main.meta"));
        var missing = MetaApiCatalog.Default.Members.Select(member => member.Scope).Where(scope => scope.Length > 0).Distinct()
            .Where(scope => !source.Contains("scope == \"" + scope + "\") { return tr(ru, \"", StringComparison.Ordinal)).OrderBy(scope => scope).ToArray();
        Assert.That(missing, Is.Empty, "The browser shows these permissions raw. Add them to scopeName in the browser program: " + string.Join(", ", missing));
    }

    private static void Offer(Site site, string scopes)
    {
        var watches = site.Artifact.GetWatches();
        var watch = Enumerable.Range(0, watches.Length).Single(index => site.Artifact.GetImport(watches[index].EventImportId).Name == "meta.citinet.Browser.offerReceived");
        var offer = site.Process.CreateRecord(site.Process.GetTypeId("meta.citinet.ProgramOffer"),
            [site.Process.CreateString("offer-1"), site.Process.CreateString("Free tool"), site.Process.CreateString("K7Q4MD"), site.Process.CreateString(scopes)]);
        Assert.That(site.Process.TryEnqueueEvent(watch, [offer], 1), Is.True);
        Assert.That(site.Process.TryStartNextEvent(), Is.True);
        Drain(site.Process);
    }

    [Test]
    public void AnOfferedProgramIsDescribedInPlainWordsNotByItsPermissionCodes()
    {
        var site = Boot();
        Offer(site, "door.control,files.write,economy.pay");
        var english = View(site).GetProperty("programText").GetString()!;
        Assert.That(english, Does.Contain("Free tool"));
        Assert.That(english, Does.Contain("Open, close and bolt doors").And.Contain("Write and change the files of this device")
            .And.Contain("Pay from your account when you press a button"));
        Assert.That(english, Does.Not.Contain("door.control"));
        var russian = View(site, "ru-RU").GetProperty("programText").GetString()!;
        Assert.That(russian, Does.Contain("Открывать, закрывать и запирать двери").And.Contain("Платить с вашего счёта"));
        Assert.That(russian, Does.Not.Contain("files.write"));
    }

    [Test]
    public void AnOfferThatAsksForNothingSaysSoAndAnUnknownPermissionIsShownAsItIs()
    {
        var plain = Boot();
        Offer(plain, "");
        Assert.That(View(plain).GetProperty("programText").GetString(), Does.Contain("nothing special"));
        var odd = Boot();
        Offer(odd, "door.control,future.thing");
        Assert.That(View(odd).GetProperty("programText").GetString(), Does.Contain("future.thing"));
    }
}
