// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Armor;

/// <summary>
/// Persistent per-item armor state. Exact CurrentSP remains server-authoritative and is not networked.
/// </summary>
[RegisterComponent]
public sealed partial class NCArmorComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCArmorProfilePrototype> Profile;

    /// <summary>
    /// One entry per profile section. The server initializes missing entries from YAML MaxSP values.
    /// </summary>
    [DataField]
    public List<NCArmorSectionState> Sections = [];

    /// <summary>
    /// Invalidation revision for bounded coverage caches and persistence observers.
    /// </summary>
    [ViewVariables]
    public uint Revision;
}

/// <summary>
/// MaxSP and CurrentSP belonging to one concrete armor entity instance.
/// </summary>
[DataDefinition]
public sealed partial class NCArmorSectionState
{
    [DataField(required: true)]
    public ProtoId<NCArmorSectionPrototype> Section;

    [DataField(required: true)]
    public FixedPoint2 MaxSp;

    [DataField(required: true)]
    public FixedPoint2 CurrentSp;
}
