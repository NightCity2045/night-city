// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Social;

[Prototype("ncConversationPolicy")]
public sealed partial class NCConversationPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int MaximumActiveSessions { get; private set; } = 512;
    [DataField] public int MaximumRecentSessions { get; private set; } = 256;
    [DataField] public int MaximumSessionsPerParticipant { get; private set; } = 4;
    [DataField] public int MaximumParticipants { get; private set; } = 16;
    [DataField] public int MaximumTopics { get; private set; } = 16;
    [DataField] public int MaximumRelevantFacts { get; private set; } = 32;
    [DataField] public int MaximumTurns { get; private set; } = 64;
    [DataField] public int MaximumUtteranceLength { get; private set; } = 2048;
    [DataField] public int MaximumArchivedSummaryTurns { get; private set; } = 12;
    [DataField] public int MaximumArchivedSummaryLength { get; private set; } = 2048;
    [DataField] public int MaximumCachedArchivesPerPair { get; private set; } = 64;
    [DataField] public List<string> VerbalThreatMarkers { get; private set; } = [];
    [DataField] public TimeSpan VerbalThreatLifetime { get; private set; } = TimeSpan.FromMinutes(2);
    [DataField] public TimeSpan IdleTimeout { get; private set; } = TimeSpan.FromMinutes(3);
    [DataField] public TimeSpan MaximumLifetime { get; private set; } = TimeSpan.FromMinutes(30);
    [DataField] public TimeSpan ExpirySweepInterval { get; private set; } = TimeSpan.FromSeconds(1);
}

