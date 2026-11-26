// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

/// <summary>The stock forum site written in META: threads, replies, a moderator, bans, and what survives a restart.</summary>
[TestFixture]
public sealed class MetaForumTests
{
    private static string Folder
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Resources", "Meta", "Forum"))) directory = directory.Parent;
            return Path.Combine(directory!.FullName, "Resources", "Meta", "Forum");
        }
    }

    private sealed class Harness
    {
        public readonly MuiVerifiedApplication App;
        public readonly MetaProcess Process;
        public readonly Host Host;
        public Harness(string saved = "")
        {
            Host = new(saved);
            var request = new MetaCompilationRequest([new("main.meta", File.ReadAllText(Path.Combine(Folder, "main.meta")))], "citinet.forum");
            var compiler = new MetaCompiler(); var compiled = compiler.Compile(request);
            Assert.That(compiled.Success, Is.True, string.Join(';', compiled.Diagnostics.Select(value => value.Code + ":" + string.Join(',', value.Arguments))));
            var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
            var contract = File.ReadAllText(Path.Combine(Folder, "app.json"));
            var documents = new Dictionary<string, string> { ["ui/main.mui"] = File.ReadAllText(Path.Combine(Folder, "main.mui")) };
            var style = File.ReadAllText(Path.Combine(Folder, "style.mstyle"));
            var digest = MuiVerifiedApplication.ComputePackageDigest(artifact.Digest, contract, documents, style);
            Assert.That(MuiVerifiedApplication.TryAdmit(artifact, compiler.VerifyExports(request, artifact), contract, documents, style, digest,
                out var app, out var error), Is.True, error);
            App = app!; Process = new(artifact, Host, new() { RetainForInvocations = true });
            for (var link = 0; link < artifact.GetLinks().Length; link++) Process.TryBindLink(link, (ulong) link + 1);
            Process.SetClock(1, 60, 1000.0 / 60);
            Assert.That(Process.TryStartMain(), Is.True); Drain();
        }
        public void Drain()
        {
            for (var slice = 0; slice < 1000 && Process.State is MetaProcessState.Runnable or MetaProcessState.Collecting; slice++) Process.RunSlice(512, 1000);
            Assert.That(Process.Fault, Is.Null); Assert.That(Process.State, Is.EqualTo(MetaProcessState.Idle));
        }
        private readonly Dictionary<ulong, (MetaValue User, MetaValue Session)> _refs = [];
        // The host makes one session reference per open session and hands the same one to every call, as the server does.
        public MetaValue Context(ulong session, string locale)
        {
            if (!_refs.TryGetValue(session, out var refs))
                _refs[session] = refs = (Process.CreateOpaque(App.UserRefTypeId, session), Process.CreateOpaque(App.SessionRefTypeId, session));
            return Process.CreateRecord(App.ContextTypeId,
                [refs.User, refs.Session, Process.CreateString("main"), Process.CreateString("desktop"), Process.CreateString(locale)]);
        }
        public JsonDocument View(ulong session = 1, string locale = "en-US")
        {
            Assert.That(Process.TryStart(App.Views["main"].FunctionId, [Context(session, locale)]), Is.True); Drain();
            Assert.That(MuiDtoCodec.TryProject(Process, App.Artifact, App.Views["main"].StateTypeId, Process.LastResult!.Value,
                App.Contract.Views["main"].Fields, out var json), Is.True);
            return JsonDocument.Parse(json);
        }
        public void Action(ulong session, string name, string input)
        {
            var action = App.Actions[name];
            var value = MuiDtoCodec.DecodeInput(Process, App.Artifact, action.InputTypeId, input, App.Contract.Actions[name].Inputs);
            Host.Device = "DEV" + session.ToString("D3");
            Assert.That(Process.TryStart(action.FunctionId, [Context(session, "en-US"), value]), Is.True); Drain();
        }
    }

    private sealed class Host(string saved) : IMetaHost
    {
        public string Saved = saved;
        public string Device = "DEV001";
        public int Writes;
        public MetaValue Read(MetaProcess process, MetaHostRequest request)
        {
            var import = process.GetImport(request.ImportId);
            if (import.Name != "meta.citinet.Gateway.visitorDevice") throw new InvalidOperationException("Unexpected forum read " + import.Name);
            return process.CreateResult(import.ReturnType!.Value, process.CreateString(Device));
        }
        public MetaHostResult BeginAwait(MetaProcess process, MetaHostRequest request)
        {
            var import = process.GetImport(request.ImportId);
            MetaValue result;
            if (import.Name == "meta.files.FileStore.readText") result = process.CreateString(Saved);
            else if (import.Name == "meta.files.FileStore.writeTextAtomic") { Saved = process.GetString(request.Arguments[1]); Writes++; result = MetaValue.Unit; }
            else throw new InvalidOperationException("Unexpected forum command " + import.Name);
            return MetaHostResult.Completed(process.CreateResult(import.ReturnType!.Value, result));
        }
        public void Log(MetaProcess process, string text) { }
        public void Cancel(MetaProcess process, long operationId) { }
    }

    private static string[] Labels(JsonDocument view, string name) =>
        view.RootElement.GetProperty(name).EnumerateArray().Select(row => row.GetProperty("label").GetString()!).ToArray();

    private static string Thread(string handle, string title, string text) =>
        JsonSerializer.Serialize(new { handle, title, text });

    [Test]
    public void TheShippedPackageNamesTheRealFilesAndTheCatalogListsIt()
    {
        var root = Path.GetFullPath(Path.Combine(Folder, "..", "..", ".."));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Resources", "Meta", "Projects", "meta-stock-citinet-forum", "manifest.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var source = new SourceText(file.GetProperty("path").GetString()!, File.ReadAllText(Path.Combine(root, file.GetProperty("sourcePath").GetString()!)));
            Assert.That(source.Revision, Is.EqualTo(file.GetProperty("sha256").GetString()), file.GetProperty("path").GetString());
        }
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Resources", "Meta", "Projects", "catalog.json")));
        Assert.That(catalog.RootElement.GetProperty("projects").EnumerateArray().Select(project => project.GetProperty("id").GetString()),
            Does.Contain("meta-stock-citinet-forum"));
    }

    [Test]
    public void ForumCompilesAndStartsWithAnEmptyListAndNoThreadOpen()
    {
        var forum = new Harness(); using var view = forum.View();
        Assert.That(view.RootElement.GetProperty("listVisible").GetBoolean(), Is.True);
        Assert.That(view.RootElement.GetProperty("threadVisible").GetBoolean(), Is.False);
        Assert.That(Labels(view, "threads"), Is.Empty);
    }

    [Test]
    public void ForumTitleFollowsTheViewerLanguage()
    {
        var forum = new Harness();
        using var ru = forum.View(1, "ru-RU"); Assert.That(ru.RootElement.GetProperty("title").GetString(), Is.EqualTo("Форум СитиНет"));
        using var en = forum.View(1, "en-US"); Assert.That(en.RootElement.GetProperty("title").GetString(), Is.EqualTo("CitiNet Forum"));
    }

    [Test]
    public void AVisitorStartsAThreadRepliesAndTheOtherVisitorSeesItWhileKeepingTheirOwnPage()
    {
        var forum = new Harness();
        forum.Action(1, "newThread", Thread("neo", "Hello city", "First post"));
        forum.Action(1, "reply", "{\"handle\":\"neo\",\"text\":\"Second\"}");
        using (var mine = forum.View(1))
        {
            Assert.That(mine.RootElement.GetProperty("threadVisible").GetBoolean(), Is.True);
            Assert.That(mine.RootElement.GetProperty("threadHeading").GetString(), Is.EqualTo("Hello city"));
            Assert.That(Labels(mine, "posts"), Is.EqualTo(new[] { "1. neo: First post", "2. neo: Second" }));
        }
        using (var other = forum.View(2))
        {
            Assert.That(other.RootElement.GetProperty("listVisible").GetBoolean(), Is.True);
            Assert.That(Labels(other, "threads"), Is.EqualTo(new[] { "Hello city  -  neo  (1)" }));
        }
        Assert.That(forum.Host.Saved, Does.Contain("T|").And.Contain("neo|DEV001|First post"));
    }

    [Test]
    public void PostsAreKeptInOneLineEachAndHandlesAreCutAndDefaulted()
    {
        var forum = new Harness();
        forum.Action(1, "newThread", Thread("", "Pipes | bars", "line one\nline two"));
        using var view = forum.View(1);
        Assert.That(Labels(view, "posts"), Is.EqualTo(new[] { "1. anonymous: line one line two" }));
        Assert.That(forum.Host.Saved.Split('\n').Count(line => line.StartsWith("P|")), Is.EqualTo(1));
        Assert.That(forum.Host.Saved, Does.Contain("T|").And.Contain("Pipes   bars"));
    }

    [Test]
    public void ARestartedForumReadsItsThreadsBack()
    {
        var first = new Harness();
        first.Action(1, "newThread", Thread("neo", "Saved thread", "Body"));
        var second = new Harness(first.Host.Saved);
        using var view = second.View(1);
        Assert.That(Labels(view, "threads"), Is.EqualTo(new[] { "Saved thread  -  neo  (0)" }));
    }

    [Test]
    public void OnlyTheModeratorCanDeleteAndBanAndTheWrongPasswordIsRefused()
    {
        var forum = new Harness();
        forum.Action(1, "newThread", Thread("neo", "Thread", "Keep"));
        forum.Action(1, "reply", "{\"handle\":\"x\",\"text\":\"Spam\"}");
        using (var first = forum.View(1)) Assert.That(Labels(first, "adminPosts"), Is.Empty);
        forum.Action(2, "login", "{\"password\":\"wrong\"}");
        using (var refused = forum.View(2)) Assert.That(refused.RootElement.GetProperty("adminVisible").GetBoolean(), Is.False);
        forum.Action(2, "login", "{\"password\":\"change-me\"}");
        forum.Action(2, "openThread", "{\"thread\":\"1\"}");
        using (var mod = forum.View(2))
        {
            Assert.That(mod.RootElement.GetProperty("adminVisible").GetBoolean(), Is.True);
            Assert.That(Labels(mod, "adminPosts")[1], Does.Contain("[DEV001]").And.Contain("Spam"));
        }
        forum.Action(2, "removePost", "{\"post\":\"3\"}");
        using (var cleaned = forum.View(2)) Assert.That(Labels(cleaned, "posts"), Is.EqualTo(new[] { "1. neo: Keep" }));
        forum.Action(2, "ban", "{\"device\":\"DEV001\"}");
        forum.Action(1, "reply", "{\"handle\":\"x\",\"text\":\"More spam\"}");
        using (var banned = forum.View(1)) Assert.That(Labels(banned, "posts"), Is.EqualTo(new[] { "1. neo: Keep" }));
    }

    [Test]
    public void AVisitorWhoIsNotTheModeratorCannotRemoveAnything()
    {
        var forum = new Harness();
        forum.Action(1, "newThread", Thread("neo", "Thread", "Keep"));
        var action = forum.App.Actions["removePost"];
        var value = MuiDtoCodec.DecodeInput(forum.Process, forum.App.Artifact, action.InputTypeId, "{\"post\":\"2\"}", forum.App.Contract.Actions["removePost"].Inputs);
        Assert.That(forum.Process.TryStart(action.FunctionId, [forum.Context(1, "en-US"), value]), Is.True); forum.Drain();
        using var view = forum.View(1);
        Assert.That(Labels(view, "posts"), Is.EqualTo(new[] { "1. neo: Keep" }));
    }

    [Test]
    public void DeletingTheLastPostOfAThreadRemovesTheThread()
    {
        var forum = new Harness();
        forum.Action(1, "newThread", Thread("neo", "Thread", "Only"));
        forum.Action(1, "login", "{\"password\":\"change-me\"}");
        forum.Action(1, "removePost", "{\"post\":\"2\"}");
        using var view = forum.View(1);
        Assert.That(Labels(view, "threads"), Is.Empty);
        Assert.That(view.RootElement.GetProperty("listVisible").GetBoolean(), Is.True);
    }

    [Test]
    public void TheForumIsBoundedAndTheOldestThreadMakesRoom()
    {
        var forum = new Harness();
        for (var index = 0; index < 22; index++) forum.Action(1, "newThread", Thread("neo", "Thread " + index, "Body"));
        using var view = forum.View(2);
        var labels = Labels(view, "threads");
        Assert.That(labels, Has.Length.EqualTo(20));
        Assert.That(labels[0], Does.StartWith("Thread 21")); Assert.That(labels[^1], Does.StartWith("Thread 2 "));
    }
}
