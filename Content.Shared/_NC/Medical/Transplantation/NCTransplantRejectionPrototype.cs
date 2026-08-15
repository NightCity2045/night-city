// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Transplantation;

/// <summary>
/// Deterministic allograft progression. Every rate is balance data and immunosuppression is measured world
/// state produced by pharmacology rather than a character modifier or check.
/// </summary>
[Prototype("ncTransplantRejectionProfile")]
public sealed partial class NCTransplantRejectionProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public bool DefaultForHumans { get; private set; }

    [DataField(required: true)]
    public TimeSpan ActiveUpdateInterval { get; private set; }

    [DataField(required: true)]
    public float MaximumBurden { get; private set; }

    [DataField(required: true)]
    public float BaseGrowthPerSecond { get; private set; }

    [DataField(required: true)]
    public float GrowthSuppressionAtMaximumImmunosuppression { get; private set; }

    [DataField(required: true)]
    public float RecoveryPerImmunosuppressionPerSecond { get; private set; }

    [DataField(required: true)]
    public List<float> StageThresholds { get; private set; } = [];

    [DataField(required: true)]
    public Dictionary<NCTransplantRejectionStage, float> IntegrityDamagePerSecond { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCTransplantRejectionStage, float> FunctionDamagePerSecond { get; private set; } = new();

    [DataField(required: true)]
    public Dictionary<NCTransplantRejectionStage, float> NecrosisPerSecond { get; private set; } = new();
}
