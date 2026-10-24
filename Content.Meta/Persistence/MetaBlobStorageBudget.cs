// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Persistence;

public readonly record struct MetaBlobStorageAllocation(long Count, long Bytes, double CountTokens, double ByteTokens, long RefilledAtUtcTicks);

/// <summary>Physical deduplicated storage budget. Logical owner quotas are enforced separately.</summary>
public static class MetaBlobStorageBudget
{
    public const long MaximumCount = 262144;
    public const long MaximumBytes = 4L * 1024 * 1024 * 1024;
    public const int CountBurst = 128;
    public const int ByteBurst = 4 * 1024 * 1024;
    public const int CountPerSecond = 8;
    public const int BytesPerSecond = 256 * 1024;

    public static bool TryReserve(MetaBlobStorageAllocation previous, int bytes, long nowUtcTicks,
        out MetaBlobStorageAllocation reserved)
    {
        reserved = previous;
        if (bytes < 0 || nowUtcTicks <= 0 || previous.Count < 0 || previous.Bytes < 0 ||
            previous.RefilledAtUtcTicks <= 0 || !double.IsFinite(previous.CountTokens) || !double.IsFinite(previous.ByteTokens) ||
            previous.CountTokens is < 0 or > CountBurst || previous.ByteTokens is < 0 or > ByteBurst)
            throw new InvalidDataException("Invalid META storage admission state.");
        if (previous.Count >= MaximumCount || previous.Bytes > MaximumBytes - bytes) return false;
        var elapsed = nowUtcTicks > previous.RefilledAtUtcTicks
            ? (nowUtcTicks - previous.RefilledAtUtcTicks) / (double) TimeSpan.TicksPerSecond : 0d;
        var countTokens = Math.Min(CountBurst, previous.CountTokens + elapsed * CountPerSecond);
        var byteTokens = Math.Min(ByteBurst, previous.ByteTokens + elapsed * BytesPerSecond);
        if (countTokens < 1 || byteTokens < bytes) return false;
        reserved = new(checked(previous.Count + 1), checked(previous.Bytes + bytes), countTokens - 1,
            byteTokens - bytes, Math.Max(previous.RefilledAtUtcTicks, nowUtcTicks));
        return true;
    }
}
