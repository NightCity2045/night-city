// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Operational bounds and time scaling for unmaterialized actor movement through the semantic CityGraph.
/// </summary>
[Prototype("ncCityAbstractTravelPolicy")]
public sealed partial class NCCityAbstractTravelPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public int SnapshotPageSize { get; private set; } = 250;

    [DataField]
    public int MaximumDispatchesPerTick { get; private set; } = 4;

    [DataField]
    public int MaximumConcurrentMutations { get; private set; } = 8;

    [DataField]
    public TimeSpan RetryDelay { get; private set; } = TimeSpan.FromSeconds(10);

    [DataField]
    public TimeSpan MinimumTravelDuration { get; private set; } = TimeSpan.FromSeconds(1);

    [DataField]
    public TimeSpan MaximumTravelDuration { get; private set; } = TimeSpan.FromHours(6);

    [DataField]
    public float DurationMultiplier { get; private set; } = 1f;
}
