// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.NPC.Components;
using Content.Server.NPC.Systems;

namespace Content.Server._NC.Combat.Damage;

/// <summary>
/// Routes confirmed Night City combat impacts, which bypass legacy damage events, to NPC retaliation.
/// </summary>
public sealed partial class NCNPCRetaliationSystem : EntitySystem
{
    [Dependency] private NPCRetaliationSystem _retaliation = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<NPCRetaliationComponent, NCCombatImpactConfirmedEvent>(OnCombatImpactConfirmed);
    }

    private void OnCombatImpactConfirmed(
        Entity<NPCRetaliationComponent> victim,
        ref NCCombatImpactConfirmedEvent args)
    {
        _retaliation.TryRetaliate(victim, args.Instigator);
    }
}
