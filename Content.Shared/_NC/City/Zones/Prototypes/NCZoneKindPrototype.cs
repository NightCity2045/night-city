// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.City.Zones.Prototypes;

/// <summary>
/// Data-driven rules and editor presentation for a category of city zone.
/// </summary>
[Prototype("ncZoneKind")]
public sealed partial class NCZoneKindPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public int HierarchyRank { get; private set; }

    [DataField]
    public NCZoneContextSlot ContextSlot { get; private set; }

    [DataField]
    public HashSet<ProtoId<NCZoneKindPrototype>> AllowedParents { get; private set; } = [];

    [DataField]
    public HashSet<NCZoneGeometryKind> AllowedGeometry { get; private set; } = [];

    [DataField]
    public Color EditorColor { get; private set; } = Color.White;

    [DataField]
    public bool RequiresParent { get; private set; } = true;

    [DataField]
    public bool SupportsActivityMode { get; private set; }
}
