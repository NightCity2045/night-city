// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Anatomy;

/// <summary>
/// One anatomical replacement site, not an accessory bay. The organ graph enforces one bone per category.
/// These definitions are shared by surgical graft selection and the diagnostic map.
/// </summary>
[Prototype("ncSkeletalSlot")]
public sealed partial class NCSkeletalSlotPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; }

    [DataField(required: true)]
    public ProtoId<OrganCategoryPrototype> Category { get; private set; }

    [DataField(required: true)]
    public ProtoId<OrganCategoryPrototype> ParentCategory { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCTargetZonePrototype> TargetZone { get; private set; }

    [DataField(required: true)]
    public Vector2 MapStart { get; private set; }

    [DataField(required: true)]
    public Vector2 MapEnd { get; private set; }

    [DataField]
    public int Order { get; private set; }
}
