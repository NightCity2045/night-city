// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Serialization.Manager;

namespace Content.Server._NC.LivingCity.Actors;

public sealed record NCRandomResidentCreationResult(
    bool Success,
    NCActorId ActorId,
    string FullName,
    string PersonaProfileId,
    NCCityActorMutationStatus PersistenceStatus,
    NCPhysicalActorSpawnState? Spawn,
    string? Error);

/// <summary>
/// Server-authoritative one-click resident factory. It creates persistent identity first, then queues that same Actor
/// for physical spawning near the administrator on the current Z-level.
/// </summary>
public sealed partial class NCRandomResidentFactorySystem : EntitySystem
{
    private static readonly ProtoId<NCRandomResidentGenerationProfilePrototype> DefaultProfile =
        new("NCDefaultRandomResidentGeneration");

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCPhysicalActorLifecycleSystem _physicalActors = default!;

    public async Task<NCRandomResidentCreationResult> CreateAtAsync(EntityUid source, string auditSource)
    {
        if (!Exists(source) || !_coordinates.TryGetCoordinates(source, out var target))
            return Rejected("administrator-has-no-stable-world-position");

        var location = _zones.GetLocationContext(target).MostSpecific;
        if (location is not { IsValid: true } currentZone)
            return Rejected("administrator-is-outside-authored-city-geography");

        if (!_prototypes.TryIndex(DefaultProfile, out NCRandomResidentGenerationProfilePrototype? generation) ||
            generation == null || generation.Personas.Length == 0 ||
            !_prototypes.HasIndex(generation.Archetype) || !_prototypes.HasIndex(generation.Species) ||
            generation.Personas.Any(persona => !_prototypes.HasIndex(persona)))
        {
            return Rejected("random-resident-generation-profile-is-invalid");
        }

        var persona = _random.Pick(generation.Personas);
        var humanoid = HumanoidCharacterProfile.RandomWithSpecies(generation.Species.Id);
        var payload = NCHumanoidProfilePayload.Serialize(_serialization, humanoid);
        var actorId = NCActorId.New();
        var commandId = Guid.NewGuid();
        var recovery = new NCCityActorPhysicalRecoveryState(
            target.NetworkId,
            target.Position.X,
            target.Position.Y,
            target.Z,
            currentZone);
        var persisted = await _actors.CreateResidentAsync(new NCCityResidentCreateRequest(
            commandId,
            actorId,
            humanoid.Name,
            generation.Archetype,
            persona,
            payload,
            currentZone,
            recovery,
            commandId,
            AuditSource: auditSource));
        if (persisted.Status is not (NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate))
        {
            return new NCRandomResidentCreationResult(
                false,
                actorId,
                humanoid.Name,
                persona.Id,
                persisted.Status,
                null,
                persisted.Error ?? "persistent-resident-create-failed");
        }

        var spawn = _physicalActors.RequestAdmin(
            Guid.NewGuid(),
            actorId,
            target,
            "f7-random-resident");
        return new NCRandomResidentCreationResult(
            spawn.Status != NCPhysicalActorSpawnStatus.Rejected,
            actorId,
            humanoid.Name,
            persona.Id,
            persisted.Status,
            spawn,
            spawn.Error);
    }

    private static NCRandomResidentCreationResult Rejected(string error) =>
        new(false, default, string.Empty, string.Empty, NCCityActorMutationStatus.Rejected, null, error);
}
