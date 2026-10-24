// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Verification;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Applications;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Compilation;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Time;
using NightCity.Diagnostics.Contracts;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Map;
using Robust.Shared.Network;

namespace Content.Server._NC.Diagnostics;

public sealed partial class NCDiagnosticsSystem
{
    [Dependency] private NCMetaApplicationSystem _metaLoadApps = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _metaLoadDevices = default!;
    [Dependency] private NCMetaAuthoritySystem _metaLoadAuthority = default!;
    [Dependency] private NCMetaPersistenceSystem _metaLoadPersistence = default!;
    [Dependency] private NCEntityPartyResolverSystem _metaLoadParties = default!;
    [Dependency] private SharedUserInterfaceSystem _metaLoadUi = default!;
    private sealed class AppLoad(Guid id, string fixtureId, EntityUid host, EntityUid owner, string party, NCSessionEpoch epoch)
    {
        public readonly Guid Id = id;
        public readonly string FixtureId = fixtureId;
        public readonly EntityUid Host = host;
        public readonly EntityUid Owner = owner;
        public readonly string Party = party;
        public readonly NCSessionEpoch Epoch = epoch;
        public string Stage = "Preparing";
        public string Error = "";
        public Guid Installation;
        public Guid Generation;
        public string Artifact = "";
        public readonly Dictionary<(Guid User, EntityUid UiOwner), Guid> Sessions = [];
        public readonly List<EntityUid> UiOwners = [];
        public readonly Dictionary<Guid, EntityUid> Actors = [];
    }
    private AppLoad? _appLoad;
    private const string AppLoadSource = """
        module load.frontend;
        import meta.app;
        var count:int=0;
        pub struct State { count:int; }
        pub struct Input {}
        pub fn view(context:app.AppContext)->State { return new State(count:count); }
        pub fn step(context:app.AppContext,input:Input)->result<void> { count+=1; return ok(()); }
        fn main()->void{}
        """;
    private const string AppLoadContract = """
        {"version":"1.0","views":{"main":{"function":"load.frontend.view","document":"ui/main.mui","stateType":"load.frontend.State","fields":{"count":{"type":"integer"}},"actions":["step"]}},"actions":{"step":{"function":"load.frontend.step","inputType":"load.frontend.Input","inputs":{},"scopes":[]}},"resources":[]}
        """;
    private const string AppLoadDocument = "<Page id='page'><Column id='column'><Text id='count' text='{bind count}'/><Button id='step' action='step' text='+'/></Column></Page>";
    private JsonObject PrepareMetaAppLoad(JsonObject args)
    {
        var fixture = GetFixture(args, out var fixtureId);
        if (_appLoad is { Stage: not ("Failed" or "Stopped") }) throw new DiagnosticException("busy", "Stop the previous native app fixture first.");
        var host = ResolveEntity(args, "host"); var owner = ResolveEntity(args, "actor");
        if (!fixture.Entities.Contains(host) || !fixture.Entities.Contains(owner) || !LiveAppActor(owner) ||
            !_metaLoadParties.TryResolve(owner, out var party) || !_metaLoadDevices.CanClaim(host, owner) && !_metaLoadDevices.CanManage(host, owner) ||
            !_metaLoadPersistence.TryCaptureTime(out var epoch, out _))
            throw new DiagnosticException("access_denied", "The host and actual connected owner must belong to this isolated physical fixture and pass normal management admission.");
        if (fixture.Entities.Count + 3 > 64) throw new DiagnosticException("fixture_limit", "The fixed app fixture has no room for its four physical UI owners.");
        var job = new AppLoad(Guid.NewGuid(), fixtureId, host, owner, party.ToString(), epoch); _appLoad = job;
        job.UiOwners.Add(host);
        foreach (var offset in new[] { new Vector2(-0.75f, 0), new Vector2(0, 0.75f), new Vector2(0, -0.75f) })
        {
            var terminal = Spawn("NCMetaPerformanceHost", Transform(host).Coordinates.Offset(offset));
            fixture.Entities.Add(terminal);
            job.UiOwners.Add(terminal);
        }
        job.Actors.Add(Comp<ActorComponent>(owner).PlayerSession.UserId.UserId, owner);
        _ = PrepareMetaAppLoadAsync(job);
        return new JsonObject { ["loadId"] = job.Id.ToString("N"), ["stage"] = job.Stage };
    }
    private bool LiveAppActor(EntityUid actor) => TryComp<ActorComponent>(actor, out var component) &&
        component.PlayerSession.Status == SessionStatus.InGame && component.PlayerSession.AttachedEntity == actor &&
        _players.Sessions.Contains(component.PlayerSession);
    private bool CurrentAppLoad(AppLoad job) => ReferenceEquals(_appLoad, job) && job.Stage == "Preparing" &&
        _fixtures.TryGetValue(job.FixtureId, out var fixture) && fixture.Entities.Contains(job.Host) && fixture.Entities.Contains(job.Owner) &&
        LiveAppActor(job.Owner) && _metaLoadParties.TryResolve(job.Owner, out var party) && party.ToString() == job.Party &&
        _metaLoadPersistence.TryCaptureTime(out var epoch, out _) && epoch == job.Epoch;
    private Task<T> AppLoadMain<T>(Func<T> action)
    {
        var pending = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() => { try { pending.TrySetResult(action()); } catch (Exception error) { pending.TrySetException(error); } });
        return pending.Task;
    }
    private async Task PrepareMetaAppLoadAsync(AppLoad job)
    {
        try
        {
            var entry = await AppLoadMain(() => CurrentAppLoad(job) && _metaLoadDevices.TryGetEntry(job.Host, out var device) ? device : throw new OperationCanceledException());
            var clock = await AppLoadMain(() => { if (!CurrentAppLoad(job) || !_metaLoadPersistence.TryCaptureTime(out _, out var now)) throw new OperationCanceledException(); return now; });
            if (entry.Owner.Length == 0)
            {
                var claimed = await _metaLoadPersistence.ClaimDeviceAsync(Guid.NewGuid(), entry.Id, job.Party, "load.frontend", job.Epoch, clock,
                    await AppLoadMain(() => _metaLoadDevices.GetEconomicItemId(job.Host)));
                if (claimed == null || !await AppLoadMain(() => CurrentAppLoad(job) && _metaLoadDevices.CanClaim(job.Host, job.Owner) &&
                    _metaLoadDevices.TryApplyOwner(job.Host, entry.Id, "", job.Party, claimed.AuthorityEpoch))) throw new InvalidOperationException("Native owner claim refused.");
            }
            var projectId = Guid.NewGuid();
            var created = await _metaLoadPersistence.CreateProjectAsync(Guid.NewGuid(), projectId, job.Party, "load.frontend", job.Epoch, clock);
            if (!created.Success) throw new InvalidOperationException("Native project create refused.");
            var project = await _metaLoadPersistence.ReadProjectAsync(projectId, job.Party) ?? throw new InvalidOperationException("Project unavailable.");
            var file = project.Files.Single();
            var saved = await _metaLoadPersistence.SaveSourceAsync(Guid.NewGuid(), project.Id, file.Id, job.Party, file.Revision, file.Path, AppLoadSource, job.Epoch, clock);
            if (!saved.Success) throw new InvalidOperationException("Native source save refused.");
            project = (await _metaLoadPersistence.ReadProjectAsync(project.Id, job.Party))!;
            await using var worker = new MetaCompilerWorkerPool(MetaCompilerWorkerPool.ResolvePublishedWorkerPath(AppContext.BaseDirectory));
            var compiled = await worker.CompileAsync(job.Party, new([new(file.Id.ToString("D"), AppLoadSource)], "load.frontend", "load.frontend", "1.0.0"));
            if (!compiled.Success || compiled.Artifact == null || compiled.Exports == null) throw new InvalidOperationException("Worker refused load source.");
            var artifact = MetaVerifier.Verify(compiled.Artifact, MetaApiCatalog.Default.CreateVerificationCatalog(MetaBytecodeLoader.Load(compiled.Artifact)));
            var documents = new Dictionary<string, string> { ["ui/main.mui"] = AppLoadDocument };
            var digest = MuiVerifiedApplication.ComputePackageDigest(artifact.Digest, AppLoadContract, documents, "");
            if (!MuiVerifiedApplication.TryAdmit(artifact, compiled.Exports, AppLoadContract, documents, "", digest, out var app, out _)) throw new InvalidOperationException("Independent app admission refused.");
            var published = await _metaLoadPersistence.PublishReleaseAsync(Guid.NewGuid(), project, compiled.Artifact, "isolated-frontend-load", job.Epoch, clock, compiled.Exports);
            if (!published.Success) throw new InvalidOperationException("Native release refused.");
            project = (await _metaLoadPersistence.ReadProjectAsync(project.Id, job.Party))!;
            var capacity = await AppLoadMain(() =>
            {
                if (!CurrentAppLoad(job) || !_metaRuntime.CanInstall(job.Host, job.Owner, artifact, [])) throw new OperationCanceledException();
                var profile = _prototypes.Index(Comp<NCMetaHostComponent>(job.Host).Profile);
                return TryComp<NCMetaStorageComponent>(job.Host, out var storage) ? Math.Min(profile.StorageBytes, storage.CapacityBytes) : profile.StorageBytes;
            });
            var installed = await _metaLoadPersistence.InstallAsync(Guid.NewGuid(), entry.Id, job.Party, project, project.Releases.Last(), [], false, job.Epoch, clock, capacity, memoryLimitBytes: 512 * 1024);
            if (!installed.Success) throw new InvalidOperationException("Native install refused.");
            var installation = (await _metaLoadPersistence.ReadInstallationsAsync(entry.Id, job.Party)).Single(value => value.PackageIdentity == "load.frontend");
            if (!await AppLoadMain(() => CurrentAppLoad(job) && _metaLoadUi.TryOpenUi(job.Host, NCMetaUiKey.Workspace, job.Owner))) throw new OperationCanceledException();
            var policy = await _metaLoadPersistence.ReadServiceGrantPolicyAsync(installation.Id);
            var granted = await _metaLoadAuthority.TryIssueInstallationGrantsAsync(job.Host, job.Owner, job.Host, Guid.NewGuid(), installation, artifact,
                new(policy?.Revision ?? 0, true, true, null, []));
            if (!granted.Success) throw new InvalidOperationException("Native service consent refused.");
            installation = (await _metaLoadPersistence.ReadInstallationAsync(entry.Id, job.Party, installation.Id))!;
            var admission = await _metaLoadAuthority.ResolveServiceAdmissionAsync(installation, artifact, false);
            if (admission == null) throw new InvalidOperationException("Native service proof unavailable.");
            for (var attempt = 0; attempt < 600; attempt++)
            {
                if (await AppLoadMain(() =>
                {
                    if (!CurrentAppLoad(job)) throw new OperationCanceledException();
                    if (!_metaRuntime.TryStart(job.Host, job.Owner, installation, artifact, true, serviceAdmission: admission) ||
                        !_metaRuntime.TryGetInstallation(job.Host, job.Party, installation.Id, out var runtime)) return false;
                    var publicPolicy = new NCMetaAppPolicy(ImmutableHashSet<string>.Empty, ImmutableHashSet.Create("main"), ImmutableHashSet.Create("step"), ImmutableHashSet<string>.Empty);
                    if (!_metaLoadApps.TryRegister(job.Host, job.Owner, app!, publicPolicy, runtime.Generation)) throw new InvalidOperationException("Native app registration refused.");
                    job.Installation = installation.Id; job.Generation = runtime.Generation; job.Artifact = artifact.Digest; job.Stage = "Ready"; return true;
                })) return;
                await Task.Delay(20);
            }
            throw new InvalidOperationException("Native runtime did not admit frontend load.");
        }
        catch (Exception error) { await AppLoadMain(() => { if (ReferenceEquals(_appLoad, job) && job.Stage == "Preparing") { job.Stage = "Failed"; job.Error = error.GetType().Name + ":" + error.Message; } return true; }); }
    }
    private JsonObject ReadMetaAppLoad(JsonObject args)
    {
        RequireFixtureAdmin(); var job = SelectAppLoad(args);
        return new JsonObject { ["loadId"] = job.Id.ToString("N"), ["stage"] = job.Stage, ["error"] = job.Error,
            ["host"] = GetNetEntity(job.Host).ToString(), ["artifactDigest"] = job.Artifact, ["openedSessions"] = job.Sessions.Count,
            ["actualConnectedActors"] = _players.Sessions.Count(session => session.Status == SessionStatus.InGame && session.AttachedEntity is { } actor && LiveAppActor(actor)),
            ["quotaPolicy"] = "unchanged-production-app-quotas", ["runtimeGeneration"] = job.Generation.ToString("D"),
            ["physicalUiOwners"] = new JsonArray(job.UiOwners.Select(owner => (JsonNode?) JsonValue.Create(GetNetEntity(owner).ToString())).ToArray()),
            ["maximumViewsPerConnection"] = 4, ["frontendAdmissionVersion"] = 2,
            ["sourceHash"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppLoadSource))) };
    }
    private AppLoad SelectAppLoad(JsonObject args) => Guid.TryParseExact(args.RequiredString("loadId", 32), "N", out var id) && _appLoad is { } job && job.Id == id
        ? job : throw new DiagnosticException("load_not_found", "The native app fixture is unavailable.");
    private JsonObject OpenMetaAppLoad(JsonObject args)
    {
        var fixture = GetFixture(args, out var fixtureId); var job = SelectAppLoad(args); var actor = ResolveEntity(args, "actor");
        var uiOwner = ResolveEntity(args, "uiOwner");
        if (fixtureId != job.FixtureId || job.Stage != "Ready" || !fixture.Entities.Contains(actor) || !LiveAppActor(actor) || job.Sessions.Count >= 50 ||
            !job.UiOwners.Contains(uiOwner) || !fixture.Entities.Contains(uiOwner) || Deleted(uiOwner) ||
            !TryComp<ActorComponent>(actor, out var participant) || job.Sessions.ContainsKey((participant.PlayerSession.UserId.UserId, uiOwner)) ||
            !_metaLoadApps.TryOpen(uiOwner, actor, job.Host, "main", NCMetaAppPresentation.Website, NCMetaUiKey.Application,
                () => LiveAppActor(actor) && !Deleted(uiOwner) && _metaLoadDevices.IsPowered(uiOwner) && _metaLoadDevices.IsPowered(job.Host) &&
                    _interaction.InRangeUnobstructed(actor, uiOwner, range: 2) && _interaction.InRangeUnobstructed(actor, job.Host, range: 2), out var session,
                runtimeGeneration: job.Generation)) throw new DiagnosticException("app_admission_rejected", "The actual connected actor, physical route or unchanged app quota refused this session.");
        job.Sessions.Add((participant.PlayerSession.UserId.UserId, uiOwner), session);
        return new JsonObject { ["sessionId"] = session.ToString("D"), ["userId"] = participant.PlayerSession.UserId.ToString(),
            ["uiOwner"] = GetNetEntity(uiOwner).ToString(), ["host"] = GetNetEntity(job.Host).ToString(),
            ["runtimeGeneration"] = job.Generation.ToString("D"), ["nativeBuiOpened"] = true };
    }
    private JsonObject AttachMetaAppLoad(JsonObject args)
    {
        RequireFixtureAdmin(); var job = SelectAppLoad(args);
        if (job.Stage != "Ready" || !_fixtures.TryGetValue(job.FixtureId, out var fixture) || Deleted(fixture.Map) ||
            !Guid.TryParse(args.RequiredString("userId", 36), out var userId) ||
            _players.Sessions.FirstOrDefault(player => player.UserId == new NetUserId(userId)) is not { } connected)
            throw new DiagnosticException("player_not_found", "Select an actual connected user and an admitted physical app fixture.");
        if (job.Actors.TryGetValue(userId, out var existing) && LiveAppActor(existing))
            return new JsonObject { ["actor"] = GetNetEntity(existing).ToString(), ["fixtureId"] = job.FixtureId, ["attached"] = true };
        if (job.Actors.Count >= 50 || fixture.Entities.Count >= 64)
            throw new DiagnosticException("fixture_limit", "The fixed app fixture permits at most50 real connected actors and64 owned entities.");
        var angle = job.Actors.Count * MathF.Tau / 50;
        var actor = Spawn("MobHuman", Transform(job.Host).Coordinates.Offset(new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 0.75f));
        fixture.Entities.Add(actor);
        var attached = AttachFixtureActor(new JsonObject { ["fixtureId"] = job.FixtureId, ["actor"] = GetNetEntity(actor).ToString(), ["userId"] = connected.UserId.ToString() });
        if (!LiveAppActor(actor)) { QueueDel(actor); fixture.Entities.Remove(actor); throw new DiagnosticException("attach_rejected", "Native mind/ticker did not attach the actual connection."); }
        job.Actors[userId] = actor;
        return new JsonObject { ["actor"] = GetNetEntity(actor).ToString(), ["fixtureId"] = job.FixtureId, ["attached"] = attached["attached"]?.DeepClone() };
    }
    private JsonObject StopMetaAppLoad(JsonObject args)
    {
        RequireFixtureAdmin(); var job = SelectAppLoad(args); job.Stage = "Stopped";
        if (job.Installation != Guid.Empty) _metaRuntime.StopInstallation(job.Host, job.Installation);
        return new JsonObject { ["stopped"] = true };
    }
}
#endif
