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
