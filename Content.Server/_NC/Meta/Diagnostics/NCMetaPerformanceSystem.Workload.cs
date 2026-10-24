// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Compilation;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.Authority;
using Content.Shared._NC.Meta.Network;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.NPC;
using Content.Shared.Preferences;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization.Manager;

namespace Content.Server._NC.Meta.Diagnostics;

public sealed partial class NCMetaPerformanceSystem
{
    private sealed class Owner(EntityUid actor, NCActorId id, string party, Vector2 origin)
    {
        public readonly EntityUid Actor = actor;
        public readonly NCActorId Id = id;
        public readonly string Party = party;
        public readonly Vector2 Origin = origin;
        public readonly List<NCMetaDeviceEntry> Hosts = [];
        public NCMetaDeviceEntry Sender = default!;
        public NCMetaDeviceEntry Silent = default!;
        public NCMetaDeviceEntry? Active;
        public long NextStimulus;
        public int Destination;
        public TimeSpan NextMove;
    }
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaAuthoritySystem _authority = default!;
    [Dependency] private NCMetaNetworkSystem _network = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _actorIndex = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private ITileDefinitionManager _tiles = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    private readonly ConcurrentQueue<Action> _main = new();
    private readonly HashSet<EntityUid> _ownedEntities = [];
    private readonly List<Owner> _owners = [];
    private readonly List<NCMetaRuntimeInstance> _running = [];
    private readonly HashSet<string> _artifactDigests = new(StringComparer.Ordinal);
    private EntityUid? _map;
    private EntityUid? _grid;
    private MetaCompilerWorkerPool? _compiler;
    private int _installed;
    private int _loaded;
    private long _stimuliAccepted;
    private long _stimuliRejected;
    private long _botSteps;
    private int _driveCursor;
    private NCSessionEpoch _world;
    private static readonly Vector2[] HostOffsets = [new(-1, 0), new(1, 0), new(0, 1), new(0, -1)];
    private static readonly Vector2[] BotRoute = [new(-2, -2), new(2, -2), new(2, 2), new(-2, 2)];
    private static (int Installed, int Loaded, int Active) Shape(string workload) => workload switch
    { "A" => (100, 100, 100), "B" => (500, 500, 500), "C" => (2000, 200, 200), _ => (2000, 2000, 200) };
    private static string Source(int slot) => """
        module load.serviceSLOT;
        import meta.net;
        link port:net.MessagePort="load-port";
        var count:int=PHASE;
        var checksum:int=0;
        fn main()->void{}
        async watch port.messageReceived(message){
            if(message.topic!="load"){return;}
            if(message.payload=="attack" && SLOT==0){while(true){checksum+=1;}}
            var i:int=0;
            while(i<24){checksum=(checksum+i)%10007;i+=1;}
            count+=1;
            if(count%10==0){match(await port.send(message.replyTo,"reply","ok")){ok(receipt)=>{}err(error)=>{}}}
        }
        """.Replace("SLOT", slot.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("PHASE", (slot % 10).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    private static string WorkloadSourceHash => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 16).Select(Source)))));
    private Task<T> Main<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _main.Enqueue(() => { try { completion.TrySetResult(action()); } catch (Exception error) { completion.TrySetException(error); } });
        return completion.Task;
    }
    private void PumpWorkloadCallbacks()
    { for (var work = 0; work < 8 && _main.TryDequeue(out var action); work++) action(); }
    private async Task<T> StoreWithBackpressure<T>(Guid run, Func<Task<T>> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!await Main(() => IsPreparing(run))) throw new OperationCanceledException();
            try { return await operation(); }
            catch (Content.Server.Database.NCMetaBlobQuotaException) when (attempt < 240)
            {
                // Preparation is outside recording. Preserve the exact native command identity,
                // wait for the durable global budget, and recheck world/run before every retry.
                await Task.Delay(500);
            }
        }
    }
    private bool IsPreparing(Guid id) => id == _id && _stage == "Preparing" && _persistence.TryCaptureTime(out var world, out _) && world == _world;
    private (NCSessionEpoch, NCActiveWorldTime) Time()
    {
        if (!_persistence.TryCaptureTime(out var epoch, out var now) || epoch != _world) throw new InvalidOperationException("Performance world changed.");
        return (epoch, now);
    }
    private async Task PrepareWorkloadAsync(Guid id, NCMetaPerformanceRequest request)
    {
        try
        {
            await Main(() =>
            {
                if (_id != id || _stage != "Preparing" || !_persistence.TryCaptureTime(out _world, out _)) throw new OperationCanceledException();
                var map = _maps.CreateMap(); _map = map; _ownedEntities.Add(map);
                var grid = _maps.CreateGridEntity(map); _grid = grid.Owner; _ownedEntities.Add(grid.Owner); return true;
            });
            // Tile work is prepared in small strips before recording, never one unbounded spawn mutation.
            for (var principal = 0; principal < 40; principal++)
            {
                var origin = new Vector2(principal % 8 * 10, principal / 8 * 10);
                await Main(() =>
                {
                    if (!IsPreparing(id)) throw new OperationCanceledException();
                    var tile = new Tile(_tiles["FloorSteel"].TileId);
                    for (var x = -3; x <= 3; x++) for (var y = -3; y <= 3; y++)
                        _maps.SetTile((_grid!.Value, Comp<MapGridComponent>(_grid.Value)), new Vector2i((int) origin.X + x, (int) origin.Y + y), tile);
                    return true;
                });
                var actorId = new NCActorId(Guid.NewGuid());
                var owner = await Main(() =>
                {
                    var actor = SpawnOwned("NCMetaPerformanceBot", origin);
                    if (!_actorIndex.TryBind(actorId, actor)) throw new InvalidOperationException("Native actor binding failed.");
                    var entry = new Owner(actor, actorId, NCPartyRef.ForActor(actorId).ToString(), origin); _owners.Add(entry); return entry;
                });
                var appearance = await Main(() => NCHumanoidProfilePayload.Serialize(_serialization, HumanoidCharacterProfile.Random()));
                var command = Guid.NewGuid();
                var created = await _actors.CreateResidentAsync(new(command, actorId, Loc.GetString("nc-meta-performance-bot-name"),
                    new("NCLivingCityResident"), new("NCDefaultResidentPersona"), appearance, default, null, command,
                    AuditSource: "isolated.meta-performance-owner"));
                if (created.Status != NCCityActorMutationStatus.Applied) throw new InvalidOperationException("Native test principal creation rejected.");
                owner.Sender = await CreateDeviceAsync(id, owner, new(-1.5f, -0.5f));
                owner.Silent = await CreateDeviceAsync(id, owner, new(1.5f, -0.5f));
                var shape = Shape(request.Workload);
                var count = shape.Installed / 40 + (principal < shape.Installed % 40 ? 1 : 0);
                for (var hostIndex = 0; hostIndex < (count + 15) / 16; hostIndex++)
                {
                    var host = await CreateDeviceAsync(id, owner, HostOffsets[hostIndex]); owner.Hosts.Add(host);
                    await WireAsync(owner, owner.Sender.Entity, host.Entity);
                    await WireAsync(owner, owner.Silent.Entity, host.Entity);
                }
                await PrepareInstallationsAsync(id, request, principal, owner, count);
            }
            var assemblyHash = await AssemblyHashAsync();
            await Main(() =>
            {
                if (!IsPreparing(id)) throw new OperationCanceledException();
                var shape = Shape(request.Workload);
                if (_installed != shape.Installed || _loaded != (request.Baseline ? 0 : shape.Loaded) || _owners.Count != 40)
                    throw new InvalidOperationException("Requested native scale was not admitted.");
                _assemblyHash = assemblyHash; _stage = "Warmup";
                // Allocate the sealed recording buffer before warmup. A tiny separate sink warms
                // observer/JIT paths without mixing warmup observations into measured samples.
                _capture = new(); _warmupMetrics = new(1); _runtime.SetMetrics(_warmupMetrics);
                _tickListener?.Dispose(); _tickListener = new(); _tickListener.SetSink(_warmupMetrics);
                for (var index = 0; index < _owners.Count; index++)
                {
                    var phase = unchecked((uint) request.Seed + (uint) index * 2654435761u);
                    _owners[index].NextStimulus = Stopwatch.GetTimestamp() + (long) (phase % 500) * Stopwatch.Frequency / 1000;
                    _owners[index].Destination = (int) (phase % 4);
                }
                _stageDeadline = Stopwatch.GetTimestamp() + request.WarmupSeconds * Stopwatch.Frequency; return true;
            });
        }
        catch (Exception error)
        {
            await Main(() => { if (id == _id && _stage == "Preparing") { Log.Error($"META isolated workload preparation failed: {error}"); FailRun(error.GetType().Name + ":" + error.Message); } return true; });
        }
    }
    private EntityUid SpawnOwned(string prototype, Vector2 position)
    {
        if (_ownedEntities.Count >= 1024 || _grid == null) throw new InvalidOperationException("Performance fixture entity cap.");
        var uid = Spawn(prototype, new EntityCoordinates(_grid.Value, position)); _ownedEntities.Add(uid); return uid;
    }
    private async Task<NCMetaDeviceEntry> CreateDeviceAsync(Guid run, Owner owner, Vector2 offset)
    {
        var uid = await Main(() => IsPreparing(run) ? SpawnOwned("NCMetaPerformanceHost", owner.Origin + offset) : throw new OperationCanceledException());
        NCMetaDeviceEntry? entry = null;
        for (var attempt = 0; attempt < 600; attempt++)
        {
            entry = await Main(() => IsPreparing(run) && _devices.TryGetEntry(uid, out var value) && value.Ready ? value : null);
            if (entry != null) break;
            await Task.Delay(20);
        }
        if (entry == null) throw new InvalidOperationException("Physical performance host did not become ready.");
        var time = await Main(Time);
        var claimed = await _persistence.ClaimDeviceAsync(Guid.NewGuid(), entry.Id, owner.Party, "Isolated load workspace", time.Item1, time.Item2);
        if (claimed == null || !await Main(() => _devices.TryApplyOwner(uid, entry.Id, "", owner.Party, claimed.AuthorityEpoch)))
            throw new InvalidOperationException("Native device ownership failed.");
        return entry;
    }
    private async Task WireAsync(Owner owner, EntityUid first, EntityUid second)
    {
        await Main(() =>
        {
            var cable = SpawnOwned("NCMetaNetworkCable", owner.Origin);
            if (!_network.TryConnectCable(cable, owner.Actor, first) || !_network.TryConnectCable(cable, owner.Actor, second))
                throw new InvalidOperationException("Native physical cable rejected.");
            return true;
        });
    }
    private async Task PrepareInstallationsAsync(Guid run, NCMetaPerformanceRequest request, int principal, Owner owner, int count)
    {
        var published = new Dictionary<int, (NCMetaProject Project, NCMetaRelease Release, VerifiedArtifact Artifact)>();
        var shape = Shape(request.Workload);
        for (var index = 0; index < count; index++)
        {
            if (!await Main(() => IsPreparing(run))) throw new OperationCanceledException();
            var slot = index % 16; var host = owner.Hosts[index / 16];
            if (!published.TryGetValue(slot, out var package))
            {
                var projectId = Guid.NewGuid(); var time = await Main(Time);
                var createId = Guid.NewGuid();
                var created = await StoreWithBackpressure(run, () => _persistence.CreateProjectAsync(createId, projectId, owner.Party, "Load " + slot, time.Item1, time.Item2));
                if (!created.Success) throw new InvalidOperationException("Project create rejected: " + created.MessageKey);
                var project = await _persistence.ReadProjectAsync(projectId, owner.Party) ?? throw new InvalidOperationException("Project missing.");
                var file = project.Files.Single(); var source = Source(slot); time = await Main(Time);
                var saveId = Guid.NewGuid(); var sourceProject = project;
                var saved = await StoreWithBackpressure(run, () => _persistence.SaveSourceAsync(saveId, sourceProject.Id, file.Id, owner.Party, file.Revision, file.Path, source, time.Item1, time.Item2));
                if (!saved.Success) throw new InvalidOperationException("Source save rejected: " + saved.MessageKey);
                project = (await _persistence.ReadProjectAsync(projectId, owner.Party))!;
                var worker = await Main(() => _compiler ??= new MetaCompilerWorkerPool(MetaCompilerWorkerPool.ResolvePublishedWorkerPath(AppContext.BaseDirectory)));
                var compiled = await worker.CompileAsync(owner.Party, new([new(file.Id.ToString("D"), source)], "load.service" + slot, "load.service" + slot, "1.0.0"));
                if (!compiled.Success || compiled.Artifact == null) throw new InvalidOperationException("Actual worker compilation rejected.");
                var module = MetaBytecodeLoader.Load(compiled.Artifact); var artifact = MetaVerifier.Verify(compiled.Artifact, MetaApiCatalog.Default.CreateVerificationCatalog(module));
                await Main(() => _artifactDigests.Add(artifact.Digest));
                time = await Main(Time);
                var publishId = Guid.NewGuid(); var releaseProject = project;
                var released = await StoreWithBackpressure(run, () => _persistence.PublishReleaseAsync(publishId, releaseProject, compiled.Artifact, "isolated-performance", time.Item1, time.Item2, compiled.Exports));
                if (!released.Success) throw new InvalidOperationException("Release rejected: " + released.MessageKey);
                project = (await _persistence.ReadProjectAsync(projectId, owner.Party))!;
                package = (project, project.Releases.Last(), artifact); published.Add(slot, package);
            }
            var activeForOwner = shape.Active / 40 + (principal < shape.Active % 40 ? 1 : 0);
            var eventActive = index < activeForOwner;
            var target = eventActive ? host : owner.Silent;
            if (eventActive) owner.Active = host;
            var link = package.Artifact.GetLinks().Single();
            var bindings = new[] { new NCMetaBinding(link.SlotName, target.Id, link.ContractHash, package.Artifact.Manifest.RequiredScopes) };
            var shouldRun = !request.Baseline && (request.Workload != "C" || eventActive);
            var timeNow = await Main(Time);
            var installId = Guid.NewGuid();
            var installed = await StoreWithBackpressure(run, () => _persistence.InstallAsync(installId, host.Id, owner.Party, package.Project, package.Release, bindings,
                shouldRun, timeNow.Item1, timeNow.Item2, 256 * 1024 * 1024, memoryLimitBytes: 128 * 1024));
            if (!installed.Success) throw new InvalidOperationException("Install rejected: " + installed.MessageKey);
            var installation = (await _persistence.ReadInstallationsAsync(host.Id, owner.Party)).Single(value => value.PackageIdentity == "load.service" + slot);
            await Main(() => { _installed++; return true; });
            if (!shouldRun) continue;
            if (!await Main(() => _ui.TryOpenUi(host.Entity, NCMetaUiKey.Workspace, owner.Actor))) throw new InvalidOperationException("Native owner workspace unavailable.");
            var policy = await _persistence.ReadServiceGrantPolicyAsync(installation.Id);
            var members = MetaApiCatalog.Default.Members.Where(member => member.Owner == "meta.net.MessagePort" && package.Artifact.Manifest.RequiredScopes.Contains(member.Scope)).ToArray();
            var approval = await _authority.TryIssueInstallationGrantsAsync(host.Entity, owner.Actor, host.Entity, Guid.NewGuid(), installation, package.Artifact,
                new(policy?.Revision ?? 0, true, true, null, [new(link.SlotName, target.Id, members.Select(member => member.QualifiedName).ToList(), members.Select(member => member.Scope).Distinct().ToList())]));
            if (!approval.Success) throw new InvalidOperationException("Native grant rejected: " + approval.MessageKey);
            installation = (await _persistence.ReadInstallationAsync(host.Id, owner.Party, installation.Id))!;
            var admission = await _authority.ResolveServiceAdmissionAsync(installation, package.Artifact, true);
            if (admission == null) throw new InvalidOperationException("Native service admission missing.");
            NCMetaRuntimeInstance? instance = null;
            for (var attempt = 0; attempt < 600 && instance == null; attempt++)
            {
                instance = await Main(() => IsPreparing(run) && _runtime.TryStartRecovered(host.Entity, installation, package.Artifact, false, serviceAdmission: admission) &&
                    _runtime.TryGetInstallation(host.Entity, owner.Party, installation.Id, out var running) ? running : null);
                if (instance == null) await Task.Delay(20);
            }
            if (instance == null) throw new InvalidOperationException("Requested resident context refused by real runtime admission.");
            await Main(() => { _running.Add(instance); _loaded++; return true; });
        }
    }
    private void DriveNativeStimuli()
    {
        if (_request == null || _owners.Count == 0) return;
        var now = Stopwatch.GetTimestamp();
        for (var work = 0; work < 8; work++)
        {
            var owner = _owners[_driveCursor++ % _owners.Count];
            if (owner.Active == null || now < owner.NextStimulus) continue;
            owner.NextStimulus += Stopwatch.Frequency / 2;
            var payload = _stage == "Capturing" && _request.Mode == "Hostile" ? "attack" : _request.Mode == "Heavy" ? HeavyPayload : "tick";
            if (_network.TrySendPerformanceStimulus(owner.Sender.Entity, owner.Actor, owner.Active.Entity, payload)) _stimuliAccepted++; else _stimuliRejected++;
            if (_stage == "Capturing" && _request.Mode == "Burst" && (_timing.CurTick.Value % 600) < 8)
            { if (_network.TrySendPerformanceStimulus(owner.Sender.Entity, owner.Actor, owner.Active.Entity, payload)) _stimuliAccepted++; else _stimuliRejected++; }
        }
    }
    private static readonly string HeavyPayload = new('x', 2048);
    private void AdvanceNativeBots()
    {
        foreach (var owner in _owners)
        {
            if (_timing.CurTime < owner.NextMove || !Exists(owner.Actor) || _grid == null) continue;
            if (TryComp<NPCSteeringComponent>(owner.Actor, out var steering) && steering.Status == SteeringStatus.InRange) _botSteps++;
            owner.NextMove = _timing.CurTime + TimeSpan.FromSeconds(3);
            EnsureComp<ActiveNPCComponent>(owner.Actor);
            _steering.Register(owner.Actor, new EntityCoordinates(_grid.Value, owner.Origin + BotRoute[owner.Destination++ % BotRoute.Length]));
        }
    }
    private void StopWorkload()
    {
        foreach (var instance in _running) _runtime.StopInstallation(instance.Host, instance.Installation.Id);
        foreach (var owner in _owners) if (Exists(owner.Actor)) _steering.Unregister(owner.Actor);
        _running.Clear();
    }
    private void CleanUpWorkload()
    {
        _stage = "Cleaning";
        _ = ArchiveAndCleanAsync(_id, _preparation);
    }
    private async Task ArchiveAndCleanAsync(Guid id, Task? preparation)
    {
        try
        {
            if (preparation != null) await preparation;
            var owners = await Main(() => _owners.ToArray());
            foreach (var owner in owners)
            {
                var record = await _actors.GetAsync(owner.Id);
                if (record == null) continue;
                var archived = await _actors.SetStatusAsync(new(Guid.NewGuid(), owner.Id, record.AggregateVersion,
                    Content.Shared._NC.LivingCity.Actors.NCCityActorStatus.Archived, Guid.NewGuid(), AuditSource: "isolated.meta-performance-cleanup"));
                if (archived.Status != NCCityActorMutationStatus.Applied) throw new InvalidOperationException("Cannot archive physical fixture actor.");
            }
            await Main(() =>
            {
                if (_id != id) return false;
                if (_map is { } map && Exists(map)) QueueDel(map);
                _map = _grid = null; _ownedEntities.Clear(); _owners.Clear(); _running.Clear(); _driveCursor = 0; _stage = "Stopped";
                return true;
            });
        }
        catch (Exception error) { await Main(() => { if (_id == id) { _error = "Cleanup:" + error.GetType().Name; _stage = "Failed"; } return true; }); }
    }
}
