// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Navigation;

/// <summary>Bounds the station fallback independently from map size and NPC population.</summary>
[Prototype("ncStationSemanticDestinationPolicy")]
public sealed partial class NCStationSemanticDestinationPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public int MaximumDestinations { get; private set; } = 24;

    [DataField]
    public int MaximumWorkplaces { get; private set; } = 12;

    [DataField]
    public int MaximumDepartments { get; private set; } = 8;

    [DataField]
    public int MaximumLandmarks { get; private set; } = 8;

    [DataField]
    public TimeSpan HandleLifetime { get; private set; } = TimeSpan.FromMinutes(15);

    [DataField]
    public int MaximumRetainedScopes { get; private set; } = 2;
}
