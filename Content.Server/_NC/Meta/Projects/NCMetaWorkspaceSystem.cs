// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Bytecode;
using Content.Meta.Verification;
using Content.Meta.Testing;
using Content.Meta.Hosting;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Compilation;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server.Database;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Projects;

/// <summary>Authenticated private workspace sessions. Only immutable DTOs cross asynchronous boundaries.</summary>
public sealed partial class NCMetaWorkspaceSystem : EntitySystem
{
    private sealed record Context(EntityUid Terminal, EntityUid User, string DeviceId, string Owner,
        ulong DeviceEpoch, ulong AuthorityEpoch, NCSessionEpoch Epoch, NCActiveWorldTime Time,
        string Actor, NCMetaManagementProof Management);
    private sealed class Upload(Guid id, Guid fileId, long revision, string path, int total, string digest, TimeSpan expires)
    {
        public readonly Guid Id = id;
        public readonly Guid FileId = fileId;
        public readonly long Revision = revision;
        public readonly string Path = path;
        public readonly int Total = total;
        public readonly string Digest = digest;
        public readonly TimeSpan Expires = expires;
        public readonly MemoryStream Bytes = new();
        public Guid LastRequest;
        public int LastOffset;
        public string LastChunk = string.Empty;
        public string? FinalSource;
    }
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NCMetaBuiltinCatalogSystem _catalog = default!;
    [Dependency] private NCMetaAuthoritySystem _authority = default!;
    private readonly HashSet<(EntityUid, EntityUid)> _authorizing = [];
    private readonly HashSet<(EntityUid, EntityUid)> _processing = [];
    private sealed record QueuedRequest(NCMetaWorkspaceRequestMessage Request, NCSessionEpoch Epoch);
    private readonly Dictionary<(EntityUid, EntityUid), Queue<QueuedRequest>> _queuedRequests = [];
    private readonly Dictionary<(EntityUid, EntityUid), Upload> _uploads = [];
    private readonly Dictionary<Guid, (EntityUid Terminal, EntityUid User, TimeSpan Expires)> _leases = [];
    private readonly Dictionary<EntityUid, (MetaRateLimit Rate, TimeSpan Seen)> _requestRates = [];
    private TimeSpan _nextCleanup;
    private MetaCompilerWorkerPool? _compiler;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMetaTerminalComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<NCMetaTerminalComponent, BoundUIClosedEvent>(OnClosed);
        SubscribeLocalEvent<NCMetaTerminalComponent, NCMetaWorkspaceRequestMessage>(OnRequest);
        InitializeNativeTools();
        InitializeLanguageSymbols();
    }

    public override void Shutdown()
    {
        ShutdownLanguageSymbols();
        if (_compiler != null)
            _ = _compiler.DisposeAsync();
        foreach (var upload in _uploads.Values)
            upload.Bytes.Dispose();
        _uploads.Clear();
        _commerceViews.Clear();
        _queuedRequests.Clear();
        _nativeTools.Clear();
        base.Shutdown();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_timing.CurTime < _nextCleanup) return;
        _nextCleanup = _timing.CurTime + TimeSpan.FromSeconds(5);
        foreach (var key in _uploads.Where(pair => pair.Value.Expires <= _timing.CurTime).Select(pair => pair.Key).ToArray())
            if (_uploads.Remove(key, out var upload)) upload.Bytes.Dispose();
        foreach (var key in _leases.Where(pair => pair.Value.Expires <= _timing.CurTime).Select(pair => pair.Key).ToArray())
            _leases.Remove(key);
        foreach (var key in _requestRates.Where(pair => pair.Value.Seen + TimeSpan.FromMinutes(5) <= _timing.CurTime)
                     .Select(pair => pair.Key).ToArray()) _requestRates.Remove(key);
    }

    private void OnOpened(Entity<NCMetaTerminalComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (args.Actor is { } user)
            _ = BeginAuthorizedAsync(ent.Owner, user, new NCMetaWorkspaceRequestMessage(NCMetaWorkspaceOperation.Open, Guid.NewGuid()));
    }

    private void OnClosed(Entity<NCMetaTerminalComponent> ent, ref BoundUIClosedEvent args)
    {
        var actor = args.Actor;
        _queuedRequests.Remove((ent.Owner, actor));
        _commerceViews.Remove((ent.Owner, actor));
        _debug.Remove((ent.Owner, actor));
        if (actor is { } contractUser) _development.CloseFor(ent.Owner, contractUser);
        _nativeTools.Remove((ent.Owner, actor));
        _languageTransfers.Remove((ent.Owner, actor));
        _languageBuildTokens.Remove((ent.Owner, actor));
        if (actor is { } user && _uploads.Remove((ent.Owner, user), out var upload))
            upload.Bytes.Dispose();
        foreach (var file in _leases.Where(pair => pair.Value.Terminal == ent.Owner && pair.Value.User == actor)
                     .Select(pair => pair.Key).ToArray()) _leases.Remove(file);
    }

    private void OnRequest(Entity<NCMetaTerminalComponent> ent, ref NCMetaWorkspaceRequestMessage args) =>
        _ = BeginAuthorizedAsync(ent.Owner, args.Actor, args);

    private async Task BeginAuthorizedAsync(EntityUid terminal, EntityUid user, NCMetaWorkspaceRequestMessage request)
    {
        if (_authorizing.Count >= 64 || !_authorizing.Add((terminal, user))) return;
        try
        {
            if (!_devices.TryGetEntry(terminal, out var device) || !_parties.TryResolve(user, out var actor)) return;
            if (await _authority.CaptureManagementAsync(user, device.Owner.Length == 0 ? actor.ToString() : device.Owner) == null) return;
            await OnMainAsync(() => TryBegin(terminal, user, request));
        }
        catch (Exception exception) { Log.Error($"META workspace authorization failed: {exception}"); }
        finally { await OnMainAsync(() => _authorizing.Remove((terminal, user))); }
    }

    private Context? CaptureWorkspaceContext(EntityUid terminal, EntityUid user, bool allowUnclaimed = false)
    {
        if (!_ui.IsUiOpen(terminal, NCMetaUiKey.Workspace, user) || !_devices.TryGetEntry(terminal, out var device) ||
            !_parties.TryResolve(user, out var actor) || !_persistence.TryCaptureTime(out var epoch, out var time) ||
            !_devices.CanManage(terminal, user) && !(allowUnclaimed && _devices.CanClaim(terminal, user))) return null;
        var owner = device.Owner.Length == 0 ? actor.ToString() : device.Owner;
        return !_authority.TryGetManagementProof(user, owner, out var proof) ? null :
            new(terminal, user, device.Id, owner, device.DeviceEpoch, device.AuthorityEpoch, epoch, time, actor.ToString(), proof);
    }

    private bool TryBegin(EntityUid terminal, EntityUid user, NCMetaWorkspaceRequestMessage request, bool alreadyAdmitted = false)
    {
        if (request.Source == null || request.Path == null || request.ContentSha256 == null || request.DeviceId == null)
            return false;
        try { if (Utf8.GetByteCount(request.Source) > 32 * 1024) return false; }
        catch (EncoderFallbackException) { return false; }
        var context = CaptureWorkspaceContext(terminal, user, allowUnclaimed: true);
        if (request.RequestId == Guid.Empty || context == null)
            return false;
        if (!_requestRates.TryGetValue(user, out var rate))
        {
            if (_requestRates.Count >= 128) return false;
            rate = (new MetaRateLimit(12, 6), _timing.CurTime);
        }
        _requestRates[user] = (rate.Rate, _timing.CurTime);
        if (!alreadyAdmitted && !rate.Rate.TryConsume(_timing.CurTime.Ticks / TimeSpan.TicksPerMillisecond)) return false;
        if (request.Source.Length > 32 * 1024 || request.Path.Length > 192 || request.ContentSha256.Length > 64 ||
            request.DeviceId.Length > 128)
            return false;
        var key = (terminal, user);
        if (_processing.Contains(key))
        {
            if (!_queuedRequests.TryGetValue(key, out var pending)) _queuedRequests.Add(key, pending = new());
            if (pending.Any(queued => queued.Request.RequestId == request.RequestId)) return true;
            if (pending.Count >= 4) return false;
            pending.Enqueue(new(request, context.Epoch));
            return true;
        }
        if (_processing.Count >= 64 || !_processing.Add(key)) return false;
        _ = ProcessAsync(context, request);
        return true;
    }

    private async Task ProcessAsync(Context context, NCMetaWorkspaceRequestMessage request)
    {
        try
        {
            var itemId = await OnMainAsync(() => _devices.GetEconomicItemId(context.Terminal));
            var device = await _persistence.ReadDeviceAsync(context.DeviceId) ??
                await _persistence.ClaimDeviceAsync(request.RequestId, context.DeviceId, context.Owner,
                    Loc.GetString("nc-meta-default-project"), context.Epoch, context.Time, itemId);
            if (device == null || device.Owner != context.Owner)
                return;
            var authorizedEpoch = await OnMainAsync<ulong?>(() =>
            {
                if (!IsCurrent(context, allowUnclaimed: true))
                    return null;
                if (_devices.CanClaim(context.Terminal, context.User) &&
                    !_devices.TryApplyOwner(context.Terminal, context.DeviceId, string.Empty, context.Owner, device.AuthorityEpoch))
                    return null;
                if (_devices.TryGetEntry(context.Terminal, out var attested) && attested.Owner == context.Owner &&
                    attested.AuthorityEpoch != device.AuthorityEpoch &&
                    !_devices.TryApplyOwner(context.Terminal, context.DeviceId, context.Owner, context.Owner, device.AuthorityEpoch))
                    return null;
                return _devices.CanManage(context.Terminal, context.User) &&
                       _devices.TryGetEntry(context.Terminal, out var current) ? current.AuthorityEpoch : null;
            });
            if (authorizedEpoch == null)
                return;
            context = context with { AuthorityEpoch = authorizedEpoch.Value };
            device = await _persistence.EnsureWorkspaceAsync(request.RequestId, device,
                Loc.GetString("nc-meta-default-project"), context.Epoch, context.Time, WorkspaceAuthority(context));
            if (device == null) return;
            var projectId = request.ProjectId == Guid.Empty ? device.ActiveProject : request.ProjectId;
            if (!device.Projects.Contains(projectId))
                return;
            var project = await _persistence.ReadProjectAsync(projectId, context.Owner);
            if (project == null)
                return;
            var state = new NCMetaWorkspaceState { AcknowledgedRequestId = request.RequestId, CanEdit = true };
            switch (request.Operation)
            {
                case NCMetaWorkspaceOperation.ReadLanguageSymbols:
                    await LanguageSymbolsAsync(context, project, request);
                    return;
                case NCMetaWorkspaceOperation.OpenNativeTools:
                    await OnMainAsync(() => { SelectNativeTools(context, request); return true; });
                    break;
                case NCMetaWorkspaceOperation.PrepareStockApplication:
                    if (await PrepareStockApplicationAsync(context, device, request, state) is { } stockProject)
                    {
                        project = stockProject;
                        projectId = stockProject.Id;
                        device = await _persistence.ReadDeviceAsync(context.DeviceId) ?? device;
                    }
                    break;
                case NCMetaWorkspaceOperation.BrowseSoftware:
                case NCMetaWorkspaceOperation.ReadSoftwareProduct:
                case NCMetaWorkspaceOperation.QuoteSoftware:
                case NCMetaWorkspaceOperation.PurchaseSoftware:
                case NCMetaWorkspaceOperation.PublishSoftware:
                case NCMetaWorkspaceOperation.InstallLicensedSoftware:
                case NCMetaWorkspaceOperation.AssignSoftwareSeat:
                case NCMetaWorkspaceOperation.ReleaseSoftwareSeat:
                case NCMetaWorkspaceOperation.ReadSoftwareSource:
                case NCMetaWorkspaceOperation.SearchSoftwareContacts:
                case NCMetaWorkspaceOperation.OfferSoftwareResale:
                case NCMetaWorkspaceOperation.AcceptSoftwareResale:
                case NCMetaWorkspaceOperation.DiscardSoftwareCopy:
                    await CommerceRequestAsync(context, project, request, state);
                    break;
                case NCMetaWorkspaceOperation.PreviewFirmware:
                case NCMetaWorkspaceOperation.InstallFirmware:
                    await FirmwareRequestAsync(context, project, request, state);
                    break;
                case NCMetaWorkspaceOperation.ReadServiceGrants:
                case NCMetaWorkspaceOperation.IssueServiceGrants:
                case NCMetaWorkspaceOperation.RevokeServiceGrant:
                    await ServiceGrantRequestAsync(context, project, request, state);
                    break;
                case NCMetaWorkspaceOperation.DevelopmentContract:
                    if (await DevelopmentRequestAsync(context, request, state))
                    {
                        device = await _persistence.ReadDeviceAsync(context.DeviceId) ?? device;
                        projectId = device.ActiveProject;
                    }
                    break;
                case NCMetaWorkspaceOperation.ExportMedia:
                case NCMetaWorkspaceOperation.ImportMedia:
                    var transferred = await TransferMediaAsync(context, device, project, request);
                    state.Notice = Loc.GetString(transferred.MessageKey);
                    device = await _persistence.ReadDeviceAsync(context.DeviceId) ?? device;
                    projectId = device.ActiveProject;
                    break;
                case NCMetaWorkspaceOperation.ImportTemplate:
                    var template = await OnMainAsync(() => _catalog.TryGet(request.Source, out var found) ? found : null);
                    if (template == null) { state.Notice = Loc.GetString("nc-meta-error-input"); break; }
                    var cloned = await _persistence.CloneProjectAsync(request.RequestId, device, template.Name, template.Files,
                        context.Epoch, context.Time, authority: WorkspaceAuthority(context));
                    state.Notice = Loc.GetString(cloned.MessageKey);
                    device = await _persistence.ReadDeviceAsync(context.DeviceId) ?? device;
                    projectId = device.ActiveProject;
                    break;
                case NCMetaWorkspaceOperation.Save:
                case NCMetaWorkspaceOperation.CreateFile:
                    await SaveAsync(context, project, request, state, request.Source);
                    break;
                case NCMetaWorkspaceOperation.SaveChunk:
                    var assembled = await OnMainAsync(() => AcceptChunk(context, request));
                    if (!assembled.Accepted)
                    {
                        state.SaveStatus = NCMetaSaveStatus.Rejected;
                        state.Notice = Loc.GetString("nc-meta-error-input");
                    }
                    else if (assembled.Source != null)
                        await SaveAsync(context, project, request, state, assembled.Source);
                    else
                        state.SaveStatus = NCMetaSaveStatus.ChunkAccepted;
                    break;
                case NCMetaWorkspaceOperation.Compile:
                case NCMetaWorkspaceOperation.Test:
                    await CompileAsync(context, project, request, state);
                    break;
                case NCMetaWorkspaceOperation.Install:
                case NCMetaWorkspaceOperation.Start:
                case NCMetaWorkspaceOperation.Stop:
                case NCMetaWorkspaceOperation.Rollback:
                case NCMetaWorkspaceOperation.Logs:
                case NCMetaWorkspaceOperation.OpenApplication:
                case NCMetaWorkspaceOperation.PublishWebsite:
                case NCMetaWorkspaceOperation.WithdrawWebsite:
                case NCMetaWorkspaceOperation.EnableHostingOffer:
                case NCMetaWorkspaceOperation.QuoteHosting:
                case NCMetaWorkspaceOperation.PurchaseHosting:
                case NCMetaWorkspaceOperation.RefundHosting:
                case NCMetaWorkspaceOperation.DebugAttach:
                case NCMetaWorkspaceOperation.DebugDetach:
                case NCMetaWorkspaceOperation.DebugPause:
                case NCMetaWorkspaceOperation.DebugContinue:
                case NCMetaWorkspaceOperation.DebugStepInto:
                case NCMetaWorkspaceOperation.DebugStepOver:
                case NCMetaWorkspaceOperation.DebugStepOut:
                case NCMetaWorkspaceOperation.DebugSetBreakpoint:
                case NCMetaWorkspaceOperation.DebugEvaluate:
                    await RuntimeRequestAsync(context, project, request, state);
                    break;
                case NCMetaWorkspaceOperation.CreateProject:
                case NCMetaWorkspaceOperation.SelectProject:
                case NCMetaWorkspaceOperation.RenameProject:
                case NCMetaWorkspaceOperation.DeleteProject:
                case NCMetaWorkspaceOperation.RenameFile:
                case NCMetaWorkspaceOperation.DeleteFile:
                    var managed = await _persistence.ManageProjectAsync(request.Operation, request.RequestId, device,
                        project, request.FileId, request.BaseRevision,
                        request.Operation is NCMetaWorkspaceOperation.RenameFile ? request.Path : request.Source,
                        context.Epoch, context.Time, WorkspaceAuthority(context));
                    state.Notice = Loc.GetString(managed.MessageKey);
                    device = await _persistence.ReadDeviceAsync(context.DeviceId) ?? device;
                    projectId = device.ActiveProject;
                    break;
                case NCMetaWorkspaceOperation.Open:
                case NCMetaWorkspaceOperation.Read:
                    break;
                default:
                    state.Notice = Loc.GetString("nc-meta-error-action");
                    break;
            }
            project = await _persistence.ReadProjectAsync(projectId, context.Owner) ?? project;
            state.ProjectId = project.Id;
            state.ProjectName = project.Name;
            state.ProjectRevision = project.Revision;
            state.Files = project.Files.Select(file => new NCMetaFileSummary(file.Id, file.Path, file.Revision,
                file.Bytes, file.Digest, true)).ToList();
            foreach (var id in device.Projects)
            {
                if (await _persistence.ReadProjectAsync(id, context.Owner) is { } listed)
                    state.Projects.Add(new NCMetaProjectSummary(id, listed.Name, listed.Revision));
            }
            if (project.Releases.LastOrDefault() is { } release)
            {
                state.ActiveReleaseId = release.Id;
                state.ActiveReleaseDigest = release.Digest;
                await PopulateReleaseAsync(release, state);
            }
            var nativeTools = await OnMainAsync(() => _nativeTools.ContainsKey((context.Terminal, context.User)));
            if (!nativeTools && (request.Operation is NCMetaWorkspaceOperation.Read or NCMetaWorkspaceOperation.Open || state.RevisionConflict))
            {
                var file = project.Files.FirstOrDefault(value => value.Id == request.FileId) ?? project.Files.First();
                var source = await _persistence.ReadSourceAsync(project, file.Id);
                if (source != null)
                {
                    var text = new SourceText(file.Id.ToString("D"), source);
                    var start = request.Operation == NCMetaWorkspaceOperation.Open || state.RevisionConflict ? 0 : request.Utf8Offset;
                    var count = Math.Clamp(request.ChunkBytes, 1, 32 * 1024);
                    if (text.IsScalarBoundary(start))
                    {
                        var end = Math.Min(text.Utf8.Length, start + count);
                        while (!text.IsScalarBoundary(end)) end--;
                        state.Contents.Add(new NCMetaFileContent(file.Id, file.Revision,
                            Utf8.GetString(text.Utf8.Span[start..end]), file.Digest, start, end == text.Utf8.Length, text.Utf8.Length));
                    }
                }
            }
            var responseDevice = request.DeviceId.Length == 0 ? context.DeviceId : request.DeviceId;
            foreach (var installation in await _persistence.ReadInstallationsAsync(responseDevice, context.Owner))
                state.Installations.Add(new(installation.Id, installation.Active.Id, responseDevice,
                    installation.PackageIdentity, installation.SlotKind, installation.AutoStart));
            var responseInstallation = await SelectInstallationAsync(responseDevice, context.Owner, project.Id, request.ReleaseId, request.InstallationId);
            await OnMainAsync(() =>
            {
                if (!IsCurrent(context, afterClaim: true)) return false;
                PopulateDebug(context, state);
                state.Templates = _catalog.GetSummaries().ToList();
                var selectedDevice = request.DeviceId.Length == 0 ? context.DeviceId : request.DeviceId;
                if (_devices.TryResolve(selectedDevice, out var appHost) && appHost.Owner == context.Owner &&
                    responseInstallation != null && _runtime.TryGetInstallation(appHost.Entity, context.Owner, responseInstallation.Id, out var live) &&
                    _applications.TryGetRegistration(appHost.Entity, live.Generation, out var application))
                    state.ApplicationViews = application.Application.Views.Keys.Order(StringComparer.Ordinal).ToList();
                state.Devices = _devices.GetOwned(context.Owner).Take(128).Select(entry => new NCMetaDeviceSummary(
                    entry.Id, Name(entry.Entity), TryComp<NCMetaHostComponent>(entry.Entity, out var host)
                        ? host.Profile.Id : string.Empty, Loc.GetString("nc-meta-ready"), true,
                    TryComp<NCMetaDeviceComponent>(entry.Entity, out var endpoint) ? endpoint.Interfaces.ToList() :
                        HasComp<NCMetaStorageComponent>(entry.Entity) ? ["meta.files.FileStore"] : [])).ToList();
                PopulateNativeTools(context, state);
                _ui.ServerSendUiMessage(context.Terminal, NCMetaUiKey.Workspace,
                    new NCMetaWorkspaceUpdateMessage(state), context.User);
                return true;
            });
        }
        catch (Exception exception)
        {
            if (exception is not NCMetaBlobQuotaException) Log.Error($"META workspace operation failed: {exception}");
            await OnMainAsync(() =>
            {
                if (_ui.IsUiOpen(context.Terminal, NCMetaUiKey.Workspace, context.User))
                    _ui.ServerSendUiMessage(context.Terminal, NCMetaUiKey.Workspace,
                        new NCMetaWorkspaceUpdateMessage(new NCMetaWorkspaceState
                        {
                            Partial = true,
                            SaveStatus = request.Operation is NCMetaWorkspaceOperation.Save or NCMetaWorkspaceOperation.SaveChunk
                                ? NCMetaSaveStatus.Rejected : NCMetaSaveStatus.None,
                            AcknowledgedRequestId = request.RequestId,
                            Notice = Loc.GetString(exception is NCMetaBlobQuotaException ? "nc-meta-error-quota" : "nc-meta-error-storage"),
                        }), context.User);
                return false;
            });
        }
        finally
        {
            await OnMainAsync(() =>
            {
                var key = (context.Terminal, context.User);
                _processing.Remove(key);
                if (!_queuedRequests.TryGetValue(key, out var pending)) return true;
                while (pending.TryDequeue(out var queued))
                {
                    if (!_persistence.TryCaptureTime(out var currentEpoch, out _) || currentEpoch != queued.Epoch) continue;
                    if (pending.Count == 0) _queuedRequests.Remove(key);
                    if (TryBegin(context.Terminal, context.User, queued.Request, alreadyAdmitted: true)) return true;
                }
                _queuedRequests.Remove(key);
                return true;
            });
        }
    }

    private async Task SaveAsync(Context context, NCMetaProject project, NCMetaWorkspaceRequestMessage request,
        NCMetaWorkspaceState state, string source)
    {
        state.SaveStatus = NCMetaSaveStatus.Rejected;
        var fileId = request.Operation == NCMetaWorkspaceOperation.CreateFile || request.FileId == Guid.Empty
            ? NCMetaPersistenceSystem.StableGuid("file-create:" + request.RequestId) : request.FileId;
        var file = project.Files.FirstOrDefault(value => value.Id == fileId);
        if (!await OnMainAsync(() => TryAcquireLease(context, fileId)))
        { state.Notice = Loc.GetString("nc-meta-error-lease"); return; }
        var result = await _persistence.SaveSourceAsync(request.RequestId, project.Id,
            fileId, context.Owner, request.Operation == NCMetaWorkspaceOperation.CreateFile ? 0 : request.BaseRevision,
            request.Path.Length == 0 ? file?.Path ?? "src/new.meta" : request.Path, source, context.Epoch, context.Time, WorkspaceAuthority(context));
        state.Notice = Loc.GetString(result.MessageKey);
        state.RevisionConflict = result.MessageKey == "nc-meta-error-revision";
        state.SaveStatus = result.Success ? NCMetaSaveStatus.Committed : NCMetaSaveStatus.Rejected;
    }

    private async Task CompileAsync(Context context, NCMetaProject project, NCMetaWorkspaceRequestMessage request,
        NCMetaWorkspaceState state)
    {
        var sources = new List<MetaSourceFile>();
        foreach (var file in project.Files.Where(file => file.Path.EndsWith(".meta", StringComparison.Ordinal)))
        {
            var source = await _persistence.ReadSourceAsync(project, file.Id);
            if (source == null) { state.Notice = Loc.GetString("nc-meta-error-storage"); return; }
            sources.Add(new MetaSourceFile(file.Id.ToString("D"), source));
        }
        if (sources.Count == 0) { state.Notice = Loc.GetString("nc-meta-error-input"); return; }
        var worker = MetaCompilerWorkerPool.ResolvePublishedWorkerPath(AppContext.BaseDirectory);
        if (!File.Exists(worker)) { state.Notice = Loc.GetString("nc-meta-error-worker"); return; }
        var compiler = await OnMainAsync(() => _compiler ??= new MetaCompilerWorkerPool(worker));
        // The worker remains authoritative; this lightweight root declaration selection grants no capability.
        var package = await ResolveCompilationPackageAsync(context, project, MetaCompiler.InferRootModule(sources[0]) ?? "program.main");
        if (package == null) { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        var rootModule = package.RootModule;
        var compilation = await compiler.CompileAsync(context.Owner,
            new MetaCompilationRequest(sources, rootModule, package.Name, package.Version,
                BinaryDependencies: package.Libraries, Library: package.Library));
        foreach (var diagnostic in compilation.Diagnostics)
        {
            if (Guid.TryParse(diagnostic.FileId, out var id) && project.Files.FirstOrDefault(file => file.Id == id) is { } file)
                state.Diagnostics.Add(new NCMetaDiagnostic(id, file.Revision, diagnostic.Code,
                    Loc.GetString("nc-meta-compiler-diagnostic", ("code", diagnostic.Code)),
                    diagnostic.StartUtf8Byte, diagnostic.EndUtf8Byte - diagnostic.StartUtf8Byte));
        }
        if (compilation.Artifact == null)
            return;
        if (request.Operation == NCMetaWorkspaceOperation.Test)
        {
            var fixtureFile = project.Files.FirstOrDefault(file => file.Path == "meta.test.json");
            var fixtureText = fixtureFile == null ? null : await _persistence.ReadSourceAsync(project, fixtureFile.Id);
            try
            {
                var fixture = fixtureText == null ? new MetaTestFixture(rootModule, [], []) : MetaTestHost.ParseFixture(fixtureText);
                var test = await Task.Run(() =>
                {
                    var module = MetaBytecodeLoader.Load(compilation.Artifact);
                    var verified = MetaVerifier.Verify(compilation.Artifact, MetaApiCatalog.Default.CreateVerificationCatalog(module));
                    return MetaTestHost.Run(verified, fixture);
                });
                state.Logs = test.Logs.Concat(test.Commands.Select(command => command.Trace)).Take(128).ToList();
                state.Notice = Loc.GetString(test.Passed ? "nc-meta-test-passed" : "nc-meta-test-failed");
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or MetaFormatException)
            { state.Notice = Loc.GetString("nc-meta-test-invalid"); }
            return;
        }
        if (!await RevalidateBuildDependenciesAsync(context, package.Dependencies))
            return;
        var published = await _persistence.PublishReleaseAsync(request.RequestId, project, compilation.Artifact,
            MetaCompiler.BuildId, context.Epoch, context.Time, compilation.Exports, package.Dependencies, context.DeviceId, WorkspaceAuthority(context));
        state.Notice = Loc.GetString(published.MessageKey);
    }

    private (bool Accepted, string? Source) AcceptChunk(Context context, NCMetaWorkspaceRequestMessage request)
    {
        var key = (context.Terminal, context.User);
        if (request.TransferId == Guid.Empty || request.TotalUtf8Bytes is < 0 or > 256 * 1024 ||
            request.ContentSha256.Length != 64 || request.Utf8Offset < 0)
            return (false, null);
        if (_uploads.TryGetValue(key, out var expired) && expired.Expires <= _timing.CurTime)
        {
            _uploads.Remove(key);
            expired.Bytes.Dispose();
        }
        if (!_uploads.TryGetValue(key, out var upload))
        {
            if (request.Utf8Offset != 0 || _uploads.Count >= 16)
                return (false, null);
            upload = new Upload(request.TransferId, request.FileId, request.BaseRevision, request.Path,
                request.TotalUtf8Bytes, request.ContentSha256, _timing.CurTime + TimeSpan.FromSeconds(30));
            _uploads.Add(key, upload);
        }
        if (upload.Id != request.TransferId && upload.FinalSource != null && request.Utf8Offset == 0)
        {
            _uploads.Remove(key);
            upload.Bytes.Dispose();
            return AcceptChunk(context, request);
        }
        if (upload.Id != request.TransferId || upload.FileId != request.FileId || upload.Revision != request.BaseRevision ||
            upload.Path != request.Path || upload.Total != request.TotalUtf8Bytes || upload.Digest != request.ContentSha256)
            return (false, null);
        if (upload.LastRequest == request.RequestId)
            return (upload.LastOffset == request.Utf8Offset && upload.LastChunk == request.Source, upload.FinalSource);
        var bytes = Utf8.GetBytes(request.Source);
        if (bytes.Length > 32 * 1024 || request.Utf8Offset != upload.Bytes.Length ||
            bytes.Length > upload.Total - upload.Bytes.Length)
            return (false, null);
        upload.Bytes.Write(bytes);
        upload.LastRequest = request.RequestId;
        upload.LastOffset = request.Utf8Offset;
        upload.LastChunk = request.Source;
        if (!request.Complete)
            return (true, null);
        var complete = upload.Bytes.ToArray();
        if (complete.Length != upload.Total || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(complete)),
                upload.Digest, StringComparison.OrdinalIgnoreCase))
        {
            _uploads.Remove(key);
            upload.Bytes.Dispose();
            return (false, null);
        }
        upload.FinalSource = Utf8.GetString(complete);
        return (true, upload.FinalSource);
    }

    private bool IsCurrent(Context context, bool allowUnclaimed = false, bool afterClaim = false) =>
        _authority.IsCurrent(context.Management) &&
        _devices.TryGetEntry(context.Terminal, out var entry) && entry.Id == context.DeviceId &&
        entry.DeviceEpoch == context.DeviceEpoch &&
        entry.AuthorityEpoch == context.AuthorityEpoch && (allowUnclaimed || entry.Owner == context.Owner) &&
        _ui.IsUiOpen(context.Terminal, NCMetaUiKey.Workspace, context.User) &&
        (_devices.CanManage(context.Terminal, context.User) || allowUnclaimed && _devices.CanClaim(context.Terminal, context.User)) &&
        _persistence.TryCaptureTime(out var epoch, out _) && epoch == context.Epoch;

    private static NCMetaAuthorityConstraints WorkspaceAuthority(Context context) =>
        new(context.Management.Employment, [new(context.DeviceId, context.Owner, context.AuthorityEpoch)]);

    private Task<NCMetaAuthorityConstraints?> CaptureInstallationAuthorityAsync(Context context, EntityUid host) =>
        OnMainAsync(() => IsCurrent(context) ? _authority.CaptureWriteConstraints(context.Management, context.Terminal, host) : null);

    private Task<T> OnMainAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    private bool TryAcquireLease(Context context, Guid fileId)
    {
        if (!IsCurrent(context)) return false;
        if (_leases.TryGetValue(fileId, out var lease) && lease.Expires > _timing.CurTime &&
            (lease.Terminal != context.Terminal || lease.User != context.User)) return false;
        if (!_leases.ContainsKey(fileId) && _leases.Count >= 1024) return false;
        _leases[fileId] = (context.Terminal, context.User, _timing.CurTime + TimeSpan.FromSeconds(60));
        return true;
    }
}
