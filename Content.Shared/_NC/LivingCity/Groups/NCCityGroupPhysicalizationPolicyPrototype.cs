// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Groups;

/// <summary>
/// Operational bounds for coordinated persistent-group physicalization. Formation geometry is runtime-only.
/// </summary>
[Prototype("ncCityGroupPhysicalizationPolicy")]
public sealed partial class NCCityGroupPhysicalizationPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public int CatalogPageSize { get; private set; } = 100;

    [DataField]
    public int MaximumQueuedRequests { get; private set; } = 128;

    [DataField]
    public int MaximumDispatchesPerTick { get; private set; } = 1;

    [DataField]
    public int MaximumConcurrentLoads { get; private set; } = 2;

    [DataField]
    public int MaximumActiveRequestPollsPerTick { get; private set; } = 8;

    [DataField]
    public int MaximumMembersPerRequest { get; private set; } = 32;

    [DataField]
    public int MaximumCompletedRequestHistory { get; private set; } = 512;

    [DataField]
    public int MaximumPriority { get; private set; } = 1000;

    [DataField]
    public TimeSpan RetryDelay { get; private set; } = TimeSpan.FromSeconds(2);

    [DataField]
    public TimeSpan DefaultMaximumWait { get; private set; } = TimeSpan.FromMinutes(2);

    [DataField]
    public TimeSpan MaximumWait { get; private set; } = TimeSpan.FromMinutes(5);

    [DataField]
    public TimeSpan RollbackMaximumWait { get; private set; } = TimeSpan.FromMinutes(2);

    [DataField]
    public float LooseSpacing { get; private set; } = 2.25f;

    [DataField]
    public float ColumnSpacing { get; private set; } = 1.5f;

    [DataField]
    public float WedgeSpacing { get; private set; } = 1.75f;

    [DataField]
    public float DefensiveSpacing { get; private set; } = 2f;
}
