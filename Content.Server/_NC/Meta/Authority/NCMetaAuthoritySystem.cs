// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Threading;
using Content.Meta.Hosting;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.UI;
using Content.Shared.Interaction;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared.Roles;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;
using Robust.Shared.Player;
using Robust.Shared.Enums;

namespace Content.Server._NC.Meta.Authority;

/// <summary>Opaque native proof. A UI owner string, access card or META value cannot construct one.</summary>
public sealed class NCMetaManagementProof
{
    internal NCMetaManagementProof(EntityUid actorEntity, NCPartyRef actor, NCPartyRef owner,
        NCPlayerEmploymentProof? employment, long employmentGeneration, long prototypeGeneration,
        long expiresAtTicks, NCSessionEpoch epoch)
    {
        ActorEntity = actorEntity; Actor = actor; Owner = owner; Employment = employment;
        EmploymentGeneration = employmentGeneration; PrototypeGeneration = prototypeGeneration;
        ExpiresAtTicks = expiresAtTicks; WorldEpoch = epoch;
    }
    internal EntityUid ActorEntity { get; }
    public NCPartyRef Actor { get; }
    public NCPartyRef Owner { get; }
    public NCPlayerEmploymentProof? Employment { get; }
    internal long EmploymentGeneration { get; }
    internal long PrototypeGeneration { get; }
    internal long ExpiresAtTicks { get; }
    internal NCSessionEpoch WorldEpoch { get; }
}

/// <summary>One adapter over native personnel and party authority; it owns no organization/member database.</summary>
public sealed partial class NCMetaAuthoritySystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private NCPartyResolverSystem _partyResolver = default!;
    [Dependency] private NCPartyDirectorySystem _directory = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaNetworkSystem _network = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _tasks = default!;
    private readonly SemaphoreSlim _reads = new(8, 8);
    private readonly Dictionary<(EntityUid Actor, string Owner), NCMetaManagementProof> _management = [];
    private long _prototypeGeneration;
    private int _managementDirty;
    public event Action? ManagementAuthorityChanged;
    public event Action<Guid>? ServiceAuthorityChanged;

    public override void Initialize()
    {
        base.Initialize();
        _database.NCPlayerEmploymentChanged += OnPlayerEmploymentChanged;
        _devices.Changed += OnDeviceChanged;
        _devices.RegisterManagementAuthority(CanActFor);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        SubscribeLocalEvent<NCPartyDirectoryPersistedEvent>(OnPartyChanged);
        SubscribeLocalEvent<NCCityActorPersistedEvent>(OnPrincipalActorChanged);
    }
    public override void Shutdown()
    {
        _database.NCPlayerEmploymentChanged -= OnPlayerEmploymentChanged;
        _devices.Changed -= OnDeviceChanged;
        _management.Clear(); ClearServicePolicies();
        base.Shutdown();
    }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (Interlocked.Exchange(ref _managementDirty, 0) != 0)
        {
            _management.Clear();
            if (ManagementAuthorityChanged != null)
                foreach (Action subscriber in ManagementAuthorityChanged.GetInvocationList())
                    try { subscriber(); }
                    catch (Exception exception) { Log.Error($"META management invalidation failed: {exception}"); }
        }
        UpdateServiceExpiry();
    }
    private void OnPlayerEmploymentChanged() => Interlocked.Exchange(ref _managementDirty, 1);
    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        _prototypeGeneration = checked(_prototypeGeneration + 1);
        _management.Clear(); ClearServicePolicies();
        Interlocked.Exchange(ref _managementDirty, 1);
    }
    private void OnPartyChanged(NCPartyDirectoryPersistedEvent args)
    {
        _management.Clear(); ClearServicePolicies();
        Interlocked.Exchange(ref _managementDirty, 1);
    }

    private void OnPrincipalActorChanged(NCCityActorPersistedEvent args)
    {
        if (args.Status is not (NCCityActorStatus.Dead or NCCityActorStatus.Archived)) return;
        var principal = NCPartyRef.ForActor(args.ActorId);
        if (_policyPrincipals.TryGetValue(principal.ToString(), out var policies))
            foreach (var id in policies.ToArray()) InvalidateServicePolicy(id);
        foreach (var key in _management.Where(value => value.Value.Owner == principal).Select(value => value.Key).ToArray())
            _management.Remove(key);
    }

    private bool IsPrincipalActive(NCPartyRef owner) => owner.Kind switch
    {
        NCPartyKind.Actor => _actors.TryGetCached(new(owner.PersistentId), out var actor) &&
            actor.Snapshot.Lifecycle.Status is not (NCCityActorStatus.Dead or NCCityActorStatus.Archived),
        NCPartyKind.Organization => _directory.TryGetCached(owner, out var organization) && organization.Snapshot.Lifecycle == NCPartyLifecycle.Active,
        _ => true,
    };

    public async Task<NCMetaManagementProof?> CaptureManagementAsync(EntityUid actor, string ownerKey)
    {
        if (!NCPartyRef.TryParse(ownerKey, out var owner) || owner.ToString() != ownerKey || !await _reads.WaitAsync(0)) return null;
        try
        {
            var captured = await Main(() => _parties.TryResolve(actor, out var party) &&
                _persistence.TryCaptureTime(out var epoch, out var time)
                    ? (Valid: true, Actor: party, Epoch: epoch, Time: time, Employment: _database.NCPlayerEmploymentGeneration,
                        Prototypes: _prototypeGeneration) : default);
            if (!captured.Valid) return null;
            NCPlayerEmploymentProof? employment = null;
            if (captured.Actor != owner)
            {
                if (owner.Kind != NCPartyKind.Organization || !captured.Actor.TryGetCharacter(out var character)) return null;
                employment = await _database.GetNCPlayerEmploymentProofAsync(character);
                if (employment == null || employment.LastEventId <= 0) return null;
            }
            if (captured.Actor != owner && !(await _partyResolver.ResolveAsync(owner, requireActive: true)).IsResolved) return null;
            return await Main(() =>
            {
                var proof = new NCMetaManagementProof(actor, captured.Actor, owner, employment, captured.Employment,
                    captured.Prototypes, checked(captured.Time.Ticks + TimeSpan.FromSeconds(30).Ticks), captured.Epoch);
                if (!IsCurrent(proof) || employment != null && !IsNativeManager(owner, employment.JobPrototypeId)) return null;
                if (_management.Count >= 512 && !_management.ContainsKey((actor, ownerKey)))
                    _management.Remove(_management.Keys.First());
                _management[(actor, ownerKey)] = proof;
                return proof;
            });
        }
        finally { _reads.Release(); }
    }

    /// <summary>Main-thread native check. DB notification increments the generation before queued UI cleanup.</summary>
    public bool IsCurrent(NCMetaManagementProof proof) => IsManagementCurrent(proof, requireFreshCapture: true);

    public bool CanAttendService(NCMetaManagementProof proof) => IsManagementCurrent(proof, requireFreshCapture: false) &&
        TryComp<ActorComponent>(proof.ActorEntity, out var actor) && actor.PlayerSession.Status == SessionStatus.InGame &&
        actor.PlayerSession.AttachedEntity == proof.ActorEntity;

    private bool IsManagementCurrent(NCMetaManagementProof proof, bool requireFreshCapture) => _parties.TryResolve(proof.ActorEntity, out var actor) &&
        actor == proof.Actor && _persistence.TryCaptureTime(out var epoch, out var time) && epoch == proof.WorldEpoch &&
        (!requireFreshCapture || time.Ticks < proof.ExpiresAtTicks) && proof.PrototypeGeneration == _prototypeGeneration &&
        (proof.Employment == null ? proof.Actor == proof.Owner :
            proof.EmploymentGeneration == _database.NCPlayerEmploymentGeneration && IsNativeManager(proof.Owner, proof.Employment.JobPrototypeId)) &&
        IsPrincipalActive(proof.Owner);

    public bool CanActFor(EntityUid actor, string owner) => _parties.TryResolve(actor, out var party) &&
        (party.ToString() == owner || _management.TryGetValue((actor, owner), out var proof) && IsCurrent(proof));

    public bool TryGetManagementProof(EntityUid actor, string ownerKey, out NCMetaManagementProof proof)
    {
        if (_management.TryGetValue((actor, ownerKey), out proof!) && IsCurrent(proof)) return true;
        if (!_parties.TryResolve(actor, out var party) || party.ToString() != ownerKey ||
            !_persistence.TryCaptureTime(out var epoch, out var time)) { proof = default!; return false; }
        proof = new(actor, party, party, null, _database.NCPlayerEmploymentGeneration, _prototypeGeneration,
            checked(time.Ticks + TimeSpan.FromSeconds(30).Ticks), epoch);
        if (_management.Count >= 512 && !_management.ContainsKey((actor, ownerKey)))
            _management.Remove(_management.Keys.First());
        _management[(actor, ownerKey)] = proof;
        return true;
    }

    public async Task<NCMetaManagementProof?> CaptureWorkspaceManagementAsync(EntityUid terminal, EntityUid actor)
    {
        var owner = await Main(() => _ui.IsUiOpen(terminal, NCMetaUiKey.Workspace, actor) &&
            _devices.TryGetEntry(terminal, out var device) && device.Ready && _devices.IsPowered(terminal) &&
            _interaction.InRangeUnobstructed(actor, terminal, range: 2) ? device.Owner : null);
        if (owner == null) return null;
        var proof = await CaptureManagementAsync(actor, owner);
        return proof != null && await Main(() => IsCurrent(proof) && CanUseTerminal(terminal, actor)) ? proof : null;
    }

    private bool IsNativeManager(NCPartyRef owner, string job)
    {
        // This bounded projection is derived entirely from authored native department policy.
        var departments = _prototypes.EnumeratePrototypes<DepartmentPrototype>().Take(257).ToArray();
        return departments.Length <= 256 && departments.Any(department =>
            NCPersistentIdentity.Organization(department.ID) == owner && department.NCPersonnelManagers.Contains(new ProtoId<JobPrototype>(job)));
    }
    private bool CanUseTerminal(EntityUid terminal, EntityUid actor) =>
        _ui.IsUiOpen(terminal, NCMetaUiKey.Workspace, actor) && _devices.TryGetEntry(terminal, out var entry) &&
        entry.Ready && _devices.IsPowered(terminal) && CanActFor(actor, entry.Owner) &&
        _interaction.InRangeUnobstructed(actor, terminal, range: 2);

    private Task<T> Main<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() => { try { completion.SetResult(action()); } catch (Exception exception) { completion.SetException(exception); } });
        return completion.Task;
    }
}
