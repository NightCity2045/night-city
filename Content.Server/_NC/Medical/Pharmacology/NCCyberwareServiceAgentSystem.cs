// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Cyberware;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Pharmacology;
using Content.Shared.EntityEffects;

namespace Content.Server._NC.Medical.Pharmacology;

/// <summary>
/// Hands a metabolized service-agent quantity to the bounded cyberware graph. The generated identifier is
/// correlation identity for this server-observed metabolism event, never a gameplay roll.
/// </summary>
public sealed partial class NCCyberwareServiceAgentSystem :
    EntityEffectSystem<NCMedicalBodyComponent, NCApplyCyberwareServiceAgent>
{
    [Dependency] private NCCyberwareSystem _cyberware = default!;

    protected override void Effect(
        Entity<NCMedicalBodyComponent> entity,
        ref EntityEffectEvent<NCApplyCyberwareServiceAgent> args)
    {
        var units = args.Scale * args.Effect.UnitsPerReagent;
        if (!float.IsFinite(units) || units <= 0f)
            return;

        _cyberware.TryApplyServiceToBody(
            entity.Owner,
            NCActionResolutionId.New(),
            args.Effect.ServiceProfile,
            units);
    }
}
