// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Administration;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Groups;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Migration;
using Content.Server._NC.LivingCity.Population;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Persistence.Outbox;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Actors.Commands;

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCLivingCityStatusCommand : LocalizedEntityCommands
{
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _registry = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityActorMaterializationSystem _materialization = default!;
    [Dependency] private NCCityActorDematerializationSystem _dematerialization = default!;
    [Dependency] private NCCityRelevanceSystem _relevance = default!;
    [Dependency] private NCCityActorScheduleSchedulerSystem _scheduler = default!;
    [Dependency] private NCCityAbstractTravelSystem _travel = default!;
    [Dependency] private NCCityPopulationImportSystem _population = default!;
    [Dependency] private NCCityZoneStateProjectionSystem _zoneStates = default!;
    [Dependency] private NCCityGroupRegistrySystem _groups = default!;
    [Dependency] private NCCityGroupPhysicalizationSystem _groupPhysicalization = default!;
    [Dependency] private NCCityGroupRuntimeFormationSystem _groupFormations = default!;
    [Dependency] private NCPartyDirectorySystem _parties = default!;
    [Dependency] private NCEconomyLedgerSystem _economy = default!;
    [Dependency] private NCEmploymentRegistrySystem _employment = default!;
    [Dependency] private NCLegacyBankJobsMigrationSystem _legacyBankJobs = default!;
    [Dependency] private NCPersistentOutboxDispatcherSystem _outbox = default!;

    public override string Command => "alife_status";
    public override string Description => "Show persistent Living City actor and scheduler status.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError($"Usage: {Command}");
            return;
        }

        var clockText = _clock.TryGetCurrentTime(out var epoch, out var now)
            ? $"active epoch={epoch}, time={now.Elapsed}"
            : "inactive";
        shell.WriteLine($"A-Life clock: {clockText}.");
        shell.WriteLine(
            $"Actors: cached={_registry.CachedActorCount}, materialized={_runtime.MaterializedActorCount}.");
        shell.WriteLine(
            $"Materialization: active={_materialization.IsActive}, queued={_materialization.PendingRequestCount}, " +
            $"inFlight={_materialization.InFlightCount}, retained={_materialization.RetainedResultCount}.");
        shell.WriteLine(
            $"Dematerialization: active={_dematerialization.IsActive}, " +
            $"queued={_dematerialization.PendingRequestCount}, inFlight={_dematerialization.InFlightCount}, " +
            $"retained={_dematerialization.RetainedResultCount}.");
        shell.WriteLine(
            $"Relevance: active={_relevance.IsActive}, catalogReady={_relevance.CatalogRecovered}, " +
            $"catalogActors={_relevance.CatalogActorCount}, clusters={_relevance.ClusterCount}.");
        shell.WriteLine(
            $"Groups: cached={_groups.CachedGroupCount}, cachedMemberships={_groups.CachedMembershipCount}.");
        shell.WriteLine(
            $"Parties/economy/employment: directory={_parties.CachedPartyCount}, " +
            $"accounts={_economy.CachedAccountCount}, transactions={_economy.CachedTransactionCount}, " +
            $"employments={_employment.CachedEmploymentCount}, employeeIndexes={_employment.CachedEmployeeIndexCount}.");
        var legacyMigration = _legacyBankJobs.Progress;
        shell.WriteLine(
            $"Legacy Bank/Jobs rehearsal: running={legacyMigration.Running}, mode={legacyMigration.Mode}, " +
            $"phase={legacyMigration.Phase}, processed={legacyMigration.Processed}, " +
            $"conflicts={legacyMigration.Conflicts}, failed={legacyMigration.Failed}.");
        shell.WriteLine(
            $"Group physicalization: active={_groupPhysicalization.IsActive}, " +
            $"catalogReady={_groupPhysicalization.CatalogRecovered}, " +
            $"catalogGroups={_groupPhysicalization.CatalogGroupCount}, " +
            $"physicalGroups={_groupFormations.MaterializedGroupCount}, " +
            $"queued={_groupPhysicalization.PendingRequestCount}, " +
            $"inFlight={_groupPhysicalization.InFlightCount}, " +
            $"retained={_groupPhysicalization.RetainedResultCount}.");
        shell.WriteLine(
            $"Schedule: loaded={_scheduler.InitialLoadComplete}, queued={_scheduler.QueuedActorCount}, " +
            $"inFlight={_scheduler.InFlightCount}, pendingRetries={_scheduler.PendingMutationCount}.");
        shell.WriteLine(
            $"Abstract travel: loaded={_travel.InitialLoadComplete}, queued={_travel.QueuedActorCount}, " +
            $"inFlight={_travel.InFlightCount}, pendingRetries={_travel.PendingMutationCount}.");
        var population = _population.Progress;
        shell.WriteLine(
            $"Population import: running={population.Running}, profile={population.ProfileId ?? "none"}, " +
            $"processed={population.Processed}/{population.Requested}.");
        shell.WriteLine(
            $"Zone states: loaded={_zoneStates.InitialLoadComplete}, indexedActors={_zoneStates.IndexedActorCount}, " +
            $"scopes={_zoneStates.ScopeCount}, pending={_zoneStates.PendingScopeCount}, " +
            $"inFlight={_zoneStates.InFlightWriteCount}.");
        shell.WriteLine(
            $"Outbox: active={_outbox.IsActive}, fetch={_outbox.FetchInFlight}, " +
            $"inFlight={_outbox.DeliveriesInFlight}, handlers={_outbox.RegisteredHandlerCount}, " +
            $"delivered={_outbox.DeliveredThisSession}, failed={_outbox.FailedThisSession}, " +
            $"exhausted={_outbox.ExhaustedThisSession}.");
        if (_outbox.LastFetchError != null)
            shell.WriteLine($"Outbox last fetch error: {_outbox.LastFetchError}");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityActorDematerializeCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityActorDematerializationSystem _dematerialization = default!;

    public override string Command => "alife_actor_dematerialize";
    public override string Description => "Queue an atomic physical-to-strategic hand-off for one actor.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !Guid.TryParse(args[0], out var rawActorId) || rawActorId == Guid.Empty)
        {
            shell.WriteError($"Usage: {Command} <actorId>");
            return;
        }

        var actorId = new NCActorId(rawActorId);
        if (!_runtime.TryGetEntity(actorId, out var entityUid))
        {
            shell.WriteError($"Actor {actorId} is not materialized.");
            return;
        }

        var state = _dematerialization.Request(new NCCityDematerializationRequest(
            Guid.NewGuid(),
            actorId,
            entityUid,
            NCCityDematerializationReason.Admin,
            900,
            shell.Player is { } player ? $"admin:{player.UserId}" : "admin:server-console"));
        var message = $"{state.Status}: request={state.RequestId}, actor={state.ActorId}, " +
                      $"entity={state.EntityUid}, attempts={state.Attempts}, error={state.Error ?? "none"}.";
        if (state.Status == NCCityDematerializationStatus.Rejected)
            shell.WriteError(message);
        else
            shell.WriteLine(message);
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityDematerializationInfoCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityActorDematerializationSystem _dematerialization = default!;

    public override string Command => "alife_dematerialization_info";
    public override string Description => "Inspect one retained active-round dematerialization request.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !Guid.TryParse(args[0], out var requestId) || requestId == Guid.Empty)
        {
            shell.WriteError($"Usage: {Command} <requestId>");
            return;
        }

        if (!_dematerialization.TryGetRequest(requestId, out var state))
        {
            shell.WriteError($"Dematerialization request {requestId:D} is not retained in the current round.");
            return;
        }

        shell.WriteLine(
            $"{state.Status}: request={state.RequestId}, actor={state.ActorId}, entity={state.EntityUid}, " +
            $"reason={state.Reason}, attempts={state.Attempts}.");
        shell.WriteLine(
            $"Window: requested={state.RequestedAt.Elapsed}, expires={state.ExpiresAt.Elapsed}, " +
            $"error={state.Error ?? "none"}.");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityRelevanceInfoCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityRelevanceSystem _relevance = default!;

    public override string Command => "alife_relevance_info";
    public override string Description => "Show current player/camera clusters or one actor's derived LOD.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length > 1)
        {
            shell.WriteError($"Usage: {Command} [actorId]");
            return;
        }

        if (args.Length == 1)
        {
            if (!Guid.TryParse(args[0], out var rawActorId) ||
                !_relevance.TryGetLod(new NCActorId(rawActorId), out var lod))
            {
                shell.WriteError("The actor is absent from the current relevance catalog.");
                return;
            }
            shell.WriteLine($"Actor {rawActorId:D}: LOD={lod}.");
            return;
        }

        shell.WriteLine(
            $"active={_relevance.IsActive}, catalogReady={_relevance.CatalogRecovered}, " +
            $"actors={_relevance.CatalogActorCount}, clusters={_relevance.ClusterCount}.");
        foreach (var cluster in _relevance.Clusters)
        {
            shell.WriteLine(
                $"  {cluster.ClusterId:D}: map={cluster.MapId}, players={cluster.AttachedPlayerCount}, " +
                $"anchors={cluster.AnchorEntities.Count}, zones={cluster.ActiveZoneIds.Count}, " +
                $"materialized={cluster.MaterializedActorCount}/{cluster.SoftMaterializedBudget}.");
        }
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityActorMaterializeCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityActorMaterializationSystem _materialization = default!;

    public override string Command => "alife_actor_materialize";
    public override string Description => "Queue safe materialization of one persistent actor in its current NCZoneId.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 1 or > 2 ||
            !Guid.TryParse(args[0], out var rawActorId) || rawActorId == Guid.Empty ||
            (args.Length == 2 && !int.TryParse(args[1], out _)))
        {
            shell.WriteError($"Usage: {Command} <actorId> [priority]");
            return;
        }

        var priority = args.Length == 2 ? int.Parse(args[1]) : 500;
        var state = _materialization.Request(new NCCityMaterializationRequest(
            Guid.NewGuid(),
            new NCActorId(rawActorId),
            NCCityMaterializationReason.Admin,
            priority,
            unchecked((uint) rawActorId.GetHashCode()),
            null,
            shell.Player is { } player ? $"admin:{player.UserId}" : "admin:server-console"));
        var message =
            $"{state.Status}: request={state.RequestId}, actor={state.ActorId}, " +
            $"attempts={state.Attempts}, entity={state.EntityUid}, error={state.Error ?? "none"}.";
        if (state.Status == NCCityMaterializationRequestStatus.Rejected)
            shell.WriteError(message);
        else
            shell.WriteLine(message);
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityMaterializationInfoCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityActorMaterializationSystem _materialization = default!;

    public override string Command => "alife_materialization_info";
    public override string Description => "Inspect one retained active-round materialization request.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !Guid.TryParse(args[0], out var requestId) || requestId == Guid.Empty)
        {
            shell.WriteError($"Usage: {Command} <requestId>");
            return;
        }

        if (!_materialization.TryGetRequest(requestId, out var state))
        {
            shell.WriteError($"Materialization request {requestId:D} is not retained in the current round.");
            return;
        }

        shell.WriteLine(
            $"{state.Status}: request={state.RequestId}, actor={state.ActorId}, reason={state.Reason}, " +
            $"priority={state.Priority}, attempts={state.Attempts}, entity={state.EntityUid}.");
        shell.WriteLine(
            $"Window: requested={state.RequestedAt.Elapsed}, expires={state.ExpiresAt.Elapsed}, " +
            $"error={state.Error ?? "none"}.");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityActorInfoCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityActorRegistrySystem _registry = default!;
    [Dependency] private NCCityActorDomainRegistrySystem _domains = default!;

    public override string Command => "alife_actor_info";
    public override string Description => "Inspect one persistent actor without materializing an ECS entity.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!TryParseActor(args, out var actorId))
        {
            shell.WriteError($"Usage: {Command} <actorId>");
            return;
        }

        var actor = await _registry.GetAsync(actorId, true);
        if (actor == null)
        {
            shell.WriteError($"Persistent actor {actorId} was not found or has an unreadable snapshot.");
            return;
        }

        var snapshot = actor.Snapshot;
        shell.WriteLine(
            $"{snapshot.Identity.FullName} ({actorId}), archetype={snapshot.Identity.ArchetypeId}, " +
            $"version={actor.AggregateVersion}/{snapshot.Identity.DataVersion}.");
        shell.WriteLine(
            $"Lifecycle: {snapshot.Lifecycle.Status}, frozen={snapshot.Lifecycle.Frozen}, " +
            $"diedAt={snapshot.Lifecycle.DiedAt?.Elapsed.ToString() ?? "none"}.");
        shell.WriteLine(
            $"Location: current={snapshot.Location.CurrentZone}, destination={snapshot.Location.DestinationZone}, " +
            $"phase={snapshot.Location.TravelPhase}, mode={snapshot.Location.TravelMode}.");
        shell.WriteLine(
            $"Schedule: {snapshot.Schedule.CurrentActivity}/{snapshot.Schedule.CurrentGoal}, " +
            $"availability={snapshot.Schedule.Availability}, next={snapshot.Schedule.NextUpdateAt.Elapsed}, " +
            $"commitments={snapshot.Schedule.Commitments.Length}.");
        foreach (var commitment in snapshot.Schedule.Commitments)
        {
            shell.WriteLine(
                $"  {commitment.CommitmentId}: {commitment.Activity} -> {commitment.TargetZone}, " +
                $"{commitment.StartsAt.Elapsed}..{commitment.EndsAt.Elapsed}, priority={commitment.Priority}.");
        }

        var healthTask = _domains.GetHealthAsync(actorId, true);
        var inventoryTask = _domains.GetInventoryAsync(actorId, true);
        await Task.WhenAll(healthTask, inventoryTask);
        var health = await healthTask;
        var inventory = await inventoryTask;
        shell.WriteLine(health == null
            ? "Health: missing or unreadable."
            : $"Health: version={health.AggregateVersion}, condition={health.Snapshot.Summary.OverallCondition}, " +
              $"bleeding={health.Snapshot.Summary.Bleeding}, mobility={health.Snapshot.Summary.Mobility}, " +
              $"consciousness={health.Snapshot.Summary.Consciousness}, " +
              $"medicalPayload={(health.Snapshot.MedicalStatePayload == null ? "not captured" : "present")}.");
        shell.WriteLine(inventory == null
            ? "Inventory: missing or unreadable."
            : $"Inventory: version={inventory.AggregateVersion}, source={inventory.Snapshot.SourceAuthorityVersion}, " +
              $"unique={inventory.Snapshot.Summary.UniqueItems.Length}, " +
              $"weapons={inventory.Snapshot.Summary.Weapons.Length}, " +
              $"documents={inventory.Snapshot.Summary.Documents.Length}, " +
              $"credentials={inventory.Snapshot.Summary.Credentials.Length}, " +
              $"commodities={inventory.Snapshot.Summary.Commodities.Length}.");
    }

    private static bool TryParseActor(string[] args, out NCActorId actorId)
    {
        actorId = default;
        if (args.Length != 1 || !Guid.TryParse(args[0], out var raw))
            return false;
        actorId = new NCActorId(raw);
        return actorId.IsValid;
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityActorCaptureDomainsCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityActorDomainRegistrySystem _domains = default!;

    public override string Command => "alife_actor_capture_domains";
    public override string Description => "Atomically capture health and stable inventory of a materialized actor.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!TryParseActor(args, out var actorId))
        {
            shell.WriteError($"Usage: {Command} <actorId>");
            return;
        }

        var health = await _domains.GetHealthAsync(actorId, true);
        var inventory = await _domains.GetInventoryAsync(actorId, true);
        if (health == null || inventory == null)
        {
            shell.WriteError($"Actor {actorId} has missing or unreadable domain aggregates.");
            return;
        }

        var result = await _domains.CaptureMaterializedAsync(new NCCityActorDomainCaptureRequest(
            Guid.NewGuid(),
            actorId,
            health.AggregateVersion,
            inventory.AggregateVersion,
            Guid.NewGuid(),
            AuditSource: AuditSource(shell)));
        if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
        {
            shell.WriteLine(
                $"{result.Status}: actor={actorId}, health={result.Health?.AggregateVersion}, " +
                $"inventory={result.Inventory?.AggregateVersion}.");
        }
        else
        {
            shell.WriteError($"{result.Status}: {result.Error}");
        }
    }

    private static bool TryParseActor(string[] args, out NCActorId actorId)
    {
        actorId = default;
        if (args.Length != 1 || !Guid.TryParse(args[0], out var raw))
            return false;
        actorId = new NCActorId(raw);
        return actorId.IsValid;
    }

    private static string AuditSource(IConsoleShell shell) =>
        shell.Player is { } player ? $"admin:{player.UserId}" : "admin:server-console";
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityActorRestoreHealthCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityActorDomainRegistrySystem _domains = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;

    public override string Command => "alife_actor_restore_health";
    public override string Description => "Restore the stored full NC medical graph to a materialized actor.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!TryParseActor(args, out var actorId))
        {
            shell.WriteError($"Usage: {Command} <actorId>");
            return;
        }

        var health = await _domains.GetHealthAsync(actorId, true);
        if (health == null || !_runtime.TryGetEntity(actorId, out var actorEntity) ||
            !_domains.TryRestoreHealth(actorId, actorEntity, health))
        {
            shell.WriteError(
                $"Actor {actorId} is not materialized or has no compatible full medical snapshot.");
            return;
        }

        shell.WriteLine($"Restored health for actor {actorId} from aggregate version {health.AggregateVersion}.");
    }

    private static bool TryParseActor(string[] args, out NCActorId actorId)
    {
        actorId = default;
        if (args.Length != 1 || !Guid.TryParse(args[0], out var raw))
            return false;
        actorId = new NCActorId(raw);
        return actorId.IsValid;
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityActorCreateCommand : LocalizedEntityCommands
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCCityActorRegistrySystem _registry = default!;

    public override string Command => "alife_actor_create";
    public override string Description => "Create one deterministic persistent Living City actor.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 6 ||
            !Guid.TryParse(args[0], out var populationScope) || populationScope == Guid.Empty ||
            !_prototypes.HasIndex(new ProtoId<NCCityActorArchetypePrototype>(args[2])) ||
            !NCZoneId.TryParse(args[3], out var homeZone) ||
            !TryParseOptionalZone(args[4], out var workZone))
        {
            shell.WriteError(
                $"Usage: {Command} <populationScopeGuid> <stableLocalId> <archetype> " +
                "<homeZoneId> <workZoneId|none> <full name>");
            return;
        }

        var result = await _registry.CreateAsync(new NCCityActorCreateRequest(
            Guid.NewGuid(),
            populationScope,
            args[1],
            string.Join(' ', args.Skip(5)),
            [],
            new ProtoId<NCCityActorArchetypePrototype>(args[2]),
            homeZone,
            workZone,
            Guid.NewGuid(),
            AuditSource: AuditSource(shell)));
        WriteMutation(shell, result);
    }

    private static bool TryParseOptionalZone(string value, out NCZoneId zoneId)
    {
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            zoneId = default;
            return true;
        }

        return NCZoneId.TryParse(value, out zoneId);
    }

    private static string AuditSource(IConsoleShell shell) =>
        shell.Player is { } player ? $"admin:{player.UserId}" : "admin:server-console";

    private static void WriteMutation(IConsoleShell shell, NCCityActorMutationResult result)
    {
        if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
            shell.WriteLine($"{result.Status}: actor={result.ActorId}, version={result.AggregateVersion}.");
        else
            shell.WriteError($"{result.Status}: {result.Error}");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityActorFreezeCommand : LocalizedEntityCommands
{
    [Dependency] private NCCityActorRegistrySystem _registry = default!;

    public override string Command => "alife_actor_freeze";
    public override string Description => "Freeze or unfreeze one persistent actor with an audited mutation.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2 || !Guid.TryParse(args[0], out var rawActorId) ||
            !bool.TryParse(args[1], out var frozen))
        {
            shell.WriteError($"Usage: {Command} <actorId> <true|false>");
            return;
        }

        var actorId = new NCActorId(rawActorId);
        var actor = await _registry.GetAsync(actorId, true);
        if (actor == null)
        {
            shell.WriteError($"Persistent actor {actorId} was not found.");
            return;
        }

        WriteMutation(shell, await _registry.SetFrozenAsync(new NCCityActorFreezeMutationRequest(
            Guid.NewGuid(),
            actorId,
            actor.AggregateVersion,
            frozen,
            Guid.NewGuid(),
            AuditSource: AuditSource(shell))));
    }

    private static string AuditSource(IConsoleShell shell) =>
        shell.Player is { } player ? $"admin:{player.UserId}" : "admin:server-console";

    private static void WriteMutation(IConsoleShell shell, NCCityActorMutationResult result)
    {
        if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
            shell.WriteLine($"{result.Status}: actor={result.ActorId}, version={result.AggregateVersion}.");
        else
            shell.WriteError($"{result.Status}: {result.Error}");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCCityActorMoveCommand : LocalizedEntityCommands
{
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCityActorRegistrySystem _registry = default!;

    public override string Command => "alife_actor_move";
    public override string Description => "Move one persistent actor to an NCZoneId with an audited mutation.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2 || !Guid.TryParse(args[0], out var rawActorId) ||
            !NCZoneId.TryParse(args[1], out var zoneId) || !_zones.TryGetZone(zoneId, out _))
        {
            shell.WriteError($"Usage: {Command} <actorId> <zoneId>");
            return;
        }

        var actorId = new NCActorId(rawActorId);
        var actor = await _registry.GetAsync(actorId, true);
        if (actor == null || !_clock.TryGetCurrentTime(out _, out var now))
        {
            shell.WriteError(actor == null
                ? $"Persistent actor {actorId} was not found."
                : "The active-world clock is not ready.");
            return;
        }

        var location = actor.Snapshot.Location;
        WriteMutation(shell, await _registry.SetLocationAsync(new NCCityActorLocationMutationRequest(
            Guid.NewGuid(),
            actorId,
            actor.AggregateVersion,
            zoneId,
            default,
            NCCityActorTravelPhase.Stationary,
            location.TravelMode,
            null,
            "admin-move",
            string.Empty,
            actor.Snapshot.Schedule.Availability,
            now,
            Guid.NewGuid(),
            AuditSource: AuditSource(shell))));
    }

    private static string AuditSource(IConsoleShell shell) =>
        shell.Player is { } player ? $"admin:{player.UserId}" : "admin:server-console";

    private static void WriteMutation(IConsoleShell shell, NCCityActorMutationResult result)
    {
        if (result.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate)
            shell.WriteLine($"{result.Status}: actor={result.ActorId}, version={result.AggregateVersion}.");
        else
            shell.WriteError($"{result.Status}: {result.Error}");
    }
}
