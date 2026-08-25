// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Affordances;
using Content.Server._NC.LivingCity.CityServices;
using Content.Server._NC.LivingCity.Cognition;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server.NPC.Components;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared.Access.Systems;
using Content.Shared.Contraband;
using Content.Shared.Cuffs;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Storage;
using Content.Shared.Storage.Components;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Police.NCPD;

/// <summary>
/// Searches only a restrained suspect's bounded physical inventory and transfers dangerous items through vanilla
/// inventory, hands and storage systems. Persistent incident evidence is written only after physical custody exists.
/// </summary>
public sealed partial class NCNcpdEvidenceSystem : EntitySystem
{
    private static readonly ProtoId<NCNcpdDetentionPolicyPrototype> DefaultPolicy =
        "NCDefaultNcpdDetentionPolicy";

    private static readonly HashSet<string> SupportedActions =
    [
        "ncpd-search-restrained-suspect",
        "ncpd-seize-dangerous-items",
        "ncpd-deliver-evidence-storage",
        "ncpd-submit-evidence",
    ];

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityLocalPerceptionSystem _perception = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private NCCityIncidentRegistrySystem _incidents = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCNcpdAssignmentExecutionSystem _assignments = default!;
    [Dependency] private SharedCuffableSystem _cuffable = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private SharedEntityStorageSystem _entityStorage = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedMapSystem _maps = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private NCCityNpcSteeringLeaseSystem _steeringLease = default!;
    [Dependency] private NCCityPhysicalExecutionSystem _physical = default!;
    [Dependency] private NPCSteeringSystem _steering = default!;
    [Dependency] private HTNSystem _htn = default!;

    private readonly Dictionary<Guid, RemovalAttempt> _removals = [];
    private readonly Dictionary<Guid, PendingEvidenceMutation> _pending = [];
    private readonly Dictionary<Guid, EvidenceRoute> _routes = [];
    private readonly HashSet<EntityUid> _steeringActors = [];
    private readonly HashSet<EntityUid> _suspendedHtn = [];
    private readonly HashSet<EntityUid> _receiverCandidates = [];
    private readonly List<NCCityAffordanceInfo> _affordanceCandidates = [];

    public int PendingEvidenceMutations => _pending.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanActionExecutionEvent>(OnExecute);
        SubscribeLocalEvent<NCHumanActionInterruptedEvent>(OnInterrupted);
        SubscribeLocalEvent<NCNcpdOfficerComponent, ComponentShutdown>(OnOfficerShutdown);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Clear());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Clear());
    }

    private void OnExecute(NCHumanActionExecutionEvent args)
    {
        if (!SupportedActions.Contains(args.Step.ActionId) || args.EntityUid is not { } officer ||
            !Exists(officer) || !TryGetPolicyAndAuthority(officer, out var policy))
        {
            return;
        }

        args.Handled = true;
        _assignments.YieldToProfessionalAction(officer);
        if (!_clock.TryGetCurrentTime(out _, out var now))
        {
            args.Failure = "ncpd-evidence-clock-unavailable";
            return;
        }
        if (args.Step.ActionId == "ncpd-deliver-evidence-storage")
        {
            ExecuteEvidenceDelivery(args, officer, policy, now);
            return;
        }
        if (args.Step.ActionId == "ncpd-submit-evidence")
        {
            ExecuteEvidenceSubmission(args, officer, policy, now);
            return;
        }

        string? failure = null;
        if (args.Target.Kind != NCCognitionReferenceKind.StableKey ||
            !_perception.TryResolveSubjectHandle(args.ActorId, args.Target.StableId, now, out var suspect) ||
            !TryValidateBasis(officer, suspect, out var claim, out var suspectParty, out failure))
        {
            args.Failure = failure ?? "ncpd-evidence-target-unavailable";
            return;
        }
        if (!_interaction.InRangeUnobstructed(officer, suspect, policy.SearchRange))
        {
            args.Failure = "ncpd-search-target-out-of-range";
            return;
        }

        switch (args.Step.ActionId)
        {
            case "ncpd-search-restrained-suspect":
                ExecuteSearch(args, officer, suspect, suspectParty, claim.IncidentId, policy, now);
                return;
            case "ncpd-seize-dangerous-items":
                ExecuteSeizure(args, officer, suspect, suspectParty, claim.IncidentId, policy, now);
                return;
        }
    }

    private void ExecuteEvidenceDelivery(
        NCHumanActionExecutionEvent args,
        EntityUid officer,
        NCNcpdDetentionPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        if (!TryComp<NCNcpdOfficerComponent>(officer, out var officerComponent) ||
            !TryComp<NCNcpdEvidenceSearchComponent>(officer, out var search) ||
            search.Status != NCNcpdEvidenceSearchStatus.Complete)
        {
            args.Failure = "ncpd-completed-evidence-search-unavailable";
            return;
        }

        search.SeizedItems.RemoveAll(item => !Exists(item) ||
            !TryComp<NCNcpdEvidenceItemComponent>(item, out var evidence) ||
            evidence.Status != NCNcpdEvidenceCustodyStatus.Recorded ||
            !_transform.ContainsEntity(officer, item));
        if (search.SeizedItems.Count == 0)
        {
            var empty = EnsureComp<NCNcpdEvidenceSubmissionComponent>(officer);
            empty.IncidentId = search.IncidentId;
            empty.Status = NCNcpdEvidenceSubmissionStatus.Complete;
            empty.SubmittedItems = 0;
            empty.UpdatedAt = now;
            empty.Failure = string.Empty;
            args.Completed = true;
            return;
        }

        if (!_routes.TryGetValue(args.InstanceId, out var route))
        {
            if (!TrySelectEvidenceReceiver(officer, officerComponent.StationZone, StableSeed(args.InstanceId), policy,
                    out var receiver, out var target))
            {
                FailSubmission(officer, search.IncidentId, now, "ncpd-physical-evidence-storage-unavailable", args);
                return;
            }
            route = new EvidenceRoute(officer, receiver, target, 0, now);
            _routes.Add(args.InstanceId, route);
            var submission = EnsureComp<NCNcpdEvidenceSubmissionComponent>(officer);
            submission.IncidentId = search.IncidentId;
            submission.Status = NCNcpdEvidenceSubmissionStatus.EnRoute;
            submission.Destination = target;
            submission.Receiver = receiver;
            submission.SubmittedItems = 0;
            submission.UpdatedAt = now;
            submission.Failure = string.Empty;
        }

        if (TryComp<NPCSteeringComponent>(officer, out var steering) && steering.Status == SteeringStatus.NoPath)
        {
            StopRoute(args.InstanceId, officer);
            route = route with { Replans = route.Replans + 1, RetryAt = now.Advance(policy.RouteRetryDelay) };
            if (route.Replans > policy.MaximumRouteReplans)
            {
                FailSubmission(officer, search.IncidentId, now, "ncpd-evidence-storage-route-unavailable", args);
                return;
            }
            _routes[args.InstanceId] = route;
            return;
        }
        if (now < route.RetryAt)
            return;
        if (IsReached(officer, route.Target, policy.ArrivalRange))
        {
            StopRoute(args.InstanceId, officer);
            var submission = EnsureComp<NCNcpdEvidenceSubmissionComponent>(officer);
            submission.Status = NCNcpdEvidenceSubmissionStatus.AtStorage;
            submission.UpdatedAt = now;
            submission.Failure = string.Empty;
            args.Completed = true;
            return;
        }

        BeginApproach(officer, route.Target);
    }

    private void ExecuteEvidenceSubmission(
        NCHumanActionExecutionEvent args,
        EntityUid officer,
        NCNcpdDetentionPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        if (!TryComp<NCNcpdEvidenceSearchComponent>(officer, out var search) ||
            !TryComp<NCNcpdEvidenceSubmissionComponent>(officer, out var submission))
        {
            args.Failure = "ncpd-evidence-storage-arrival-unconfirmed";
            return;
        }
        if (submission.IncidentId == search.IncidentId &&
            submission.Status == NCNcpdEvidenceSubmissionStatus.Complete)
        {
            args.Completed = true;
            return;
        }
        if (
            submission.IncidentId != search.IncidentId ||
            submission.Status is not (NCNcpdEvidenceSubmissionStatus.AtStorage or
                NCNcpdEvidenceSubmissionStatus.Submitting))
        {
            args.Failure = "ncpd-evidence-storage-arrival-unconfirmed";
            return;
        }
        if (!Exists(submission.Receiver) || !Transform(submission.Receiver).Anchored ||
            !_access.IsAllowed(officer, submission.Receiver) ||
            !_interaction.InRangeUnobstructed(officer, submission.Receiver, policy.SearchRange))
        {
            FailSubmission(officer, search.IncidentId, now, "ncpd-evidence-receiver-unavailable", args);
            return;
        }

        submission.Status = NCNcpdEvidenceSubmissionStatus.Submitting;
        submission.UpdatedAt = now;
        foreach (var item in search.SeizedItems.ToArray())
        {
            if (!Exists(item) || !TryComp<NCNcpdEvidenceItemComponent>(item, out var evidence) ||
                evidence.IncidentId != search.IncidentId || evidence.Status != NCNcpdEvidenceCustodyStatus.Recorded ||
                !_transform.ContainsEntity(officer, item))
            {
                FailSubmission(officer, search.IncidentId, now, "ncpd-evidence-chain-broken", args);
                return;
            }
            if (!TryInsertEvidenceItem(officer, submission.Receiver, item) ||
                !_transform.ContainsEntity(submission.Receiver, item))
            {
                FailSubmission(officer, search.IncidentId, now, "ncpd-evidence-receiver-capacity-unavailable", args);
                return;
            }

            evidence.Status = NCNcpdEvidenceCustodyStatus.Submitted;
            evidence.UpdatedAt = now;
            evidence.Failure = string.Empty;
            submission.SubmittedItems++;
        }

        submission.Status = NCNcpdEvidenceSubmissionStatus.Complete;
        submission.UpdatedAt = now;
        submission.Failure = string.Empty;
        args.Completed = true;
    }

    private void ExecuteSearch(
        NCHumanActionExecutionEvent args,
        EntityUid officer,
        EntityUid suspect,
        NCPartyRef suspectParty,
        NCCityIncidentId incidentId,
        NCNcpdDetentionPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        if (TryComp<NCNcpdEvidenceSearchComponent>(officer, out var existing) &&
            existing.Suspect == suspect && existing.SuspectParty == suspectParty &&
            existing.IncidentId == incidentId && existing.Status != NCNcpdEvidenceSearchStatus.Failed)
        {
            args.Completed = true;
            return;
        }

        var search = EnsureComp<NCNcpdEvidenceSearchComponent>(officer);
        search.IncidentId = incidentId;
        search.Suspect = suspect;
        search.SuspectParty = suspectParty;
        search.Status = NCNcpdEvidenceSearchStatus.Searching;
        search.InspectedItems = 0;
        search.DangerousItems.Clear();
        search.SeizedItems.Clear();
        search.UpdatedAt = now;
        search.Failure = string.Empty;

        var pending = new Queue<(EntityUid Entity, int Depth)>();
        var visited = new HashSet<EntityUid>();
        if (TryComp<HandsComponent>(suspect, out var hands))
        {
            foreach (var held in _hands.EnumerateHeld((suspect, hands)))
                pending.Enqueue((held, 0));
        }
        var slots = _inventory.GetSlotEnumerator(suspect);
        while (slots.NextItem(out var equipped, out _))
            pending.Enqueue((equipped, 0));

        // Traversal starts only from real hands/equipment and is bounded by both item count and YAML depth.
        while (pending.TryDequeue(out var entry) && visited.Count < policy.MaximumSearchItems)
        {
            if (!Exists(entry.Entity) || !visited.Add(entry.Entity))
                continue;
            if (HasComp<ItemComponent>(entry.Entity) && IsDangerous(entry.Entity, policy))
                search.DangerousItems.Add(entry.Entity);
            if (entry.Depth >= policy.MaximumSearchDepth ||
                !TryComp<ContainerManagerComponent>(entry.Entity, out var manager))
            {
                continue;
            }

            foreach (var container in _containers.GetAllContainers(entry.Entity, manager))
            {
                foreach (var contained in container.ContainedEntities)
                    pending.Enqueue((contained, entry.Depth + 1));
            }
        }

        search.InspectedItems = visited.Count;
        search.UpdatedAt = now;
        if (pending.Count > 0)
        {
            FailSearch(search, now, "ncpd-search-item-bound-exceeded", args);
            return;
        }
        if (search.DangerousItems.Count > policy.MaximumSeizedItems)
        {
            FailSearch(search, now, "ncpd-seizure-item-bound-exceeded", args);
            return;
        }

        search.Status = NCNcpdEvidenceSearchStatus.SearchComplete;
        args.Completed = true;
    }

    private void ExecuteSeizure(
        NCHumanActionExecutionEvent args,
        EntityUid officer,
        EntityUid suspect,
        NCPartyRef suspectParty,
        NCCityIncidentId incidentId,
        NCNcpdDetentionPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        if (!TryComp<NCNcpdEvidenceSearchComponent>(officer, out var search) || search.Suspect != suspect ||
            search.SuspectParty != suspectParty || search.IncidentId != incidentId ||
            search.Status == NCNcpdEvidenceSearchStatus.Failed)
        {
            args.Failure = "ncpd-search-result-unavailable";
            return;
        }
        search.Status = NCNcpdEvidenceSearchStatus.Seizing;
        search.UpdatedAt = now;

        if (_pending.TryGetValue(args.InstanceId, out var pendingMutation))
        {
            CompletePendingMutation(args, search, pendingMutation, now);
            return;
        }

        while (search.DangerousItems.Count > 0)
        {
            var item = search.DangerousItems[0];
            if (!Exists(item) || !IsDangerous(item, policy))
            {
                search.DangerousItems.RemoveAt(0);
                continue;
            }

            var evidence = EnsureEvidenceItem(item, incidentId, suspectParty, officer, now, out var evidenceFailure);
            if (evidence == null)
            {
                FailSearch(search, now, evidenceFailure ?? "ncpd-evidence-identity-unavailable", args);
                return;
            }
            if (evidence.Status == NCNcpdEvidenceCustodyStatus.Recorded &&
                _transform.ContainsEntity(officer, item))
            {
                search.DangerousItems.RemoveAt(0);
                if (!search.SeizedItems.Contains(item))
                    search.SeizedItems.Add(item);
                continue;
            }

            if (TryFindEquippedSlot(suspect, item, out var slot))
            {
                if (_removals.TryGetValue(args.InstanceId, out var attempt) && attempt.Item == item &&
                    now < attempt.RetryAt)
                {
                    return;
                }
                evidence.Status = NCNcpdEvidenceCustodyStatus.Removing;
                evidence.UpdatedAt = now;
                _removals[args.InstanceId] = new RemovalAttempt(officer, item, slot,
                    now.Advance(policy.SearchInteractionRetryDelay));
                // Vanilla inventory owns access checks and any clothing unequip DoAfter.
                if (!_inventory.TryUnequip(officer, suspect, slot, out _, silent: true, checkDoafter: true,
                        triggerHandContact: true))
                {
                    return;
                }
            }

            _removals.Remove(args.InstanceId);
            if (!TryPlaceInOfficerCustody(officer, item))
            {
                evidence.Status = NCNcpdEvidenceCustodyStatus.Failed;
                evidence.UpdatedAt = now;
                evidence.Failure = "ncpd-officer-evidence-capacity-unavailable";
                FailSearch(search, now, evidence.Failure, args);
                return;
            }

            evidence.Status = NCNcpdEvidenceCustodyStatus.Persisting;
            evidence.UpdatedAt = now;
            evidence.Failure = string.Empty;
            var kind = HasComp<GunComponent>(item) || HasComp<MeleeWeaponComponent>(item)
                ? NCIncidentEvidenceKind.WeaponTrace
                : NCIncidentEvidenceKind.Physical;
            var prototype = MetaData(item).EntityPrototype?.ID ?? "unknown-item";
            var reference = $"{evidence.EvidenceId:D}:{prototype}";
            _pending[args.InstanceId] = new PendingEvidenceMutation(
                officer,
                item,
                PersistEvidenceAsync(incidentId, evidence.EvidenceId, evidence.SeizedBy, kind, reference,
                    policy.PhysicalEvidenceReliability, policy.PhysicalEvidenceAdmissible));
            return;
        }

        search.Status = NCNcpdEvidenceSearchStatus.Complete;
        search.UpdatedAt = now;
        search.Failure = string.Empty;
        args.Completed = true;
    }

    private void CompletePendingMutation(
        NCHumanActionExecutionEvent args,
        NCNcpdEvidenceSearchComponent search,
        PendingEvidenceMutation pending,
        NCActiveWorldTime now)
    {
        if (!pending.Operation.IsCompleted)
            return;
        _pending.Remove(args.InstanceId);
        var result = pending.Operation.GetAwaiter().GetResult();
        if (!Exists(pending.Item) || !TryComp<NCNcpdEvidenceItemComponent>(pending.Item, out var evidence))
        {
            FailSearch(search, now, "ncpd-physical-evidence-disappeared", args);
            return;
        }
        if (!result.Success)
        {
            evidence.Status = NCNcpdEvidenceCustodyStatus.Failed;
            evidence.UpdatedAt = now;
            evidence.Failure = result.Error ?? "ncpd-evidence-persistence-rejected";
            FailSearch(search, now, evidence.Failure, args);
            return;
        }

        evidence.Status = NCNcpdEvidenceCustodyStatus.Recorded;
        evidence.UpdatedAt = now;
        evidence.Failure = string.Empty;
        search.DangerousItems.Remove(pending.Item);
        if (!search.SeizedItems.Contains(pending.Item))
            search.SeizedItems.Add(pending.Item);
        search.UpdatedAt = now;
    }

    private NCNcpdEvidenceItemComponent? EnsureEvidenceItem(
        EntityUid item,
        NCCityIncidentId incidentId,
        NCPartyRef suspectParty,
        EntityUid officer,
        NCActiveWorldTime now,
        out string? failure)
    {
        failure = null;
        if (!_parties.TryResolve(officer, out var officerParty))
        {
            failure = "ncpd-officer-party-unavailable";
            return null;
        }

        var evidence = EnsureComp<NCNcpdEvidenceItemComponent>(item);
        if (evidence.EvidenceId != Guid.Empty && evidence.IncidentId != incidentId)
        {
            failure = "ncpd-item-belongs-to-another-evidence-chain";
            return null;
        }
        if (evidence.EvidenceId == Guid.Empty)
        {
            evidence.EvidenceId = Guid.NewGuid();
            evidence.IncidentId = incidentId;
            evidence.SeizedFrom = suspectParty;
            evidence.SeizedBy = officerParty;
            evidence.Status = NCNcpdEvidenceCustodyStatus.Identified;
            evidence.UpdatedAt = now;
            evidence.Failure = string.Empty;
        }
        return evidence;
    }

    private bool TryPlaceInOfficerCustody(EntityUid officer, EntityUid item)
    {
        if (_transform.ContainsEntity(officer, item))
            return true;

        // Prefer real equipped storage so an officer can retain hands for restraints and pulling.
        var slots = _inventory.GetSlotEnumerator(officer);
        while (slots.NextItem(out var equipped, out _))
        {
            if (!TryComp<StorageComponent>(equipped, out var storage) ||
                !_storage.CanInsert(equipped, item, out _, storage))
            {
                continue;
            }
            if (_storage.Insert(equipped, item, out _, user: officer, storageComp: storage))
                return _transform.ContainsEntity(officer, item);
        }

        return _hands.TryPickupAnyHand(officer, item) && _transform.ContainsEntity(officer, item);
    }

    private bool TryFindEvidenceReceiver(
        EntityUid officer,
        NCMapCoordinates generatedTarget,
        NCNcpdDetentionPolicyPrototype policy,
        out EntityUid receiver)
    {
        receiver = EntityUid.Invalid;
        if (!_coordinates.TryResolve(generatedTarget, out var target))
            return false;

        _receiverCandidates.Clear();
        _lookup.GetEntitiesInRange(target.MapId, target.Position, policy.EvidenceStorageSearchRadius,
            _receiverCandidates);
        var nearestDistance = float.MaxValue;
        foreach (var candidate in _receiverCandidates)
        {
            if (!Exists(candidate) || !Transform(candidate).Anchored || !_access.IsAllowed(officer, candidate) ||
                MetaData(candidate).EntityPrototype?.ID is not { } prototype ||
                !policy.EvidenceStorageReceivers.Contains(prototype) ||
                !HasComp<StorageComponent>(candidate) && !HasComp<EntityStorageComponent>(candidate))
            {
                continue;
            }

            var candidateCoordinates = _transform.GetMapCoordinates(candidate);
            if (candidateCoordinates.MapId != target.MapId)
                continue;
            var distance = Vector2.DistanceSquared(candidateCoordinates.Position, target.Position);
            if (distance >= nearestDistance)
                continue;
            nearestDistance = distance;
            receiver = candidate;
        }
        _receiverCandidates.Clear();
        return receiver.IsValid();
    }

    private bool TrySelectEvidenceReceiver(
        EntityUid officer,
        Content.Shared._NC.City.Zones.NCZoneId stationZone,
        uint seed,
        NCNcpdDetentionPolicyPrototype policy,
        out EntityUid receiver,
        out NCMapCoordinates target)
    {
        receiver = EntityUid.Invalid;
        target = default;
        _affordances.Get(NCCityAffordanceKind.EvidenceStorage, stationZone, _affordanceCandidates);
        if (_affordanceCandidates.Count == 0)
            return false;

        var start = (int) (seed % (uint) _affordanceCandidates.Count);
        for (var offset = 0; offset < _affordanceCandidates.Count; offset++)
        {
            var entry = _affordanceCandidates[(start + offset) % _affordanceCandidates.Count];
            if (!TryFindEvidenceReceiver(officer, entry.Target, policy, out receiver) ||
                !_coordinates.TryGetCoordinates(receiver, out target))
            {
                continue;
            }
            return true;
        }
        return false;
    }

    private bool TryInsertEvidenceItem(EntityUid officer, EntityUid receiver, EntityUid item)
    {
        if (TryComp<StorageComponent>(receiver, out var storage) &&
            _storage.CanInsert(receiver, item, out _, storage))
        {
            return _storage.Insert(receiver, item, out _, user: officer, storageComp: storage);
        }
        if (TryComp<EntityStorageComponent>(receiver, out var entityStorage) && !entityStorage.Open &&
            _entityStorage.CanInsert(item, receiver, entityStorage))
        {
            return _entityStorage.Insert(item, receiver, entityStorage);
        }
        return false;
    }

    private bool BeginApproach(EntityUid officer, NCMapCoordinates target)
    {
        // The route is registered only once; re-registering each action pulse recreates expensive vanilla replans.
        if (_steeringActors.Contains(officer))
            return true;
        if (!TryResolveEntityCoordinates(target, out var destination) ||
            _steeringLease.HasOtherOwner(officer, "ncpd-evidence") ||
            !_steeringLease.Acquire(officer, "ncpd-evidence"))
        {
            return false;
        }
        if (_suspendedHtn.Add(officer) && TryComp<HTNComponent>(officer, out var htn) && htn.Enabled)
            _htn.SetHTNEnabled((officer, htn), false);
        _physical.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, true);
        _steeringActors.Add(officer);
        _steering.Register(officer, destination);
        return true;
    }

    private bool TryResolveEntityCoordinates(NCMapCoordinates target, out EntityCoordinates coordinates)
    {
        coordinates = default;
        if (!_coordinates.TryResolve(target, out var mapCoordinates) ||
            !_maps.TryGetMap(mapCoordinates.MapId, out var mapUid) || mapUid is not { } map ||
            !_maps.TryFindGridAt(map, mapCoordinates.Position, out var gridUid, out _))
        {
            return false;
        }
        coordinates = _transform.ToCoordinates((gridUid, null), mapCoordinates);
        return true;
    }

    private bool IsReached(EntityUid officer, NCMapCoordinates target, float range) =>
        _coordinates.TryGetCoordinates(officer, out var current) && current.NetworkId == target.NetworkId &&
        current.Z == target.Z && Vector2.DistanceSquared(current.Position, target.Position) <= range * range;

    private void StopRoute(Guid instanceId, EntityUid officer)
    {
        _routes.Remove(instanceId);
        StopSteering(officer);
    }

    private void StopSteering(EntityUid officer)
    {
        if (_steeringActors.Remove(officer) && Exists(officer))
        {
            _steering.Unregister(officer);
            _steeringLease.Release(officer, "ncpd-evidence");
        }
        if (!Exists(officer))
        {
            _suspendedHtn.Remove(officer);
            return;
        }
        _physical.SetInterrupt(officer, NCCityPhysicalInterruptFlags.Police, false);
        if (_suspendedHtn.Remove(officer) && TryComp<HTNComponent>(officer, out var htn))
            _htn.SetHTNEnabled((officer, htn), true, htn.PlanCooldown);
    }

    private bool TryValidateBasis(
        EntityUid officer,
        EntityUid suspect,
        out NCNcpdSuspectClaimComponent claim,
        out NCPartyRef suspectParty,
        out string? failure)
    {
        suspectParty = default;
        failure = null;
        if (!_cuffable.IsCuffed(suspect))
        {
            claim = default!;
            failure = "ncpd-search-requires-restraints";
            return false;
        }
        if (!TryComp<NCNcpdSuspectClaimComponent>(officer, out claim!) || claim.Suspect != suspect ||
            !claim.IncidentId.IsValid)
        {
            failure = "ncpd-search-police-basis-unavailable";
            return false;
        }
        if (!_parties.TryResolve(suspect, out suspectParty))
        {
            failure = "ncpd-search-suspect-party-unavailable";
            return false;
        }
        return true;
    }

    private bool TryGetPolicyAndAuthority(
        EntityUid officer,
        out NCNcpdDetentionPolicyPrototype policy)
    {
        policy = default!;
        return TryComp<NCNcpdOfficerComponent>(officer, out var component) &&
               _prototypes.TryIndex(component.RoleProfile, out NCNcpdRoleProfilePrototype? role) && role != null &&
               role.CanDetain && _prototypes.TryIndex(DefaultPolicy, out policy!);
    }

    private bool IsDangerous(EntityUid item, NCNcpdDetentionPolicyPrototype policy) =>
        policy.ConfiscateDangerousWeapons &&
        (policy.SeizeGuns && HasComp<GunComponent>(item) ||
         policy.SeizeMeleeWeapons && HasComp<MeleeWeaponComponent>(item) ||
         policy.SeizeContraband && HasComp<ContrabandComponent>(item));

    private bool TryFindEquippedSlot(EntityUid suspect, EntityUid item, out string slot)
    {
        var slots = _inventory.GetSlotEnumerator(suspect);
        while (slots.NextItem(out var equipped, out var definition))
        {
            if (equipped != item)
                continue;
            slot = definition.Name;
            return true;
        }
        slot = string.Empty;
        return false;
    }

    private async Task<EvidenceMutationResult> PersistEvidenceAsync(
        NCCityIncidentId incidentId,
        Guid evidenceId,
        NCPartyRef officer,
        NCIncidentEvidenceKind kind,
        string reference,
        int reliability,
        bool admissible)
    {
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var incident = await _incidents.GetAsync(incidentId, true);
                if (incident == null)
                    return new(false, "ncpd-evidence-incident-not-found");
                if (incident.Snapshot.Evidence.Any(value => value.EvidenceId == evidenceId))
                    return new(true, null);

                var evidence = new NCIncidentEvidence(evidenceId, kind, officer, reference, reliability, admissible,
                    default);
                var result = await _incidents.AddEvidenceAsync(new NCAddIncidentEvidenceRequest(
                    NCPersistentIdentity.Command("ncpd", "seize-evidence", evidenceId.ToString("D")),
                    incidentId,
                    incident.AggregateVersion,
                    evidence,
                    officer,
                    NCPersistentIdentity.Correlation("ncpd", "seize-evidence", incidentId.ToString()),
                    incident.Snapshot.LastAppliedEventId,
                    "ncpd-physical-evidence"));
                if (result.Status is NCIncidentMutationStatus.Applied or NCIncidentMutationStatus.Duplicate)
                    return new(true, null);
                if (result.Status != NCIncidentMutationStatus.VersionConflict)
                    return new(false, result.Error ?? "ncpd-evidence-persistence-rejected");
            }
            return new(false, "ncpd-evidence-version-conflict");
        }
        catch (Exception exception)
        {
            return new(false, $"ncpd-evidence-exception:{exception.GetType().Name}");
        }
    }

    private static void FailSearch(
        NCNcpdEvidenceSearchComponent search,
        NCActiveWorldTime now,
        string failure,
        NCHumanActionExecutionEvent args)
    {
        search.Status = NCNcpdEvidenceSearchStatus.Failed;
        search.UpdatedAt = now;
        search.Failure = failure;
        args.Failure = failure;
    }

    private void FailSubmission(
        EntityUid officer,
        NCCityIncidentId incidentId,
        NCActiveWorldTime now,
        string failure,
        NCHumanActionExecutionEvent args)
    {
        StopRoute(args.InstanceId, officer);
        var submission = EnsureComp<NCNcpdEvidenceSubmissionComponent>(officer);
        submission.IncidentId = incidentId;
        submission.Status = NCNcpdEvidenceSubmissionStatus.Failed;
        submission.UpdatedAt = now;
        submission.Failure = failure;
        args.Failure = failure;
    }

    private void OnInterrupted(NCHumanActionInterruptedEvent args)
    {
        if (!SupportedActions.Contains(args.ActionId))
            return;
        _removals.Remove(args.InstanceId);
        _pending.Remove(args.InstanceId);
        if (args.EntityUid is { } officer)
            StopRoute(args.InstanceId, officer);
        else
            _routes.Remove(args.InstanceId);
    }

    private void OnOfficerShutdown(Entity<NCNcpdOfficerComponent> officer, ref ComponentShutdown args)
    {
        foreach (var id in _pending.Where(value => value.Value.Officer == officer.Owner)
                     .Select(value => value.Key).ToArray())
            _pending.Remove(id);
        foreach (var id in _removals.Where(value => value.Value.Officer == officer.Owner)
                     .Select(value => value.Key).ToArray())
            _removals.Remove(id);
        foreach (var id in _routes.Where(value => value.Value.Officer == officer.Owner)
                     .Select(value => value.Key).ToArray())
            _routes.Remove(id);
        StopSteering(officer.Owner);
    }

    private void Clear()
    {
        foreach (var officer in _steeringActors.ToArray())
            StopSteering(officer);
        _removals.Clear();
        _pending.Clear();
        _routes.Clear();
        _steeringActors.Clear();
        _suspendedHtn.Clear();
        _receiverCandidates.Clear();
        _affordanceCandidates.Clear();
    }

    private static uint StableSeed(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        var hash = 2166136261u;
        foreach (var value in bytes)
            hash = (hash ^ value) * 16777619u;
        return hash;
    }

    private readonly record struct RemovalAttempt(
        EntityUid Officer,
        EntityUid Item,
        string Slot,
        NCActiveWorldTime RetryAt);
    private readonly record struct PendingEvidenceMutation(
        EntityUid Officer,
        EntityUid Item,
        Task<EvidenceMutationResult> Operation);
    private readonly record struct EvidenceRoute(
        EntityUid Officer,
        EntityUid Receiver,
        NCMapCoordinates Target,
        int Replans,
        NCActiveWorldTime RetryAt);
    private readonly record struct EvidenceMutationResult(bool Success, string? Error);
}
