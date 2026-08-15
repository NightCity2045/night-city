// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using Content.Shared._NC.City.Zones;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.City.Connectivity;

public enum NCCitySpawnCandidateKind : byte
{
    Pedestrian,
    Vehicle,
    Patrol,
    Incident,
}

/// <summary>
/// Data-driven sampling policy for one class of A-Life or incident materialization target.
/// </summary>
[Prototype("ncCitySpawnCandidateProfile")]
public sealed partial class NCCitySpawnCandidateProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public NCCitySpawnCandidateKind Kind { get; private set; }

    [DataField(required: true)]
    public NCCityTravelMode TravelMode { get; private set; }

    [DataField(required: true)]
    public HashSet<NCZoneContextSlot> AllowedZoneContexts { get; private set; } = [];

    [DataField(required: true)]
    public float MinimumSpacing { get; private set; }

    [DataField(required: true)]
    public float MinimumClearance { get; private set; }

    [DataField(required: true)]
    public float PortalExclusionRadius { get; private set; }

    [DataField(required: true)]
    public int MaximumPerRoom { get; private set; }
}

/// <summary>
/// Declares which semantic zone kinds must be materializable and connected in a releasable city layout.
/// Exactly one rules prototype is expected.
/// </summary>
[Prototype("ncCityConnectivityValidation")]
public sealed partial class NCCityConnectivityValidationPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public HashSet<NCZoneContextSlot> RequiresNavigationRoom { get; private set; } = [];

    [DataField(required: true)]
    public HashSet<NCZoneContextSlot> RequiresTraversableExit { get; private set; } = [];

    [DataField(required: true)]
    public HashSet<NCZoneContextSlot> RequiresStreetReachability { get; private set; } = [];

    [DataField]
    public Dictionary<NCZoneContextSlot, List<ProtoId<NCCitySpawnCandidateProfilePrototype>>> RequiredSpawnProfiles { get; private set; } = [];
}
