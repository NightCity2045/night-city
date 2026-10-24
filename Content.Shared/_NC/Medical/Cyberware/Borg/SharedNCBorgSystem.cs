// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Nutrition;
using Content.Shared.Item;
using Content.Shared.Interaction.Events;
using Content.Shared._NC.Medical.Function;

namespace Content.Shared._NC.Medical.Cyberware.Borg;

/// <summary>Predict the physical food inlet check; chemistry and medical outcomes remain authoritative.</summary>
public sealed partial class SharedNCBorgSystem : EntitySystem
{
    [Dependency] private SharedNCBodyFunctionSystem _bodyFunctions = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCBorgCoreComponent, IngestionAttemptEvent>(OnIngestionAttempt);
        SubscribeLocalEvent<NCBorgCoreComponent, GettingPickedUpAttemptEvent>(OnPickupAttempt);
        SubscribeLocalEvent<NCBorgCoreComponent, AttackAttemptEvent>(OnAttackAttempt);
    }

    private void OnAttackAttempt(Entity<NCBorgCoreComponent> core, ref AttackAttemptEvent args)
    {
        if (!core.Comp.ShellConnected || !_bodyFunctions.CanUseActiveHand(core))
            args.Cancel();
    }

    private void OnPickupAttempt(Entity<NCBorgCoreComponent> core, ref GettingPickedUpAttemptEvent args)
    {
        if (core.Comp.ShellConnected)
            args.Cancel();
    }

    private void OnIngestionAttempt(Entity<NCBorgCoreComponent> core, ref IngestionAttemptEvent args)
    {
        if (!core.Comp.FoodProcessorAvailable)
            args.Cancelled = true;
    }

    public void SetFoodProcessor(Entity<NCBorgCoreComponent> core, bool available, bool shellConnected)
    {
        if (core.Comp.FoodProcessorAvailable == available && core.Comp.ShellConnected == shellConnected)
            return;
        core.Comp.FoodProcessorAvailable = available;
        core.Comp.ShellConnected = shellConnected;
        Dirty(core);
    }
}
