// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors.Components;
using Content.Shared.CombatMode;
using Content.Shared.Cuffs.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Mobs;
using Content.Shared._NC.LivingCity.Actors;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Converts existing combat, medical, police and interaction ownership into objective StateTree conditions. Existing
/// SS14 HTN/behavior systems retain their action logic; this adapter only prevents competing route ownership.
/// </summary>
public sealed partial class NCCityPhysicalBehaviorAdapterSystem : EntitySystem
{
    public void BuildConditions(
        EntityUid actor,
        NCCityPhysicalExecutionComponent execution,
        HashSet<NCCityPhysicalCondition> result)
    {
        result.Clear();
        if (execution.GoalId != Guid.Empty && execution.DestinationZone.IsValid)
            result.Add(NCCityPhysicalCondition.TravelIntent);
        if (TryComp<CombatModeComponent>(actor, out var combat) && combat.IsInCombatMode)
            result.Add(NCCityPhysicalCondition.CombatActive);
        if (TryComp<NCCityLocalPerceptionComponent>(actor, out var perception) && perception.HostileCount > 0)
            result.Add(NCCityPhysicalCondition.ThreatObserved);
        if (TryComp<CuffableComponent>(actor, out var cuffable) && cuffable.CuffedHandCount > 0)
            result.Add(NCCityPhysicalCondition.Detained);
        if (TryComp<MobStateComponent>(actor, out var mob) &&
            mob.CurrentState is MobState.Critical or MobState.Dead)
        {
            result.Add(NCCityPhysicalCondition.Incapacitated);
        }
        AddInterruptConditions(execution.Interrupts, result);
    }

    internal static void AddInterruptConditions(
        NCCityPhysicalInterruptFlags interrupts,
        HashSet<NCCityPhysicalCondition> result)
    {
        if ((interrupts & NCCityPhysicalInterruptFlags.Combat) != 0)
            result.Add(NCCityPhysicalCondition.CombatActive);
        if ((interrupts & NCCityPhysicalInterruptFlags.Conversation) != 0)
            result.Add(NCCityPhysicalCondition.ConversationClaim);
        if ((interrupts & NCCityPhysicalInterruptFlags.Detained) != 0)
            result.Add(NCCityPhysicalCondition.Detained);
        if ((interrupts & NCCityPhysicalInterruptFlags.Medical) != 0)
            result.Add(NCCityPhysicalCondition.MedicalClaim);
        if ((interrupts & NCCityPhysicalInterruptFlags.Police) != 0)
            result.Add(NCCityPhysicalCondition.PoliceClaim);
        if ((interrupts & NCCityPhysicalInterruptFlags.Scripted) != 0)
            result.Add(NCCityPhysicalCondition.ScriptedClaim);
    }
}
