// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Social;

[Prototype("ncRumorTopic")]
public sealed partial class NCRumorTopicPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField] public int DefaultConfidence { get; private set; } = 800;
    [DataField] public NCMemoryPrivacy DefaultPrivacy { get; private set; } = NCMemoryPrivacy.Personal;
    [DataField] public TimeSpan Lifetime { get; private set; } = TimeSpan.FromHours(72);
    [DataField] public bool Critical { get; private set; }
}

[Prototype("ncRumorPolicy")]
public sealed partial class NCRumorPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int MaximumKnowledgePerOwner { get; private set; } = 128;
    [DataField] public int TargetKnowledgeAfterPrune { get; private set; } = 96;
    [DataField] public int MaximumSourceEvents { get; private set; } = 8;
    [DataField] public int MaximumQueryCount { get; private set; } = 32;
    [DataField] public int MaximumHopCount { get; private set; } = 8;
    [DataField] public int ConfidenceRetentionPermille { get; private set; } = 850;
    [DataField] public int DistortionPerHop { get; private set; } = 100;
    [DataField] public int MinimumPropagationConfidence { get; private set; } = 100;
}

