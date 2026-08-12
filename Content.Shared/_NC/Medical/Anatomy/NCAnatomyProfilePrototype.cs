// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Anatomy;

/// <summary>
/// Selects the zone catalogue and doll presentation for a live SS14 organ graph.
/// Containment is never duplicated here; it is read from ParentOrgan/ChildOrgan relationships.
/// </summary>
[Prototype("ncAnatomyProfile")]
public sealed partial class NCAnatomyProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Ordered zones shared by combat and medical interactions.
    /// </summary>
    [DataField(required: true)]
    public List<ProtoId<NCTargetZonePrototype>> TargetZones { get; private set; } = [];

    /// <summary>
    /// Default deterministic fallback for an invalid or missing automatic candidate.
    /// </summary>
    [DataField(required: true)]
    public ProtoId<NCTargetZonePrototype> AutomaticFallbackZone { get; private set; }

    /// <summary>
    /// Pure presentation data consumed by the reusable client doll.
    /// </summary>
    [DataField]
    public ProtoId<NCTargetDollProfilePrototype> TargetDoll { get; private set; }
}
