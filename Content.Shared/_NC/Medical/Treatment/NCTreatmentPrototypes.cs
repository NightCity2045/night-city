// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Bleeding;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Treatment;

/// <summary>
/// Data-driven first-aid policy for one physiology family. Rates are multipliers of the measured,
/// prototype-derived bleeding source rate; field treatment never rewrites the underlying wound trauma.
/// </summary>
[Prototype("ncTreatmentProfile")]
public sealed partial class NCTreatmentProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public bool DefaultForHumans { get; private set; }

    [DataField(required: true)]
    public Dictionary<NCBleedingChannel, float> PressureMultipliers { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCBleedingChannel, float> BandageMultipliers { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCBleedingChannel, float> HemostaticMultipliers { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCBleedingChannel, float> TourniquetMultipliers { get; private set; } = new();

    /// <summary>
    /// Maps every tourniquet-compatible target zone to its canonical limb root. Zones absent from this
    /// map are not valid tourniquet sites. Distal zones can share the proximal limb root.
    /// </summary>
    [DataField(required: true)]
    public Dictionary<ProtoId<NCTargetZonePrototype>, ProtoId<NCTargetZonePrototype>> TourniquetZoneRoots
        { get; private set; } = new();

    [DataField(required: true)]
    public float BandageAbsorptionCapacity { get; private set; }

    [DataField(required: true)]
    public float BandageSterility { get; private set; }

    [DataField(required: true)]
    public float BandageSterilityDecayPerSecond { get; private set; }

    [DataField(required: true)]
    public float SaturatedBandageContamination { get; private set; }

    [DataField(required: true)]
    public TimeSpan ControlledReleaseAfter { get; private set; }

    [DataField(required: true)]
    public TimeSpan IschemiaRiskAfter { get; private set; }

    [DataField(required: true)]
    public TimeSpan CriticalIschemiaAfter { get; private set; }

    [DataField(required: true)]
    public TimeSpan ReleaseReboundDuration { get; private set; }

    [DataField(required: true)]
    public float ReleaseReboundMultiplier { get; private set; }

    [DataField(required: true)]
    public int ProcessedActionCapacity { get; private set; }

    [DataField(required: true)]
    public int AuditHistoryCapacity { get; private set; }
}

/// <summary>
/// Connects a spawnable treatment item to one deterministic treatment action and balance profile.
/// Interaction code can consume this contract without knowing any medical constants.
/// </summary>
[Prototype("ncTreatmentCapability")]
public sealed partial class NCTreatmentCapabilityPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public NCTreatmentKind Kind { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCTreatmentProfilePrototype> Profile { get; private set; }

    [DataField(required: true)]
    public TimeSpan ApplicationDuration { get; private set; }

    [DataField]
    public float InteractionRange { get; private set; } = 2f;

    [DataField]
    public NCTreatmentResourceKind ResourceKind { get; private set; }

    [DataField]
    public int ResourceCost { get; private set; } = 1;
}
