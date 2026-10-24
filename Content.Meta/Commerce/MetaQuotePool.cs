// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Commerce;

public sealed record MetaQuoteLease(int Slot, Guid QuoteId, string Buyer, long ExpiresAtTicks);
public sealed record MetaQuotePool(long Revision, long LastTicks, long CreditTicks, MetaQuoteLease[] Leases);
public sealed record MetaQuoteAllocation(int Slot, MetaQuotePool Next);

/// <summary>Fixed reusable current-state capacity. Immutable acknowledgements belong to foundation command receipts.</summary>
public static class MetaQuotePoolPolicy
{
    public const int Slots = 1024;
    public const int BuyerSlots = 8;
    public const int Burst = 32;
    public const int PerSecond = 4;
    public const long MaximumLifetimeTicks = 2 * TimeSpan.TicksPerMinute;
    private const long MaximumCredit = Burst * TimeSpan.TicksPerSecond;
    public static MetaQuotePool Empty => new(0, 0, MaximumCredit, []);
    public static string SlotKey(int slot) => slot is >= 0 and < Slots ? "slot:" + slot.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) :
        throw new ArgumentOutOfRangeException(nameof(slot));
    public static bool Valid(MetaQuotePool pool) => pool.Revision is >= 0 and < long.MaxValue && pool.LastTicks >= 0 &&
        pool.CreditTicks is >= 0 and <= MaximumCredit && pool.Leases is { Length: <= Slots } && pool.Leases.All(lease =>
            lease != null && lease.Slot is >= 0 and < Slots && lease.QuoteId != Guid.Empty && lease.Buyer is { Length: > 0 and <= 128 } &&
            lease.ExpiresAtTicks > 0 && lease.ExpiresAtTicks - Math.Min(lease.ExpiresAtTicks, pool.LastTicks) <= MaximumLifetimeTicks) &&
        pool.Leases.Select(lease => lease.Slot).Distinct().Count() == pool.Leases.Length &&
        pool.Leases.Select(lease => lease.QuoteId).Distinct().Count() == pool.Leases.Length;

    public static MetaQuoteAllocation? Reserve(MetaQuotePool pool, string buyer, Guid quote, long now, long expires)
    {
        if (!Valid(pool) || pool.Revision >= long.MaxValue - 1 || buyer is not { Length: > 0 and <= 128 } || quote == Guid.Empty || now < pool.LastTicks ||
            expires <= now || expires - now > MaximumLifetimeTicks || pool.Leases.Any(lease => lease.QuoteId == quote)) return null;
        var live = pool.Leases.Where(lease => lease.ExpiresAtTicks > now).ToArray();
        if (live.Length == Slots || live.Count(lease => lease.Buyer == buyer) >= BuyerSlots) return null;
        var elapsed = Math.Min(now - pool.LastTicks, MaximumCredit / PerSecond);
        var credit = Math.Min(MaximumCredit, pool.CreditTicks + elapsed * PerSecond);
        if (credit < TimeSpan.TicksPerSecond) return null;
        var occupied = live.Select(lease => lease.Slot).ToHashSet();
        var slot = Enumerable.Range(0, Slots).First(value => !occupied.Contains(value));
        return new(slot, new(pool.Revision + 1, now, credit - TimeSpan.TicksPerSecond,
            [..live, new(slot, quote, buyer, expires)]));
    }

    public static MetaQuotePool? Release(MetaQuotePool pool, string buyer, Guid quote, long now)
    {
        if (!Valid(pool) || pool.Revision >= long.MaxValue - 1 || now < pool.LastTicks || !pool.Leases.Any(lease => lease.QuoteId == quote && lease.Buyer == buyer)) return null;
        return pool with { Revision = pool.Revision + 1, Leases = pool.Leases.Where(lease => lease.QuoteId != quote).ToArray() };
    }
}
