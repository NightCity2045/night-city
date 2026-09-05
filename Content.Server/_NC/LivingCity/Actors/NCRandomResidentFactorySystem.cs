// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Preferences;
using Robust.Shared.Map;
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
/// for physical spawning near the administrator on the current map.
/// </summary>
public sealed partial class NCRandomResidentFactorySystem : EntitySystem
{
    private static readonly ProtoId<NCRandomResidentGenerationProfilePrototype> DefaultProfile =
        new("NCDefaultRandomResidentGeneration");

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCPhysicalActorLifecycleSystem _physicalActors = default!;

    public async Task<NCRandomResidentCreationResult> CreateAtAsync(EntityUid source, string auditSource)
    {
        if (!Exists(source))
            return Rejected("administrator-has-no-attached-entity");
        var target = _transform.GetMapCoordinates(source);
        if (target.MapId == MapId.Nullspace || !float.IsFinite(target.Position.X) ||
            !float.IsFinite(target.Position.Y))
        {
            return Rejected("administrator-has-no-world-position");
        }

        // City geography enriches persistence when present. Ordinary SS14 maps intentionally remain valid:
        // their runtime MapId is used only for this spawn and is never written to durable actor state.
        var currentZone = default(NCZoneId);
        NCCityActorPhysicalRecoveryState? recovery = null;
        if (_coordinates.TryConvert(target, out var stable))
        {
            var location = _zones.GetLocationContext(stable).MostSpecific;
            if (location.IsValid)
            {
                currentZone = location;
                recovery = new NCCityActorPhysicalRecoveryState(
                    stable.NetworkId,
                    stable.Position.X,
                    stable.Position.Y,
                    stable.Z,
                    currentZone);
            }
        }

        var created = await CreatePersistentAsync(DefaultProfile, null, auditSource, currentZone, recovery);
        if (!created.Success)
            return created;

        var spawn = await _physicalActors.RequestAdminAsync(
            Guid.NewGuid(),
            created.ActorId,
            target,
            "f7-random-resident");
        return created with
        {
            Success = spawn.Status != NCPhysicalActorSpawnStatus.Rejected,
            Spawn = spawn,
            Error = spawn.Error,
        };
    }

    /// <summary>
    /// Creates the same durable random person used by the F7 factory without choosing a map spawn. Test rules and
    /// future population sources use this API so appearance and persona generation have one authority.
    /// </summary>
    public Task<NCRandomResidentCreationResult> CreatePersistentAsync(
        ProtoId<NCRandomResidentGenerationProfilePrototype> profile,
        NCActorId? actorId,
        string auditSource) =>
        CreatePersistentAsync(profile, actorId, auditSource, default, null);

    private async Task<NCRandomResidentCreationResult> CreatePersistentAsync(
        ProtoId<NCRandomResidentGenerationProfilePrototype> profile,
        NCActorId? requestedActorId,
        string auditSource,
        NCZoneId currentZone,
        NCCityActorPhysicalRecoveryState? recovery)
    {
        if (!_prototypes.TryIndex(profile, out NCRandomResidentGenerationProfilePrototype? generation) ||
            generation == null || !Valid(generation))
        {
            return Rejected("random-resident-generation-profile-is-invalid");
        }

        var persona = _random.Pick(generation.Personas);
        var humanoid = HumanoidCharacterProfile.RandomWithSpecies(generation.Species.Id);
        var payload = NCHumanoidProfilePayload.Serialize(_serialization, humanoid);
        var stableActorId = requestedActorId is { IsValid: true } selected ? selected : NCActorId.New();
        var details = new NCGeneratedPersonaDetails(
            _random.Pick(generation.BackgroundKeys),
            _random.Pick(generation.PressureKeys),
            _random.Pick(generation.FearKeys),
            _random.Pick(generation.GoalKeys),
            _random.Pick(generation.SocialHabitKeys),
            _random.Pick(generation.SpeechQuirkKeys));
        var commandId = Guid.NewGuid();
        var persisted = await _actors.CreateResidentAsync(new NCCityResidentCreateRequest(
            commandId,
            stableActorId,
            humanoid.Name,
            generation.Archetype,
            persona,
            payload,
            currentZone,
            recovery,
            commandId,
            AuditSource: auditSource,
            GeneratedPersona: details));
        var success = persisted.Status is NCCityActorMutationStatus.Applied or NCCityActorMutationStatus.Duplicate;
        return new NCRandomResidentCreationResult(
            success,
            stableActorId,
            humanoid.Name,
            persona.Id,
            persisted.Status,
            null,
            success ? null : persisted.Error ?? "persistent-resident-create-failed");
    }

    private bool Valid(NCRandomResidentGenerationProfilePrototype generation)
    {
        return generation.Personas.Length > 0 &&
               _prototypes.HasIndex(generation.Archetype) && _prototypes.HasIndex(generation.Species) &&
               generation.Personas.All(persona => _prototypes.HasIndex(persona)) &&
               ValidKeys(generation.BackgroundKeys) && ValidKeys(generation.PressureKeys) &&
               ValidKeys(generation.FearKeys) && ValidKeys(generation.GoalKeys) &&
               ValidKeys(generation.SocialHabitKeys) && ValidKeys(generation.SpeechQuirkKeys);
    }

    private static bool ValidKeys(string[] values) => values.Length > 0 && values.All(value =>
        value.Length is > 0 and <= 96 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));

    private static NCRandomResidentCreationResult Rejected(string error) =>
        new(false, default, string.Empty, string.Empty, NCCityActorMutationStatus.Rejected, null, error);
}
