// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Actors.Domains;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Captures every bound physical NPC before the active-world epoch closes. A checkpoint is persistence only: it
/// never deletes, relocates, or virtualizes an entity.
/// </summary>
public sealed partial class NCPhysicalActorCheckpointSystem : EntitySystem
{
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCCityActorDomainRegistrySystem _domains = default!;

    private readonly List<(NCActorId ActorId, EntityUid EntityUid)> _bindings = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || epoch != args.Epoch)
            return;

        _runtime.GetBindings(_bindings);
        foreach (var (actorId, entityUid) in _bindings)
        {
            string? error = null;
            if (!_actors.TryGetCached(actorId, out var actor) ||
                !_domains.TryGetCached(actorId, out var health, out var inventory) ||
                !_domains.TryCaptureMaterialized(
                    actorId,
                    entityUid,
                    health.AggregateVersion,
                    inventory.AggregateVersion,
                    now,
                    out var capture,
                    out error))
            {
                args.AddPendingWrite(Task.FromException(new InvalidOperationException(
                    $"Physical actor checkpoint capture failed for {actorId}: {error ?? "required cached state is missing"}.")));
                continue;
            }

            var commandId = Guid.NewGuid();
            var currentZone = default(NCZoneId);
            NCCityActorPhysicalRecoveryState? recovery = null;
            if (_coordinates.TryGetCoordinates(entityUid, out var coordinates) &&
                _zones.GetLocationContext(coordinates).MostSpecific is { IsValid: true } stableZone)
            {
                currentZone = stableZone;
                recovery = new NCCityActorPhysicalRecoveryState(
                    coordinates.NetworkId,
                    coordinates.Position.X,
                    coordinates.Position.Y,
                    coordinates.Z,
                    currentZone);
            }

            // Ordinary maps have no durable CityGraph coordinate. Their actor body and domains are still saved, but
            // the next round deliberately chooses a normal SS14 spawn point instead of persisting a runtime MapId.
            args.AddPendingWrite(CommitAsync(new NCPhysicalActorCheckpointRequest(
                commandId,
                actorId,
                actor.AggregateVersion,
                currentZone,
                capture,
                commandId,
                null,
                "physical-actor-round-checkpoint",
                null,
                recovery)));
        }
    }

    private async Task CommitAsync(NCPhysicalActorCheckpointRequest request)
    {
        var result = await _actors.CommitPhysicalCheckpointAsync(request);
        if (result.Status is not (NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate))
            throw new InvalidOperationException(
                $"Physical actor checkpoint commit failed for {request.ActorId}: {result.Error ?? result.Status.ToString()}.");
    }
}
