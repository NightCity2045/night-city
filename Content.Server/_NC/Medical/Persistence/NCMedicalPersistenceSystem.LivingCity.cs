// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.Medical.Persistence;

public sealed partial class NCMedicalPersistenceSystem
{
    /// <summary>
    /// Captures a materialized persistent actor through the same complete medical serializer used by players.
    /// The actor aggregate owns the payload; the player-character medical table is never touched.
    /// </summary>
    public bool TryCapturePersistentActorState(
        EntityUid body,
        NCActorId actorId,
        long revision,
        out NCMedicalStateSnapshot snapshot,
        out string payload)
    {
        snapshot = default!;
        payload = string.Empty;
        if (!actorId.IsValid || revision < 0 || !HasComp<NCMedicalBodyComponent>(body) ||
            !CanBindPersistentActor(body, actorId))
        {
            return false;
        }

        BindOwner(body, NCMedicalStableOwnerKind.PersistentActor, actorId.Value.ToString("D"), revision);
        var captured = SerializeMedicalState(body);
        if (captured == null || !TryEncodeSnapshot(captured, out payload))
            return false;

        snapshot = captured;
        return true;
    }

    /// <summary>
    /// Restores an actor-owned opaque payload without advancing physiology for server downtime.
    /// </summary>
    public bool TryRestorePersistentActorState(
        EntityUid body,
        NCActorId actorId,
        long revision,
        int snapshotVersion,
        string payload)
    {
        if (!actorId.IsValid || revision < 1 || snapshotVersion < 1 || string.IsNullOrWhiteSpace(payload) ||
            !HasComp<NCMedicalBodyComponent>(body) || !CanBindPersistentActor(body, actorId))
        {
            return false;
        }

        var stored = new NCMedicalStoredSnapshotData(
            default,
            snapshotVersion,
            revision,
            payload,
            DateTime.UtcNow,
            DateTime.UtcNow);
        if (!TryDecodeStoredSnapshot(stored, out var decoded) ||
            !TryRebaseSnapshotForRuntime(decoded, _timing.CurTime, out var rebased))
        {
            return false;
        }

        BindOwner(body, NCMedicalStableOwnerKind.PersistentActor, actorId.Value.ToString("D"), revision);
        _suppressPersistenceEvents.Add(body);
        try
        {
            return RestoreMedicalState(body, rebased);
        }
        finally
        {
            _suppressPersistenceEvents.Remove(body);
        }
    }

    /// <summary>
    /// Advances only the runtime ownership marker after the actor health aggregate commits.
    /// </summary>
    public bool SetPersistentActorRevision(EntityUid body, NCActorId actorId, long revision)
    {
        if (!actorId.IsValid || revision < 1 ||
            !TryComp<NCMedicalPersistenceOwnerComponent>(body, out var owner) ||
            owner.Kind != NCMedicalStableOwnerKind.PersistentActor ||
            !Guid.TryParse(owner.Key, out var ownerId) || ownerId != actorId.Value)
        {
            return false;
        }

        owner.Revision = revision;
        return true;
    }

    private bool CanBindPersistentActor(EntityUid body, NCActorId actorId)
    {
        if (!TryComp<NCMedicalPersistenceOwnerComponent>(body, out var owner) ||
            owner.Kind == NCMedicalStableOwnerKind.Unassigned)
        {
            return true;
        }

        return owner.Kind == NCMedicalStableOwnerKind.PersistentActor &&
               Guid.TryParse(owner.Key, out var ownerId) && ownerId == actorId.Value;
    }
}
