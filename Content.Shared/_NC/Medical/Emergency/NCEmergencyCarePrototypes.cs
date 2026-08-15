// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Damage;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared.Damage.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Emergency;

/// <summary>
/// Human emergency-care balance. No treatment checks or random modifiers exist: measured patient state,
/// item capability and these values fully determine every outcome.
/// </summary>
[Prototype("ncEmergencyCareProfile")]
public sealed partial class NCEmergencyCareProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public bool DefaultForHumans { get; private set; }

    [DataField(required: true)]
    public TimeSpan CprDuration { get; private set; }

    [DataField(required: true)]
    public TimeSpan CprApplicationDuration { get; private set; }

    [DataField]
    public float CprInteractionRange { get; private set; } = 2f;

    [DataField(required: true)]
    public float CprAssistedPerfusion { get; private set; }

    [DataField(required: true)]
    public float CprBrainInjuryRateMultiplier { get; private set; }

    [DataField(required: true)]
    public float CprMaximumNaturalPerfusion { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCTargetZonePrototype> CprAssessmentZone { get; private set; }

    [DataField(required: true)]
    public float CprMaximumChestTrauma { get; private set; }

    [DataField(required: true)]
    public float OxygenRespiratorySupport { get; private set; }

    [DataField(required: true)]
    public float VentilationRespiratorySupport { get; private set; }

    [DataField(required: true)]
    public float MaximumCrystalloidVolume { get; private set; }

    [DataField(required: true)]
    public float MaximumBloodVolumeMultiplier { get; private set; }

    /// <summary>
    /// Donor compatibility token to the recipient tokens it may be given to.
    /// </summary>
    [DataField(required: true)]
    public Dictionary<string, List<string>> TransfusionCompatibility { get; private set; } = new();

    [DataField(required: true)]
    public ProtoId<NCCombatDamageProfilePrototype> DefibrillationBurnProfile { get; private set; }

    [DataField(required: true)]
    public ProtoId<DamageTypePrototype> DefibrillationBurnDamageType { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCTargetZonePrototype> DefibrillationBurnZone { get; private set; }

    [DataField(required: true)]
    public float DefibrillationBurnDamage { get; private set; }

    [DataField(required: true)]
    public int ProcessedActionCapacity { get; private set; }

    [DataField(required: true)]
    public int AuditHistoryCapacity { get; private set; }
}

/// <summary>
/// Connects one spawnable item to a deterministic emergency-care operation. Duration, delivered volume,
/// donor group and charge cost are item properties and therefore live in data rather than action code.
/// </summary>
[Prototype("ncEmergencyCareCapability")]
public sealed partial class NCEmergencyCareCapabilityPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public NCEmergencyCareKind Kind { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCEmergencyCareProfilePrototype> Profile { get; private set; }

    [DataField]
    public TimeSpan SupportDuration { get; private set; }

    [DataField]
    public float InfusionVolume { get; private set; }

    [DataField]
    public string DonorCompatibility { get; private set; } = string.Empty;

    [DataField(required: true)]
    public int ChargeCost { get; private set; }

    [DataField]
    public NCEmergencyCareResourceKind ResourceKind { get; private set; }

    [DataField(required: true)]
    public TimeSpan ApplicationDuration { get; private set; }

    [DataField]
    public float InteractionRange { get; private set; } = 2f;
}
