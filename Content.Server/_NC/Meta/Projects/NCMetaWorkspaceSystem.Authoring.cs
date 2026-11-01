// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Verification;
using Content.Server._NC.Meta.Applications;
using Content.Server._NC.Meta.Compilation;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.UI;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    private sealed class Draft(Context context, NCMetaAuthoringBeginMessage request, TimeSpan expires) : IDisposable
    {
        public readonly Context Context = context;
        public readonly NCMetaAuthoringBeginMessage Request = request;
        public readonly TimeSpan Expires = expires;
        public readonly MemoryStream[] Sources = request.Files.Select(_ => new MemoryStream()).ToArray();
        public readonly CancellationTokenSource Cancellation = new(TimeSpan.FromSeconds(10));
        public bool Ready, Running;
        public void Dispose()
        {
            Cancellation.Cancel();
            foreach (var source in Sources) source.Dispose();
            // The running worker may still observe the cancellation token.
            if (!Running) Cancellation.Dispose();
        }
    }
    private readonly Dictionary<(EntityUid, EntityUid), Draft> _drafts = [];
    private sealed record DraftProof(Context Context, Guid Id, TimeSpan Until);
    private readonly Dictionary<(EntityUid, EntityUid), DraftProof> _draftProofs = [];

    private void InitializeAuthoring()
    {
        SubscribeLocalEvent<NCMetaTerminalComponent, NCMetaAuthoringBeginMessage>(OnAuthoringBegin);
        SubscribeLocalEvent<NCMetaTerminalComponent, NCMetaAuthoringChunkMessage>(OnAuthoringChunk);
        SubscribeLocalEvent<NCMetaTerminalComponent, NCMetaAuthoringRunMessage>(OnAuthoringRun);
    }

    private void CloseDraft((EntityUid, EntityUid) key)
    {
        if (_drafts.Remove(key, out var draft)) draft.Dispose();
    }
    private void ClearDrafts()
    {
        foreach (var key in _drafts.Keys.ToArray()) CloseDraft(key);
        foreach (var proof in _draftProofs.Values)
            if (_ui.IsUiOpen(proof.Context.Terminal, NCMetaUiKey.Workspace, proof.Context.User))
                _ui.ServerSendUiMessage(proof.Context.Terminal, NCMetaUiKey.Workspace,
                    new NCMetaAuthoringReplyMessage(proof.Id) { Rejected = true, Complete = true }, proof.Context.User);
        _draftProofs.Clear();
    }
    private bool DraftCurrent(Draft draft) => !draft.Cancellation.IsCancellationRequested &&
        draft.Expires > _timing.CurTime && IsCurrent(draft.Context, afterClaim: true) &&
        _drafts.TryGetValue((draft.Context.Terminal, draft.Context.User), out var current) && ReferenceEquals(draft, current);

    private void SendDraft(Draft draft, NCMetaAuthoringReplyMessage reply)
    {
        if (DraftCurrent(draft)) _ui.ServerSendUiMessage(draft.Context.Terminal, NCMetaUiKey.Workspace, reply, draft.Context.User);
    }
    private static bool DraftProjectCurrent(Draft draft, NCMetaProject? project) => project != null &&
        project.Id == draft.Request.Project && project.Revision == draft.Request.Revision &&
        project.Files.Count() == draft.Request.Inventory.Count && project.Files.All(file =>
            draft.Request.Inventory.Any(item => item.FileId == file.Id && item.Path == file.Path &&
                item.Revision == file.Revision && item.Sha256 == file.Digest && item.Utf8Bytes == file.Bytes)) &&
        draft.Request.Files.Where(file => file.Id != Guid.Empty).All(file =>
            project.Files.Any(saved => saved.Id == file.Id && saved.Path == file.Path && saved.Revision == file.Revision));

    private void OnAuthoringBegin(Entity<NCMetaTerminalComponent> ent, ref NCMetaAuthoringBeginMessage args)
        => _ = BeginAuthoringAuthorizedAsync(ent.Owner, args.Actor, args);

    private async Task BeginAuthoringAuthorizedAsync(EntityUid terminal, EntityUid user, NCMetaAuthoringBeginMessage request)
    {
        if (_authorizing.Count >= 64 || !_authorizing.Add((terminal, user))) return;
        try
        {
            if (await _authority.CaptureWorkspaceManagementAsync(terminal, user) == null) return;
            await OnMainAsync(() => { TryBeginAuthoring(terminal, user, request); return true; });
        }
        catch (Exception exception) { Log.Error($"META authoring authorization failed: {exception}"); }
        finally { await OnMainAsync(() => _authorizing.Remove((terminal, user))); }
    }

    private void TryBeginAuthoring(EntityUid terminal, EntityUid user, NCMetaAuthoringBeginMessage args)
    {
        var request = args;
        var key = (terminal, user);
        var context = CaptureWorkspaceContext(terminal, user);
        if (context == null || args.Id == Guid.Empty || args.Project == Guid.Empty || args.Kind is < 0 or > 6 ||
            args.Caret is < 0 or > 256 * 1024 || args.Files is not { Count: > 0 and <= 65 } ||
            args.Inventory is not { Count: > 0 and <= 256 } ||
            args.Inventory.Any(file => file == null || file.Path == null || file.Path.Length > 192 || file.Sha256 is not { Length: 64 }) ||
            args.Files.Any(file => file == null || file.Path is not { Length: > 0 and <= 192 } || file.Hash is not { Length: 64 } ||
                file.Bytes is < 0 or > 256 * 1024 || file.Id == Guid.Empty && (request.Kind != 5 || file.Path != "@preview" || file.Bytes > MuiLimits.InitialStateBytes)) ||
            args.Files.Sum(file => (long) file.Bytes) > 1024 * 1024 ||
            args.Files.Select(file => file.Id).Distinct().Count() != args.Files.Count ||
            args.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != args.Files.Count ||
            args.Inventory.Select(file => file.FileId).Distinct().Count() != args.Inventory.Count ||
            !args.Files.Any(file => file.Id == request.Selected && file.Id != Guid.Empty)) return;
        if (!_requestRates.TryGetValue(user, out var rate))
        {
            if (_requestRates.Count >= 128) return;
            rate = (new Content.Meta.Hosting.MetaRateLimit(12, 6), _timing.CurTime);
        }
        _requestRates[user] = (rate.Rate, _timing.CurTime);
        if (!rate.Rate.TryConsume(_timing.CurTime.Ticks / TimeSpan.TicksPerMillisecond)) return;
        CloseDraft(key);
        _draftProofs.Remove(key);
        _languageTransfers.Remove(key);
        if (_drafts.Count + _languageTransfers.Count >= 16) return;
        var draft = new Draft(context, args, _timing.CurTime + TimeSpan.FromSeconds(10));
        _drafts.Add(key, draft);
        _ = BeginDraftAsync(draft);
    }

    private async Task BeginDraftAsync(Draft draft)
    {
        try
        {
            var device = await _persistence.ReadDeviceAsync(draft.Context.DeviceId);
            var project = device?.Owner == draft.Context.Owner && device.Projects.Contains(draft.Request.Project)
                ? await _persistence.ReadProjectAsync(draft.Request.Project, draft.Context.Owner) : null;
            await OnMainAsync(() =>
            {
                if (!DraftCurrent(draft)) return false;
                draft.Ready = DraftProjectCurrent(draft, project);
                SendDraft(draft, new(draft.Request.Id) { Ready = draft.Ready, Rejected = !draft.Ready });
                if (!draft.Ready) CloseDraft((draft.Context.Terminal, draft.Context.User));
                return true;
            });
        }
        catch (Exception) { await RejectDraftAsync(draft); }
    }

    private void OnAuthoringChunk(Entity<NCMetaTerminalComponent> ent, ref NCMetaAuthoringChunkMessage args)
    {
        if (!_drafts.TryGetValue((ent.Owner, args.Actor), out var draft) || args.Id != draft.Request.Id ||
            !draft.Ready || draft.Running || !DraftCurrent(draft)) return;
        if (args.File < 0 || args.File >= draft.Sources.Length || args.Bytes is not { Length: > 0 and <= 32768 }) return;
        var stream = draft.Sources[args.File];
        if (args.Offset != stream.Length || stream.Length + args.Bytes.Length > draft.Request.Files[args.File].Bytes) return;
        stream.Write(args.Bytes);
        SendDraft(draft, new(args.Id) { File = args.File, Offset = (int) stream.Length });
    }

    private void OnAuthoringRun(Entity<NCMetaTerminalComponent> ent, ref NCMetaAuthoringRunMessage args)
    {
        var key = (ent.Owner, args.Actor);
        if (!_drafts.TryGetValue(key, out var draft) || args.Id != draft.Request.Id) return;
        if (args.Cancel) { CloseDraft(key); return; }
        if (!draft.Ready || draft.Running || !DraftCurrent(draft) ||
            draft.Sources.Where((source, index) => source.Length != draft.Request.Files[index].Bytes).Any()) return;
        draft.Running = true;
        // No network or world work runs on the compiler's thread.
        var bytes = draft.Sources.Select(source => source.ToArray()).ToArray();
        _ = RunDraftAsync(draft, bytes);
    }

    private async Task RejectDraftAsync(Draft draft) => await OnMainAsync(() =>
    {
        SendDraft(draft, new(draft.Request.Id) { Complete = true, Rejected = true, Code = "META0001" });
        if (_drafts.TryGetValue((draft.Context.Terminal, draft.Context.User), out var current) && ReferenceEquals(current, draft))
            CloseDraft((draft.Context.Terminal, draft.Context.User));
        return true;
    });

    private async Task RunDraftAsync(Draft draft, byte[][] bytes)
    {
        try
        {
            var sources = await Task.Run(() => bytes.Select((source, index) =>
            {
                draft.Cancellation.Token.ThrowIfCancellationRequested();
                if (Convert.ToHexStringLower(SHA256.HashData(source)) != draft.Request.Files[index].Hash) throw new ArgumentException();
                var text = Utf8.GetString(source);
                if (draft.Request.Files[index].Id != Guid.Empty && new SourceText(draft.Request.Files[index].Path, text).Text != text) throw new ArgumentException();
                return new MetaSourceFile(draft.Request.Files[index].Path, text);
            }).ToArray());
            var project = await _persistence.ReadProjectAsync(draft.Request.Project, draft.Context.Owner);
            if (!DraftProjectCurrent(draft, project)) { await RejectDraftAsync(draft); return; }
            var selected = draft.Request.Files.Single(file => file.Id == draft.Request.Selected);
            var reply = new NCMetaAuthoringReplyMessage(draft.Request.Id) { Complete = true };
            NCMetaDependencyRef[] dependencies = [];
            if (draft.Request.Kind <= 3)
            {
                var code = sources.Where(source => source.FileId.EndsWith(".meta", StringComparison.Ordinal)).ToArray();
                var package = await ResolveCompilationPackageAsync(draft.Context, project!, MetaCompiler.InferRootModule(code.First()) ?? "program.main");
                if (package == null) { await RejectDraftAsync(draft); return; }
                dependencies = package.Dependencies;
                if (!await _languageHeaderBuilds.WaitAsync(0, draft.Cancellation.Token)) { await RejectDraftAsync(draft); return; }
                MetaSemanticLibraryHeader[] headers;
                try
                {
                    headers = await Task.Run(() => package.Libraries.Select(library => MetaSemanticHeaders.Project(
                        MetaVerifier.Verify(library.Artifact, MetaApiCatalog.Default.CreateVerificationCatalog(
                            Content.Meta.Bytecode.MetaBytecodeLoader.Load(library.Artifact))), library.Exports)).ToArray());
                    if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(headers).Length > MetaSemanticHeaders.MaximumBytes)
                        throw new ArgumentException("Public semantic environment exceeds policy.");
                }
                finally { _languageHeaderBuilds.Release(); }
                var worker = MetaCompilerWorkerPool.ResolvePublishedWorkerPath(AppContext.BaseDirectory);
                var compiler = await OnMainAsync(() => File.Exists(worker) && DraftCurrent(draft)
                    ? _compiler ??= new MetaCompilerWorkerPool(worker) : null);
                var result = compiler == null ? null : await compiler.AnalyzeAsync(draft.Context.Owner,
                    new(code, package.RootModule, package.Name, package.Version, Library: package.Library),
                    new(draft.Request.Kind, selected.Path, selected.Hash, draft.Request.Caret, headers), draft.Cancellation.Token);
                if (result == null) { await RejectDraftAsync(draft); return; }
                reply.Signature = result.Signature;
                reply.Items = result.Items.Select(item => new NCMetaAuthoringItem(item.Name, item.Signature,
                    item.Location?.FileId ?? "", item.Location?.Revision ?? "", item.Location?.StartUtf8Byte ?? 0)).ToList();
                reply.Diagnostics = result.Diagnostics.Select(diagnostic => new NCMetaDiagnostic(
                    draft.Request.Files.First(file => file.Path == diagnostic.FileId).Id,
                    draft.Request.Files.First(file => file.Path == diagnostic.FileId).Revision,
                    diagnostic.Code, diagnostic.Code, diagnostic.StartUtf8Byte, diagnostic.EndUtf8Byte - diagnostic.StartUtf8Byte)).ToList();
            }
            else
            {
                var release = project!.Releases.LastOrDefault();
                var artifact = release == null ? null : await ReadVerifiedAsync(release);
                var app = artifact == null ? null : (await ReadApplicationAsync(release!, artifact)).Application;
                if (app == null) { await RejectDraftAsync(draft); return; }
                if (!await _languageHeaderBuilds.WaitAsync(0, draft.Cancellation.Token)) { await RejectDraftAsync(draft); return; }
                try { reply = await Task.Run(() => BuildDraftPreview(draft.Request, sources, app.Contract)); }
                finally { _languageHeaderBuilds.Release(); }
            }
            var current = await _persistence.ReadProjectAsync(draft.Request.Project, draft.Context.Owner);
            if (!DraftProjectCurrent(draft, current) || !await RevalidateBuildDependenciesAsync(draft.Context, dependencies))
            { await RejectDraftAsync(draft); return; }
            await OnMainAsync(() =>
            {
                SendDraft(draft, reply);
                var key = (draft.Context.Terminal, draft.Context.User);
                if (DraftCurrent(draft) && !reply.Rejected && (_draftProofs.ContainsKey(key) || _draftProofs.Count < 16))
                    _draftProofs[key] = new(draft.Context, draft.Request.Id, _timing.CurTime + TimeSpan.FromSeconds(5));
                if (_drafts.TryGetValue((draft.Context.Terminal, draft.Context.User), out var live) && ReferenceEquals(live, draft))
                    CloseDraft((draft.Context.Terminal, draft.Context.User));
                return true;
            });
        }
        catch (Exception) { await RejectDraftAsync(draft); }
        finally { draft.Cancellation.Dispose(); }
    }

    private static NCMetaAuthoringReplyMessage BuildDraftPreview(NCMetaAuthoringBeginMessage request, MetaSourceFile[] sources, MuiAppContract contract)
    {
        var result = new NCMetaAuthoringReplyMessage(request.Id) { Complete = true };
        var selected = request.Files.Single(file => file.Id == request.Selected);
        var source = sources.Single(file => file.FileId == selected.Path).Text;
        var view = contract.Views.FirstOrDefault(pair => pair.Value.Document == selected.Path);
        if (request.Kind == 6)
        {
            IEnumerable<string> available = selected.Path.EndsWith(".mui", StringComparison.Ordinal)
                ? Enum.GetNames<MuiControlKind>().Concat(new[] { "id", "class", "text", "value", "visible", "enabled", "action", "field", "resource", "items", "width", "height", "columns", "gap", "pageAction", "sortAction", "filterAction", "rowAction", "rowKey", "rowField" }).Concat(contract.Actions.Keys).Concat(contract.Views.Values.SelectMany(value => value.Fields.Keys))
                : new[] { "version", "tokens", "rules", "selector", "state", "minWidth", "maxWidth", "properties", "normal", "hover", "focus", "disabled", "pressed", "color", "background", "gradient", "padding", "margin", "gap", "borderColor", "borderWidth", "cornerRadius", "font", "fontSize", "image", "opacity", "animation" };
            var text = new SourceText(selected.Path, source);
            var caret = text.Utf16Offset(request.Caret);
            var start = caret;
            while (start > 0 && (char.IsAsciiLetterOrDigit(source[start - 1]) || source[start - 1] == '_')) start--;
            var prefix = source[start..caret];
            result.Items = available.Distinct(StringComparer.Ordinal).Where(value => value.StartsWith(prefix, StringComparison.Ordinal) && value != prefix)
                .Take(64).Select(value => new NCMetaAuthoringItem(value, value, "", "", 0)).ToList();
            return result;
        }
        var styleSource = sources.FirstOrDefault(file => file.FileId.EndsWith(".mstyle", StringComparison.Ordinal))?.Text;
        var styles = MuiStyleSheet.Empty;
        if (styleSource != null && !MuiStyleSheet.TryParse(styleSource, contract, out styles, out var styleError))
        { result.Code = styleError; result.Rejected = true; return result; }
        if (selected.Path.EndsWith(".mstyle", StringComparison.Ordinal)) return result;
        var resources = sources.Where(file => file.FileId.EndsWith(".mui", StringComparison.Ordinal)).ToDictionary(file => file.FileId, file => file.Text);
        var viewContract = view.Value ?? (request.Kind == 4 ? contract.Views.Values.FirstOrDefault() : null);
        if (viewContract == null) { result.Code = "MUI1301"; result.Rejected = true; return result; }
        if (!MuiParser.TryParse(source, viewContract, contract, resources, out var document, out var diagnostic))
        { result.Code = diagnostic?.Code ?? "MUI1301"; result.Rejected = true; return result; }
        if (!styles!.FitsBudget(document!)) { result.Code = "MUI2002"; result.Rejected = true; return result; }
        if (request.Kind == 5)
        {
            var mock = sources.Single(file => file.FileId == "@preview").Text;
            result.Presentation = NCMetaMuiPresentationBuilder.Build(contract, document!, styles);
            result.ViewId = view.Key;
            result.Values = NCMetaMuiPresentationBuilder.ReadValues(mock);
        }
        return result;
    }
}
