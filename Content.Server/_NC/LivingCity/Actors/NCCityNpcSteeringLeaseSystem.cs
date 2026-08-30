// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors.Components;
using Content.Shared.NPC;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Reference-counts physical steering ownership. A pre-existing ActiveNPC is preserved, while an activation created
/// for Living City is removed only after its final route, police or other physical controller releases it.
/// </summary>
public sealed partial class NCCityNpcSteeringLeaseSystem : EntitySystem
{
    public bool HasOtherOwner(EntityUid entityUid, string owner) =>
        TryComp<NCCityNpcSteeringLeaseComponent>(entityUid, out var lease) &&
        lease.Owners.Any(value => !string.Equals(value, owner, StringComparison.Ordinal));

    public bool Acquire(EntityUid entityUid, string owner)
    {
        if (!Exists(entityUid) || string.IsNullOrWhiteSpace(owner))
            return false;
        var lease = EnsureComp<NCCityNpcSteeringLeaseComponent>(entityUid);
        if (!lease.Owners.Add(owner))
            return true;
        if (lease.Owners.Count == 1 && !HasComp<ActiveNPCComponent>(entityUid))
        {
            EnsureComp<ActiveNPCComponent>(entityUid);
            lease.AddedActiveNpc = true;
        }
        return true;
    }

    public void Release(EntityUid entityUid, string owner)
    {
        if (!Exists(entityUid) || !TryComp<NCCityNpcSteeringLeaseComponent>(entityUid, out var lease) ||
            !lease.Owners.Remove(owner) || lease.Owners.Count > 0)
        {
            return;
        }
        if (lease.AddedActiveNpc && HasComp<ActiveNPCComponent>(entityUid))
            RemComp<ActiveNPCComponent>(entityUid);
        RemCompDeferred<NCCityNpcSteeringLeaseComponent>(entityUid);
    }
}
