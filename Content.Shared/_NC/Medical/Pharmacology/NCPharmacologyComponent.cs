// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Pharmacology;

/// <summary>
/// Bounded server-authoritative drug state for one medical body. Upstream chemistry owns the solution and
/// metabolism cadence; this component only stores the physiological consequences of metabolized quantities.
/// </summary>
[RegisterComponent]
public sealed partial class NCPharmacologyComponent : Component
{
    [DataField]
    public ProtoId<NCPharmacologyProfilePrototype> Profile;

    [DataField("initialized")]
    public bool StateInitialized;

    [DataField]
    public Dictionary<ProtoId<NCDrugProfilePrototype>, NCActiveDrugExposure> ActiveDrugs = new();

    [DataField]
    public float OverdoseBurden;

    [DataField]
    public TimeSpan LastEvaluation;

    [ViewVariables]
    public NCPharmacologyModifiers CachedModifiers = NCPharmacologyModifiers.Neutral;

    [ViewVariables]
    public readonly List<ProtoId<NCDrugProfilePrototype>> RemovalBuffer = [];

    [ViewVariables]
    public readonly List<ProtoId<NCDrugProfilePrototype>> EvaluationOrder = [];
}

/// <summary>
/// Semantic concentration record suitable for persistence. It deliberately contains no entity references.
/// </summary>
[DataDefinition]
public sealed partial class NCActiveDrugExposure
{
    [DataField(required: true)]
    public ProtoId<NCDrugProfilePrototype> Profile;

    [DataField]
    public float Concentration;

    [DataField]
    public float CumulativeDose;

    [DataField]
    public TimeSpan LastAdministration;
}
