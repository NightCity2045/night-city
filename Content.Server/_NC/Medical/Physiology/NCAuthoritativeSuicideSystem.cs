// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Suicide;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Interaction.Events;

namespace Content.Server._NC.Medical.Physiology;

/// <summary>
/// Converts an accepted suicide command into irreversible authoritative physiology. The vanilla
/// environment/item handlers may still provide their messages and visuals, but compatibility HP can
/// never be the source of death for an NC human.
/// </summary>
public sealed partial class NCAuthoritativeSuicideSystem : EntitySystem
{
    [Dependency] private NCPhysiologySystem _physiology = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Run after vanilla has offered the held item/environment its normal suicide interaction.
        SubscribeLocalEvent<NCMedicalBodyComponent, SuicideEvent>(
            OnSuicide,
            after: [typeof(SuicideSystem)]);
    }

    private void OnSuicide(Entity<NCMedicalBodyComponent> entity, ref SuicideEvent args)
    {
        if (_physiology.ForceIrreversibleDeath(entity.Owner))
            args.Handled = true;
    }
}
