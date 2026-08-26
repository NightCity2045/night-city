// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Social;

public enum NCSocialContextPurpose : byte
{
    InternalReasoning,
    ConversationRendering,
    PublicRendering,
}

public sealed record NCRedactedSocialFact(
    string Domain,
    string FactKey,
    string SubjectKey,
    float Confidence,
    NCMemoryPrivacy Privacy);

/// <summary>
/// Neural-safe structured context. It contains no prose, account IDs, command payloads, EntityUid, or hidden facts
/// above the effective disclosure ceiling.
/// </summary>
public sealed record NCRedactedSocialContext(
    NCPartyRef Owner,
    NCSocialContextPurpose Purpose,
    NCMemoryPrivacy EffectiveMaximumPrivacy,
    IReadOnlyList<NCRedactedSocialFact> Facts,
    IReadOnlyList<string> ForbiddenTopics,
    IReadOnlyList<string> ForbiddenAssertionKeys,
    int RedactedMemoryCount,
    int RedactedRumorCount,
    int RedactedObligationCount);

[Prototype("ncSocialContextPolicy")]
public sealed partial class NCSocialContextPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int MaximumItemsPerDomain { get; private set; } = 24;
    [DataField] public int MaximumForbiddenTopics { get; private set; } = 32;
    [DataField] public NCMemoryPrivacy InternalMaximumPrivacy { get; private set; } = NCMemoryPrivacy.Secret;
    [DataField] public NCMemoryPrivacy ConversationMaximumPrivacy { get; private set; } = NCMemoryPrivacy.Restricted;
    [DataField] public NCMemoryPrivacy PublicMaximumPrivacy { get; private set; } = NCMemoryPrivacy.Public;
    [DataField] public List<string> ForbiddenAssertionKeys { get; private set; } = [];
}
