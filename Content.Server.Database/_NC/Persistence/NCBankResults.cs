namespace Content.Server.Database;

public sealed record NCBankMutationResult(
    bool Success,
    string? Error,
    long DebitBalance,
    long CreditBalance);
