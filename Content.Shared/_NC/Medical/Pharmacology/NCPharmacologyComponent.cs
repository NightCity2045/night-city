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

    /// <summary>Tolerance, dependence and craving towards compounds that habituate. Persistent physiology.</summary>
    [DataField]
    public Dictionary<ProtoId<NCDrugProfilePrototype>, NCDrugDependenceState> Dependence = new();

    [DataField]
    public TimeSpan LastEvaluation;

    /// <summary>Pain relief a locally applied dose gives at the wound. Short lived, no systemic cost.</summary>
    [DataField]
    public float LocalAnalgesia;

    /// <summary>Bleeding reduction a locally applied dose gives at the wound.</summary>
    [DataField]
    public float LocalHemostasis;

    [DataField]
    public TimeSpan LocalRemaining;

    /// <summary>How many separate infections this body has had. Each one may be caused by a different strain.</summary>
    [DataField]
    public int StrainEpisode;

    /// <summary>Bacterial strain of the current infection, derived from the character and the episode.</summary>
    [ViewVariables]
    public ProtoId<Lab.NCPathogenStrainPrototype>? Strain;

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

    /// <summary>
    /// Multiplier on how fast the body clears this compound. Below one when the doses carried a metabolic
    /// blocker; blended by concentration when doses with different blockers overlap.
    /// </summary>
    [DataField]
    public float ClearanceFactor = 1f;
}
