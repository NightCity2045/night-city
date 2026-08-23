// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Content.Server.Clothing.Systems;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Police.NCPD;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Roles;
using Robust.Server.GameObjects;
using Robust.Shared.Asynchronous;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>
/// Creates the single command-only neural NCPD actor for the current round and projects it beside the requesting
/// administrator. It uses persistent actor/cognition/employment boundaries but never joins a population profile.
/// </summary>
public sealed partial class NCNeuralTestNpcSystem : EntitySystem
{
    private static readonly ProtoId<NCCityActorArchetypePrototype> TestArchetype =
        "NCLivingCityNeuralNcpdTestOfficerArchetype";
    private static readonly EntProtoId TestEntity = "NCLivingCityNeuralNcpdTestOfficer";
    private static readonly ProtoId<NCNcpdRoleProfilePrototype> OfficerRole = "NCNcpdOfficerRole";
    private static readonly ProtoId<StartingGearPrototype> OfficerGear = "NCPDOfficerGear";
    private const string StableLocalId = "neural-ncpd-api-test";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    [Dependency] private OutfitSystem _outfits = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCOrganizationWorkforceSystem _workforce = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;

    public void RequestSpawn(ICommonSession session, Action<NCNeuralTestNpcSpawnResult> completed)
    {
        if (session.AttachedEntity is not { } attached || !Exists(attached) ||
            !_clock.TryGetCurrentTime(out var epoch, out _) ||
            !_prototypes.TryIndex(OfficerRole, out NCNcpdRoleProfilePrototype? role) || role == null ||
            !_prototypes.HasIndex(TestArchetype) || !_prototypes.HasIndex(TestEntity) ||
            !_prototypes.HasIndex(OfficerGear))
        {
            completed(new(false, default, default,
                "The player, active A-Life round, or neural NCPD test prototypes are unavailable."));
            return;
        }

        _ = PrepareSpawnAsync(session, epoch, role, completed);
    }

    private async Task PrepareSpawnAsync(
        ICommonSession session,
        Content.Shared._NC.Persistence.Time.NCSessionEpoch epoch,
        NCNcpdRoleProfilePrototype role,
        Action<NCNeuralTestNpcSpawnResult> completed)
    {
        NCCityActorMutationResult actorResult;
        NCWorkforceAssignmentResult employmentResult;
        try
        {
            actorResult = await _actors.CreateAsync(new NCCityActorCreateRequest(
                StableGuid(epoch.Value, "create"),
                epoch.Value,
                StableLocalId,
                Loc.GetString("nc-neural-test-officer-name"),
                [],
                TestArchetype,
                role.WorkZone,
                role.WorkZone,
                StableGuid(epoch.Value, "correlation"),
                AuditSource: "console-neural-ncpd-test"));
            if (actorResult.Status is not (NCCityActorMutationStatus.Applied or
                NCCityActorMutationStatus.Duplicate))
            {
                CompleteOnMainThread(completed, new(false, default, actorResult.ActorId,
                    actorResult.Error ?? actorResult.Status.ToString()));
                return;
            }

            employmentResult = await _workforce.EnsureEmploymentAsync(
                actorResult.ActorId,
                role.Job,
                "console-neural-ncpd-test");
        }
        catch (Exception exception)
        {
            CompleteOnMainThread(completed, new(false, default, default, exception.Message));
            return;
        }

        if (employmentResult.Status is not (NCWorkforceAssignmentStatus.Applied or
            NCWorkforceAssignmentStatus.Existing))
        {
            CompleteOnMainThread(completed, new(false, default, actorResult.ActorId,
                employmentResult.Error ?? employmentResult.Status.ToString()));
            return;
        }

        _tasks.RunOnMainThread(() => FinishSpawn(session, epoch, actorResult.ActorId, completed));
    }

    private void FinishSpawn(
        ICommonSession session,
        Content.Shared._NC.Persistence.Time.NCSessionEpoch epoch,
        NCActorId actorId,
        Action<NCNeuralTestNpcSpawnResult> completed)
    {
        if (!_clock.TryGetCurrentTime(out var currentEpoch, out var now) || currentEpoch != epoch ||
            session.AttachedEntity is not { } attached || !Exists(attached))
        {
            completed(new(false, default, actorId, "The round or requesting player changed during actor creation."));
            return;
        }
        if (_runtime.TryGetEntity(actorId, out var existing) && Exists(existing))
        {
            completed(new(true, existing, actorId, string.Empty));
            return;
        }

        var entity = Spawn(TestEntity, Transform(attached).Coordinates.Offset(new Vector2(1f, 0f)));
        _metadata.SetEntityName(entity, Loc.GetString("nc-neural-test-officer-name"));
        if (!_outfits.SetOutfit(entity, OfficerGear.Id))
        {
            QueueDel(entity);
            completed(new(false, default, actorId, "NCPDOfficerGear could not be equipped."));
            return;
        }

        var inventory = EnsureComp<NCCityActorInventoryAdapterComponent>(entity);
        inventory.EquippedLoadoutId = OfficerGear.Id;
        if (!_runtime.TryBind(actorId, entity))
        {
            QueueDel(entity);
            completed(new(false, default, actorId, "The persistent actor binding is already occupied."));
            return;
        }

        // Admin ownership prevents relevance from deleting the command-only test actor while the API is inspected.
        var lease = EnsureComp<NCCityMaterializationLeaseComponent>(entity);
        lease.Reason = NCCityMaterializationReason.Admin;
        lease.MaterializedAt = now;
        lease.LastRelevantAt = now;
        var requestId = StableGuid(epoch.Value, "physical-projection");
        RaiseLocalEvent(new NCCityActorRehydratedEvent(
            requestId,
            actorId,
            entity,
            NCCityMaterializationReason.Admin));
        completed(new(true, entity, actorId, string.Empty));
    }

    private void CompleteOnMainThread(
        Action<NCNeuralTestNpcSpawnResult> completed,
        NCNeuralTestNpcSpawnResult result)
    {
        _tasks.RunOnMainThread(() => completed(result));
    }

    private static Guid StableGuid(Guid scope, string purpose)
    {
        var scopeBytes = scope.ToByteArray();
        var purposeBytes = Encoding.UTF8.GetBytes($"{StableLocalId}/{purpose}");
        var input = new byte[scopeBytes.Length + purposeBytes.Length];
        scopeBytes.CopyTo(input, 0);
        purposeBytes.CopyTo(input, scopeBytes.Length);
        var hash = SHA256.HashData(input);
        hash[6] = (byte) ((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte) ((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }
}

public sealed record NCNeuralTestNpcSpawnResult(
    bool Success,
    EntityUid EntityUid,
    NCActorId ActorId,
    string Error);
