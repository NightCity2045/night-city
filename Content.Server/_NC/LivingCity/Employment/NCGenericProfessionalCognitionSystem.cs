// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Affordances;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.LivingCity.Employment;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Employment;

/// <summary>
/// Executes ordinary mobile professional duty for every organization through the common human cognition pipeline.
/// Destinations come from the geography-derived affordance catalog, so organizations never need bespoke mapper
/// anchors or a separate NPC brain.
/// </summary>
public sealed partial class NCGenericProfessionalCognitionSystem : EntitySystem
{
    private static readonly ProtoId<NCGenericProfessionalWorkPolicyPrototype> DefaultPolicy =
        "NCDefaultGenericProfessionalWorkPolicy";
    private const string SteeringOwner = "living-city-generic-professional-work";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;
    [Dependency] private NCCityLocationSystem _locations = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCCityNpcSteeringLeaseSystem _steeringLease = default!;
    [Dependency] private NCCityPhysicalExecutionSystem _physical = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private HTNSystem _htn = default!;

    private readonly Dictionary<EntityUid, NCMapCoordinates> _targets = [];
    private readonly Dictionary<EntityUid, uint> _cycles = [];
    private readonly HashSet<EntityUid> _steeringActors = [];
    private readonly HashSet<EntityUid> _suspendedHtn = [];
    private readonly List<NCCityAffordanceInfo> _candidateBuffer = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCProfessionalWorkRequestedEvent>(OnProfessionalWork);
        SubscribeLocalEvent<NCCityActorBindingComponent, ComponentShutdown>(OnActorShutdown);
    }

    private void OnProfessionalWork(NCProfessionalWorkRequestedEvent args)
    {
        if (!_prototypes.TryIndex(DefaultPolicy, out var policy) ||
            !args.Profile.CapabilityOverlays.Contains(policy.Capability))
            return;

        args.Handled = true;
        var worker = args.Worker;
        if (!Exists(worker))
        {
            args.Failure = "professional-worker-unavailable";
            return;
        }

        if (!_targets.TryGetValue(worker, out var target))
        {
            if (!TrySelectTarget(args, policy, out target))
            {
                // A map without semantic geography is valid for isolated developer tests. The employee remains
                // available at work instead of inventing an unreachable point or scanning the whole map.
                args.Completed = true;
                return;
            }
            _targets[worker] = target;
        }

        if (IsReached(worker, target, policy.ArrivalRange))
        {
            Stop(worker);
            _cycles[worker] = _cycles.GetValueOrDefault(worker) + 1;
            args.Completed = true;
            return;
        }

        if (TryComp<NPCSteeringComponent>(worker, out var steering) && steering.Status == SteeringStatus.NoPath)
        {
            Stop(worker);
            args.Failure = "professional-duty-target-unreachable";
            return;
        }
        if (!BeginApproach(worker, target))
            args.Failure = "professional-locomotion-busy";
    }

    private bool TrySelectTarget(NCProfessionalWorkRequestedEvent args,
        NCGenericProfessionalWorkPolicyPrototype policy, out NCMapCoordinates target)
    {
        target = default;
        _locations.EnsureTracked(args.Worker);
        var within = default(NCZoneId);
        if (_locations.TryGetContext(args.Worker, out var context))
        {
            within = args.Profile.WorkAffordance == NCCityAffordanceKind.PatrolArea && context.StreetId.IsValid
                ? context.StreetId.ZoneId
                : context.MostSpecific;
        }
        _affordances.Get(args.Profile.WorkAffordance, within, _candidateBuffer);
        var count = Math.Min(policy.MaximumCatalogCandidates, _candidateBuffer.Count);
        if (count == 0)
            return false;
        var seed = StableSeed(args.ActorId.Value, _cycles.GetValueOrDefault(args.Worker));
        target = _candidateBuffer[(int) (seed % (uint) count)].Target;
        return _coordinates.TryResolve(target, out _);
    }

    private bool BeginApproach(EntityUid worker, NCMapCoordinates target)
    {
        if (!TryResolveEntityCoordinates(target, out var destination) ||
            _steeringLease.HasOtherOwner(worker, SteeringOwner) || !_steeringLease.Acquire(worker, SteeringOwner))
            return false;
        if (_suspendedHtn.Add(worker) && TryComp<HTNComponent>(worker, out var htn) && htn.Enabled)
            _htn.SetHTNEnabled((worker, htn), false);
        _physical.SetInterrupt(worker, NCCityPhysicalInterruptFlags.Work, true);
        _steeringActors.Add(worker);
        _steering.Register(worker, destination);
        return true;
    }

    private bool TryResolveEntityCoordinates(NCMapCoordinates target, out EntityCoordinates coordinates)
    {
        coordinates = default;
        if (!_coordinates.TryResolve(target, out var mapCoordinates) ||
            !_maps.TryGetMap(mapCoordinates.MapId, out var mapUid) || mapUid is not { } map ||
            !_maps.TryFindGridAt(map, mapCoordinates.Position, out var gridUid, out _))
            return false;
        coordinates = _transform.ToCoordinates((gridUid, null), mapCoordinates);
        return true;
    }

    private bool IsReached(EntityUid worker, NCMapCoordinates target, float range) =>
        _coordinates.TryGetCoordinates(worker, out var current) && current.NetworkId == target.NetworkId &&
        current.Z == target.Z && Vector2.DistanceSquared(current.Position, target.Position) <= range * range;

    private void OnActorShutdown(Entity<NCCityActorBindingComponent> actor, ref ComponentShutdown args)
    {
        Stop(actor.Owner, false);
        _cycles.Remove(actor.Owner);
    }

    private void Stop(EntityUid worker, bool restoreHtn = true)
    {
        _targets.Remove(worker);
        if (_steeringActors.Remove(worker) && Exists(worker))
        {
            _steering.Unregister(worker);
            _steeringLease.Release(worker, SteeringOwner);
        }
        if (!restoreHtn || !Exists(worker))
        {
            _suspendedHtn.Remove(worker);
            return;
        }
        _physical.SetInterrupt(worker, NCCityPhysicalInterruptFlags.Work, false);
        if (_suspendedHtn.Remove(worker) && TryComp<HTNComponent>(worker, out var htn))
            _htn.SetHTNEnabled((worker, htn), true, htn.PlanCooldown);
    }

    private static uint StableSeed(Guid actorId, uint cycle)
    {
        Span<byte> bytes = stackalloc byte[16];
        actorId.TryWriteBytes(bytes);
        var hash = 2166136261u;
        foreach (var value in bytes)
            hash = (hash ^ value) * 16777619u;
        return (hash ^ cycle) * 16777619u;
    }
}
