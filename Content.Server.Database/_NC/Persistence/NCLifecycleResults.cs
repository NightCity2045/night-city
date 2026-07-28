namespace Content.Server.Database;

public sealed record NCLifecycleResult(
    bool Success,
    string? Error,
    int FinalizedProfiles = 0);
