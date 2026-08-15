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
    public NCSeverancePathRule Impact { get; private set; } = new();

    /// <summary>
    /// Optional aggregate gate for multiple physical contacts belonging to one immutable action, such as pellets
    /// from one shotgun shell. Trauma is accumulated per body region, never across separate attacks.
    /// </summary>
    [DataField]
    public NCSeverancePathRule? CompoundAction { get; private set; }

    /// <summary>
    /// Optional finishing gate for a new impact against a limb whose live structure is already nearly destroyed.
    /// This uses no historical damage counter: integrity, bone and tendon entities remain the source of truth.
    /// </summary>
    [DataField]
    public NCSeverancePathRule? MangledFinisher { get; private set; }

    [DataField(required: true)]
    public NCSeverancePathRule MechanicalImpact { get; private set; } = new();

    [DataField]
    public NCSeverancePathRule? MechanicalCompoundAction { get; private set; }

    [DataField]
    public NCSeverancePathRule? MechanicalMangledFinisher { get; private set; }
}

[DataDefinition]
public sealed partial class NCSeverancePathRule
{
    [DataField(required: true)]
    public NCWoundSeverity MinimumSeverity { get; private set; }

    [DataField(required: true)]
    public float MinimumTrauma { get; private set; }

    [DataField(required: true)]
    public float MaximumIntegrityFraction { get; private set; }

    [DataField]
    public NCBoneState? MinimumBoneState { get; private set; }

    [DataField]
    public float? MaximumTendonIntegrity { get; private set; }
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
        float compoundActionTrauma,
        float integrityFraction,
        NCAnatomicalMaterialKind material,
        NCBoneState? boneState,
        float? tendonIntegrity)
    {
        if (!float.IsFinite(impactTrauma) ||
            !float.IsFinite(integrityFraction) ||
            !float.IsFinite(compoundActionTrauma) ||
            impactTrauma <= 0f ||
            compoundActionTrauma < impactTrauma)
        {
            return false;
        }

        if (material == NCAnatomicalMaterialKind.Mechanical)
        {
            return Passes(mechanism.MechanicalImpact, severity, impactTrauma, integrityFraction, null, null) ||
                   mechanism.MechanicalCompoundAction is { } compound &&
                   Passes(compound, severity, compoundActionTrauma, integrityFraction, null, null) ||
                   mechanism.MechanicalMangledFinisher is { } finisher &&
                   Passes(finisher, severity, impactTrauma, integrityFraction, null, null);
        }

        return Passes(mechanism.Impact, severity, impactTrauma, integrityFraction, boneState, tendonIntegrity) ||
               mechanism.CompoundAction is { } compoundAction &&
               Passes(compoundAction, severity, compoundActionTrauma, integrityFraction, boneState, tendonIntegrity) ||
               mechanism.MangledFinisher is { } mangled &&
               Passes(mangled, severity, impactTrauma, integrityFraction, boneState, tendonIntegrity);
    }

    public static bool IsValid(NCSeverancePathRule rule)
    {
        return float.IsFinite(rule.MinimumTrauma) &&
               rule.MinimumTrauma > 0f &&
               float.IsFinite(rule.MaximumIntegrityFraction) &&
               rule.MaximumIntegrityFraction is >= 0f and <= 1f &&
               (rule.MaximumTendonIntegrity is not { } tendon ||
                float.IsFinite(tendon) && tendon is >= 0f and <= 1f);
    }

    private static bool Passes(
        NCSeverancePathRule rule,
        NCWoundSeverity severity,
        float trauma,
        float integrityFraction,
        NCBoneState? boneState,
        float? tendonIntegrity)
    {
        if (severity < rule.MinimumSeverity ||
            trauma < rule.MinimumTrauma ||
            integrityFraction > rule.MaximumIntegrityFraction)
        {
            return false;
        }

        if (rule.MinimumBoneState is { } requiredBone &&
            boneState is { } actualBone &&
            actualBone < requiredBone)
        {
            return false;
        }

        if (rule.MaximumTendonIntegrity is { } maximumTendon &&
            tendonIntegrity is { } actualTendon &&
            actualTendon > maximumTendon)
        {
            return false;
        }

        return true;
    }
}
