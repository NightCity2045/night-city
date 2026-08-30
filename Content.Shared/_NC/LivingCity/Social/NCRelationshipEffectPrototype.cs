// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Social;

/// <summary>
/// Data-driven social consequence of one authoritative world event as perceived by one observer.
/// </summary>
[Prototype("ncRelationshipEffect")]
public sealed partial class NCRelationshipEffectPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField]
    public int TrustDelta { get; private set; }

    [DataField]
    public int FearDelta { get; private set; }

    [DataField]
    public int RespectDelta { get; private set; }

    [DataField]
    public int AffectionDelta { get; private set; }

    [DataField]
    public int HostilityDelta { get; private set; }

    [DataField]
    public long DebtDelta { get; private set; }

    [DataField]
    public int ObligationDelta { get; private set; }
}

/// <summary>
/// Global safety and gameplay bounds for directed relationship aggregates.
/// </summary>
[Prototype("ncRelationshipPolicy")]
public sealed partial class NCRelationshipPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField]
    public int MaximumScore { get; private set; } = 1_000;

    [DataField]
    public long MaximumAbsoluteDebt { get; private set; } = 1_000_000;

    [DataField]
    public int MaximumEffectMagnitude { get; private set; } = 10;

    [DataField]
    public int MaximumSingleEffectDelta { get; private set; } = 1_000;

    [DataField]
    public int MaximumRelationshipsPerObserver { get; private set; } = 512;

    [DataField]
    public int MaximumQueryCount { get; private set; } = 100;
}
