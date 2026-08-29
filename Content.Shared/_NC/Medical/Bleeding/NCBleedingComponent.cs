// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Bleeding;

/// <summary>
/// One independently treatable bleeding source tied to a stable wound identifier.
/// </summary>
[DataDefinition]
public sealed partial class NCBleedingSourceRecord
{
    [DataField]
    public Guid SourceId;

    [DataField]
    public Guid WoundId;

    [DataField(required: true)]
    public ProtoId<NCTargetZonePrototype> TargetZone;

    [DataField(required: true)]
    public ProtoId<OrganCategoryPrototype> RegionCategory;

    [DataField]
    public NCBleedingChannel Channel;

    [DataField]
    public float BaseRate;

    [DataField]
    public float CurrentRate;

    [DataField]
    public bool PressureApplied;

    [DataField]
    public bool Bandaged;

    [DataField]
    public bool Clamped;

    [DataField]
    public bool Tourniqueted;

    [DataField]
    public bool Cauterized;
}

public enum NCBleedingChannel : byte
{
    ExternalCapillary,
    ExternalVenous,
    ExternalArterial,
    Internal,
}

/// <summary>
/// Server-only collection of active bleeding sources. It is processed by the central body scheduler,
/// never by a per-wound update loop.
/// </summary>
[RegisterComponent]
public sealed partial class NCBleedingComponent : Component
{
    [DataField]
    public List<NCBleedingSourceRecord> Sources = [];

    [DataField]
    public float OverflowExternalRate;

    [DataField]
    public float OverflowInternalRate;

    /// <summary>
    /// Region attribution for bounded wound overflow. The aggregate field above remains the authoritative
    /// total for backwards-compatible snapshots; this map determines which live cavity receives the loss.
    /// </summary>
    [DataField]
    public Dictionary<ProtoId<OrganCategoryPrototype>, float> OverflowInternalRatesByRegion = new();

    [DataField]
    public float InternalBloodLoss;

    /// <summary>
    /// Current non-circulating blood whose legacy source cannot be mapped to a live anatomical cavity.
    /// It remains diagnosable and persistent instead of disappearing from the physical ledger.
    /// </summary>
    [DataField]
    public float UnlocalizedInternalBloodVolume;

    [ViewVariables]
    public float ActiveExternalRate;

    [ViewVariables]
    public float ActiveInternalRate;

    [ViewVariables]
    public float ActiveArterialRate;
}
