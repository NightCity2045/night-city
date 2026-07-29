// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
namespace Content.Server.Database;

public sealed record NCSkillSpendResult(
    bool Success,
    string? Error,
    int NewRank,
    int SpentSkillPoints,
    int TotalSkillPoints);

public sealed record NCParticipationResult(
    int ActiveSeconds,
    bool Counted,
    bool NewlyCounted,
    int CompletedRounds,
    byte Level,
    bool LeveledUp);
