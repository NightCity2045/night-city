// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Lifecycle;
using Content.Shared.Gibbing;
using Content.Shared.Mobs;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>Applies checkpoint and succession handling when a bound physical actor dies.</summary>
public sealed partial class NCPhysicalActorLifecycleSystem : EntitySystem
{
    [Dependency] private NCPhysicalActorCheckpointSystem _checkpoints = default!;
    [Dependency] private NCDeathSuccessionSystem _deathSuccession = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityActorBindingComponent, MobStateChangedEvent>(OnPhysicalActorStateChanged);
        SubscribeLocalEvent<NCCityActorBindingComponent, BeingGibbedEvent>(OnPhysicalActorGibbed);
    }

    private void OnPhysicalActorStateChanged(
        Entity<NCCityActorBindingComponent> entity,
        ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead)
            return;

        // Robust component events have one subscriber. Lifecycle owns that subscription and explicitly fans the
        // authoritative death out to durability and succession instead of registering conflicting handlers.
        _checkpoints.ObserveTerminalState(entity.Comp.ActorId);
        _deathSuccession.ObservePhysicalDeath(entity.Comp.ActorId);
    }

    private void OnPhysicalActorGibbed(
        Entity<NCCityActorBindingComponent> entity,
        ref BeingGibbedEvent args)
    {
        _checkpoints.ObserveTerminalState(entity.Comp.ActorId);
        _deathSuccession.ObservePhysicalDeath(entity.Comp.ActorId);
    }
}
