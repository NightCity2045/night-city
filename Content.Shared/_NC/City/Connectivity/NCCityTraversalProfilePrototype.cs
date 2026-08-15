// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using Content.Shared._NC.City.ZoneTemplates.Components;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.City.Connectivity;

/// <summary>
/// Data-driven cost and capability profile used by semantic city routing.
/// </summary>
[Prototype("ncCityTraversalProfile")]
public sealed partial class NCCityTraversalProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public NCCityTravelMode TravelModes { get; private set; }

    [DataField(required: true)]
    public float BaseTravelSeconds { get; private set; }

    [DataField]
    public float CapacityPerMinute { get; private set; } = 60f;

    [DataField]
    public float ThreatCost { get; private set; }
}

/// <summary>
/// Maps existing mapper-authored vertical-link types to traversal profiles without C# balance switches.
/// Exactly one rules prototype must be loaded.
/// </summary>
[Prototype("ncCityConnectivityRules")]
public sealed partial class NCCityConnectivityRulesPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public Dictionary<NCVerticalLinkType, ProtoId<NCCityTraversalProfilePrototype>> VerticalProfiles { get; private set; } = [];

    [DataField(required: true)]
    public ProtoId<NCCityTraversalProfilePrototype> DoorProfile { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCCityTraversalProfilePrototype> OpenBoundaryProfile { get; private set; }

    /// <summary>
    /// Maximum map-space distance used to associate an open or closed mapper door with nearby navigation polygons.
    /// </summary>
    [DataField(required: true)]
    public float DoorDetectionRadius { get; private set; }
}
