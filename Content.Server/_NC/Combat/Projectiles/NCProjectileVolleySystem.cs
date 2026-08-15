// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Projectiles;

/// <summary>
/// Keeps one action roll equivalent for shotgun-style attacks while distributing their physical projectiles
/// across a primary anatomical zone and its data-defined neighbours. No projectile performs a second aim roll.
/// </summary>
public sealed partial class NCProjectileVolleySystem : EntitySystem
{
    private const int MaximumRememberedVolleys = 32;

    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCProjectileVolleyStateComponent, ComponentInit>(OnStateInit);
    }

    private void OnStateInit(Entity<NCProjectileVolleyStateComponent> entity, ref ComponentInit args)
    {
        // Robust components can be pooled after deletion; runtime correlation must never leak to another body.
        entity.Comp.PrimaryZones = new Dictionary<Guid, ProtoId<NCTargetZonePrototype>>();
        entity.Comp.Order = new Queue<Guid>();
    }

    public bool TryGetDistributedZone(
        EntityUid target,
        NCActionResolutionId resolutionId,
        uint projectileSequence,
        out ProtoId<NCTargetZonePrototype> zone)
    {
        zone = default;
        if (!TryComp<NCProjectileVolleyStateComponent>(target, out var state) ||
            !state.PrimaryZones.TryGetValue(resolutionId.Value, out var primary))
        {
            return false;
        }

        if (!_prototypes.TryIndex(primary, out NCTargetZonePrototype? primaryPrototype) ||
            primaryPrototype.NeighborZones.Count == 0 ||
            projectileSequence % 2 == 0)
        {
            zone = primary;
            return true;
        }

        var neighborIndex = (int) ((projectileSequence / 2) % (uint) primaryPrototype.NeighborZones.Count);
        zone = primaryPrototype.NeighborZones[neighborIndex];
        return true;
    }

    public void RecordPrimary(
        EntityUid target,
        NCActionResolutionId resolutionId,
        ProtoId<NCTargetZonePrototype> primary)
    {
        var state = EnsureComp<NCProjectileVolleyStateComponent>(target);
        if (state.PrimaryZones.ContainsKey(resolutionId.Value))
            return;

        while (state.Order.Count >= MaximumRememberedVolleys)
            state.PrimaryZones.Remove(state.Order.Dequeue());

        state.PrimaryZones.Add(resolutionId.Value, primary);
        state.Order.Enqueue(resolutionId.Value);
    }
}
