// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared._NC.LivingCity.Groups;

/// <summary>
/// Data-driven structural limits and initial strategic state for a persistent group.
/// </summary>
[Prototype("ncCityGroupProfile")]
public sealed partial class NCCityGroupProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField]
    public int MaximumMembers { get; private set; } = 16;

    [DataField]
    public int MaximumSnapshotHistory { get; private set; } = 128;

    [DataField]
    public bool AllowSplit { get; private set; } = true;

    [DataField]
    public bool AllowMerge { get; private set; } = true;

    [DataField]
    public bool MaterializeTogether { get; private set; } = true;

    [DataField]
    public NCCityGroupFormationPolicy FormationPolicy { get; private set; } = NCCityGroupFormationPolicy.Loose;

    [DataField]
    public int InitialTransportCapacity { get; private set; }

    [DataField]
    public int InitialCargoUnits { get; private set; }

    [DataField]
    public int InitialSupplyUnits { get; private set; }

    [DataField]
    public float InitialMorale { get; private set; } = 0.5f;
}
