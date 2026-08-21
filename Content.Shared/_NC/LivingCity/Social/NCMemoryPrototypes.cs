// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Social;

[Prototype("ncMemoryEmotion")]
public sealed partial class NCMemoryEmotionPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public string StableKey { get; private set; } = string.Empty;
}

[Prototype("ncMemoryRetention")]
public sealed partial class NCMemoryRetentionPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public string StableKey { get; private set; } = string.Empty;

    [DataField(required: true)]
    public NCMemoryRetentionClass Class { get; private set; }

    [DataField]
    public TimeSpan Lifetime { get; private set; }

    [DataField]
    public bool CanConsolidate { get; private set; }
}

[Prototype("ncMemoryEvent")]
public sealed partial class NCMemoryEventPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public string StableKey { get; private set; } = string.Empty;

    [DataField(required: true)]
    public string ConsolidationKey { get; private set; } = string.Empty;

    [DataField(required: true)]
    public ProtoId<NCMemoryRetentionPrototype> Retention { get; private set; }

    [DataField]
    public List<ProtoId<NCMemoryEmotionPrototype>> EmotionalTags { get; private set; } = [];

    [DataField]
    public int DefaultSalience { get; private set; } = 500;

    [DataField]
    public int DefaultConfidence { get; private set; } = 1_000;

    [DataField]
    public NCMemoryPrivacy DefaultPrivacy { get; private set; } = NCMemoryPrivacy.Personal;

    [DataField]
    public bool Critical { get; private set; }
}

[Prototype("ncMemoryPolicy")]
public sealed partial class NCMemoryPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField]
    public int MaximumEpisodesPerOwner { get; private set; } = 256;

    [DataField]
    public int TargetEpisodesAfterConsolidation { get; private set; } = 192;

    [DataField]
    public int MaximumSummariesPerOwner { get; private set; } = 64;

    [DataField]
    public int MaximumSourceMemoriesPerSummary { get; private set; } = 32;

    [DataField]
    public int MaximumSourceEventsPerEpisode { get; private set; } = 8;

    [DataField]
    public int MaximumSourceEventsPerSummary { get; private set; } = 256;

    [DataField]
    public int MaximumParticipantsPerEpisode { get; private set; } = 16;

    [DataField]
    public int MaximumParticipantsPerSummary { get; private set; } = 64;

    [DataField]
    public int MaximumEmotionalTagsPerEpisode { get; private set; } = 8;

    [DataField]
    public int MaximumEmotionalTagsPerSummary { get; private set; } = 32;

    [DataField]
    public int MinimumConsolidationGroupSize { get; private set; } = 2;

    [DataField]
    public TimeSpan SummaryLifetime { get; private set; } = TimeSpan.FromHours(2_160);

    [DataField]
    public int MaximumQueryCount { get; private set; } = 50;

    [DataField]
    public int ParticipantMatchBonus { get; private set; } = 2_000;

    [DataField]
    public int EventTypeMatchBonus { get; private set; } = 1_500;

    [DataField]
    public int EmotionalTagMatchBonus { get; private set; } = 1_000;

    [DataField]
    public int SalienceWeight { get; private set; } = 4;

    [DataField]
    public int ConfidenceWeight { get; private set; } = 2;

    [DataField]
    public int RecentMemoryBonus { get; private set; } = 1_000;

    [DataField]
    public TimeSpan RecentMemoryWindow { get; private set; } = TimeSpan.FromHours(6);
}
