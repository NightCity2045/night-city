// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Social;

/// <summary>
/// Stable professional or public audience. StableKey survives a prototype rename without creating new reputation.
/// </summary>
[Prototype("ncReputationDomain")]
public sealed partial class NCReputationDomainPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public string StableKey { get; private set; } = string.Empty;

    [DataField(required: true)]
    public NCReputationChannel Channel { get; private set; }
}

/// <summary>
/// Data-driven change produced by an authoritative world event.
/// </summary>
[Prototype("ncReputationEffect")]
public sealed partial class NCReputationEffectPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public List<NCReputationChannel> AllowedChannels { get; private set; } = [];

    [DataField]
    public int ScoreDelta { get; private set; }

    [DataField]
    public int VisibilityDelta { get; private set; }

    [DataField]
    public int EvidenceDelta { get; private set; }
}

/// <summary>
/// Global safety and storage bounds for subject reputation aggregates.
/// </summary>
[Prototype("ncReputationPolicy")]
public sealed partial class NCReputationPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField]
    public int MaximumScore { get; private set; } = 1_000;

    [DataField]
    public int MaximumVisibility { get; private set; } = 1_000;

    [DataField]
    public int MaximumEvidence { get; private set; } = 1_000_000;

    [DataField]
    public int MaximumEffectMagnitude { get; private set; } = 10;

    [DataField]
    public int MaximumSingleEffectDelta { get; private set; } = 1_000;

    [DataField]
    public int MaximumEntriesPerSubject { get; private set; } = 512;

    [DataField]
    public int MaximumQueryCount { get; private set; } = 100;
}
