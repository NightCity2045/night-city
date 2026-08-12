// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Systemic;

/// <summary>
/// One bounded burden channel and its contribution to whole-body physiology at maximum burden.
/// All rates and penalties are content values rather than hidden C# balance constants.
/// </summary>
[DataDefinition]
public sealed partial class NCSystemicChannelProfile
{
    [DataField(required: true)]
    public float MaximumBurden { get; private set; }

    [DataField]
    public float NaturalRecoveryPerSecond { get; private set; }

    [DataField]
    public float OxygenationPenaltyAtMaximum { get; private set; }

    [DataField]
    public float CardiacOutputPenaltyAtMaximum { get; private set; }

    [DataField]
    public float ShockContributionAtMaximum { get; private set; }

    [DataField]
    public float BrainFunctionPenaltyAtMaximum { get; private set; }

    [DataField]
    public float RhythmInstabilityAtMaximum { get; private set; }
}

/// <summary>
/// Human systemic physiology and scheduler policy. Exactly one default profile is used by authoritative humans.
/// </summary>
[Prototype("ncSystemicPhysiologyProfile")]
public sealed partial class NCSystemicPhysiologyProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public bool DefaultForHumans { get; private set; }

    [DataField(required: true)]
    public Dictionary<NCSystemicChannel, NCSystemicChannelProfile> Channels { get; private set; } = new();

    [DataField(required: true)]
    public float UnstableRhythmThreshold { get; private set; }

    [DataField(required: true)]
    public float ShockableArrestThreshold { get; private set; }

    [DataField(required: true)]
    public float DefibrillationReleaseThreshold { get; private set; }

    [DataField(required: true)]
    public TimeSpan ActiveUpdateInterval { get; private set; }

    [DataField(required: true)]
    public int ProcessedPayloadCapacity { get; private set; }
}

/// <summary>
/// Maps one compatibility damage type to a physiological burden. Positive damage adds exposure while negative
/// damage is interpreted as explicit recovery for the same channel.
/// </summary>
[Prototype("ncSystemicDamageProfile")]
public sealed partial class NCSystemicDamageProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public NCSystemicChannel Channel { get; private set; }

    [DataField(required: true)]
    public float ExposurePerDamage { get; private set; }

    [DataField(required: true)]
    public float RecoveryPerHealing { get; private set; }
}
