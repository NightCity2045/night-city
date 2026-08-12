// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared.Body;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Wounds;

/// <summary>
/// Compact server-side wound state. A stable <see cref="Guid"/> is used instead of an EntityUid so the
/// record can survive body reconstruction and later persistence migrations.
/// </summary>
[DataDefinition]
public sealed partial class NCWoundRecord
{
    [DataField]
    public Guid WoundId;

    [DataField(required: true)]
    public ProtoId<NCWoundPrototype> Prototype;

    [DataField(required: true)]
    public ProtoId<NCTargetZonePrototype> TargetZone;

    [DataField(required: true)]
    public ProtoId<OrganCategoryPrototype> RegionCategory;

    [DataField]
    public ProtoId<DamageTypePrototype>? PrimaryDamageType;

    [DataField]
    public NCWoundType WoundType;

    [DataField]
    public NCTissueDepth TissueDepth;

    [DataField]
    public NCWoundSeverity Severity;

    [DataField]
    public NCWoundStage Stage = NCWoundStage.Fresh;

    [DataField]
    public NCWoundClosureState ClosureState = NCWoundClosureState.Open;

    [DataField]
    public NCWoundTreatmentFlags TreatmentFlags;

    [DataField]
    public FixedPoint2 IntegrityDamage;

    [DataField]
    public float Area;

    [DataField]
    public float ExternalBleedRate;

    [DataField]
    public float InternalBleedRate;

    /// <summary>
    /// Untreated rates captured when the wound is created. Longitudinal healing derives current rates
    /// from these values, so reopening is deterministic and never invents blood loss from the latest tick.
    /// </summary>
    [DataField]
    public float InitialExternalBleedRate;

    [DataField]
    public float InitialInternalBleedRate;

    [DataField]
    public NCBleedKind PrimaryBleedKind;

    [DataField]
    public float Pain;

    [DataField]
    public float InitialPain;

    [DataField]
    public float Contamination;

    [DataField]
    public float Infection;

    [DataField]
    public NCInfectionStage InfectionStage;

    [DataField]
    public float Necrosis;

    [DataField]
    public float HealingProgress;

    /// <summary>
    /// Highest healing progress that already restored local integrity. Reopening may lower current progress,
    /// but repeated heal/reopen cycles cannot manufacture healthy tissue.
    /// </summary>
    [DataField]
    public float PeakHealingProgress;

    [DataField]
    public float ReopenStress;

    [DataField]
    public float ScarRisk;

    [DataField]
    public Guid? EmbeddedObjectId;

    [DataField]
    public Guid? ExitWoundId;

    [DataField]
    public Guid ForensicResolutionId;

    [DataField]
    public TimeSpan CreatedAt;

    [DataField]
    public TimeSpan ModifiedAt;

    [DataField]
    public TimeSpan Age;
}

/// <summary>
/// Compact permanent consequence of a resolved wound. It is semantic persistence data and deliberately
/// contains no entity identity or transient treatment reference.
/// </summary>
[DataDefinition]
public sealed partial class NCScarRecord
{
    [DataField]
    public Guid ScarId;

    [DataField]
    public Guid SourceWoundId;

    [DataField(required: true)]
    public ProtoId<NCTargetZonePrototype> TargetZone;

    [DataField(required: true)]
    public ProtoId<OrganCategoryPrototype> RegionCategory;

    [DataField]
    public NCWoundType WoundType;

    [DataField]
    public NCWoundSeverity OriginalSeverity;

    [DataField]
    public float Area;

    [DataField]
    public float Disfigurement;

    [DataField]
    public float Contracture;

    [DataField]
    public TimeSpan CreatedAt;
}

public enum NCWoundType : byte
{
    Ballistic,
    Slash,
    Pierce,
    Blunt,
    Thermal,
    Electrical,
    Blast,
    Cold,
    Pressure,
    Chemical,
}

public enum NCTissueDepth : byte
{
    Surface,
    SoftTissue,
    Vascular,
    Structural,
    Cavity,
    InternalOrgan,
}

public enum NCWoundSeverity : byte
{
    Minor,
    Moderate,
    Severe,
    Critical,
    Catastrophic,
}

public enum NCWoundStage : byte
{
    Fresh,
    Open,
    Treated,
    Closed,
    Healing,
    Scarred,
    Reopened,
    Infected,
    Necrotic,
}

public enum NCInfectionStage : byte
{
    None,
    Contaminated,
    Localized,
    Deep,
    Organ,
    Systemic,
    Sepsis,
}

public enum NCWoundClosureState : byte
{
    Open,
    TemporarilyClosed,
    Sutured,
    Sealed,
    Healed,
}

public enum NCBleedKind : byte
{
    None,
    Capillary,
    Venous,
    Arterial,
    Internal,
}

[Flags]
public enum NCWoundTreatmentFlags : ushort
{
    None = 0,
    Pressure = 1 << 0,
    Bandaged = 1 << 1,
    Hemostatic = 1 << 2,
    Clamped = 1 << 3,
    Tourniqueted = 1 << 4,
    Cauterized = 1 << 5,
    Debrided = 1 << 6,
    Drained = 1 << 7,
}

/// <summary>
/// Bounded de-duplication key for one physical impact. It prevents the same impact adapter event from
/// producing multiple wound records while allowing one projectile to hit more than once.
/// </summary>
public readonly record struct NCProcessedImpactKey(
    Guid ResolutionId,
    uint ProjectileSequence,
    uint ImpactSequence);
