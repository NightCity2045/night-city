// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Anatomy.Detachment;

/// <summary>
/// Human-authoritative rules for traumatic limb separation. The resolver is deliberately deterministic:
/// a limb separates only when the current impact and the live structural state satisfy every configured gate.
/// </summary>
[Prototype("ncTraumaticSeveranceProfile")]
public sealed partial class NCTraumaticSeveranceProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public ProtoId<NCTraumaProfilePrototype> StumpTraumaProfile { get; private set; }

    [DataField(required: true)]
    public Dictionary<NCWoundType, NCSeveranceMechanismRule> Mechanisms { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<ProtoId<OrganCategoryPrototype>, NCSeveranceRegionRule> Regions { get; private set; } = new();
}

[DataDefinition]
public sealed partial class NCSeveranceMechanismRule
{
    [DataField(required: true)]
    public NCWoundSeverity MinimumSeverity { get; private set; }

    [DataField(required: true)]
    public float MinimumImpactTrauma { get; private set; }

    [DataField(required: true)]
    public float MaximumIntegrityFraction { get; private set; }

    [DataField]
    public NCBoneState? MinimumBoneState { get; private set; }

    [DataField]
    public float? MaximumTendonIntegrity { get; private set; }

    [DataField(required: true)]
    public float MechanicalMinimumImpactTrauma { get; private set; }

    [DataField(required: true)]
    public float MechanicalMaximumIntegrityFraction { get; private set; }
}

[DataDefinition]
public sealed partial class NCSeveranceRegionRule
{
    [DataField(required: true)]
    public ProtoId<NCTargetZonePrototype> TargetZone { get; private set; }

    [DataField]
    public ProtoId<OrganCategoryPrototype>? BoneCategory { get; private set; }

    [DataField]
    public ProtoId<OrganCategoryPrototype>? TendonCategory { get; private set; }

    [DataField(required: true)]
    public float StumpTrauma { get; private set; }

    [DataField(required: true)]
    public float MechanicalStumpTraumaMultiplier { get; private set; }
}

/// <summary>
/// Pure threshold helper shared with prototype validation and balance regression tests.
/// </summary>
public static class NCTraumaticSeveranceRules
{
    public static bool CanSever(
        NCSeveranceMechanismRule mechanism,
        NCWoundSeverity severity,
        float impactTrauma,
        float integrityFraction,
        NCAnatomicalMaterialKind material,
        NCBoneState? boneState,
        float? tendonIntegrity)
    {
        if (!float.IsFinite(impactTrauma) ||
            !float.IsFinite(integrityFraction) ||
            impactTrauma <= 0f ||
            severity < mechanism.MinimumSeverity)
        {
            return false;
        }

        if (material == NCAnatomicalMaterialKind.Mechanical)
        {
            return impactTrauma >= mechanism.MechanicalMinimumImpactTrauma &&
                   integrityFraction <= mechanism.MechanicalMaximumIntegrityFraction;
        }

        if (impactTrauma < mechanism.MinimumImpactTrauma ||
            integrityFraction > mechanism.MaximumIntegrityFraction)
        {
            return false;
        }

        if (mechanism.MinimumBoneState is { } requiredBone &&
            boneState is { } actualBone &&
            actualBone < requiredBone)
        {
            return false;
        }

        if (mechanism.MaximumTendonIntegrity is { } maximumTendon &&
            tendonIntegrity is { } actualTendon &&
            actualTendon > maximumTendon)
        {
            return false;
        }

        return true;
    }
}
