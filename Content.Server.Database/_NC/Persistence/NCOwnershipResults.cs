using System;

namespace Content.Server.Database;

public sealed record NCOwnershipResult(
    bool Success,
    string? Error,
    Guid? EntityId);
