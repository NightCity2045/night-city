// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Persistence.Time;

/// <summary>
/// Identifies one authoritative round/session epoch so stale asynchronous work cannot cross a restart.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCSessionEpoch(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;

    public static NCSessionEpoch New()
    {
        return new NCSessionEpoch(Guid.NewGuid());
    }

    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>
/// Monotonic gameplay time accumulated only while a round is active.
/// It deliberately excludes real time elapsed while the server or round is stopped.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCActiveWorldTime(long Ticks) : IComparable<NCActiveWorldTime>
{
    public static NCActiveWorldTime Zero => new(0);

    public bool IsValid => Ticks >= 0;
    public TimeSpan Elapsed => TimeSpan.FromTicks(Ticks);

    public NCActiveWorldTime Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Active world time cannot move backwards.");

        return new NCActiveWorldTime(checked(Ticks + elapsed.Ticks));
    }

    public TimeSpan ElapsedSince(NCActiveWorldTime earlier)
    {
        if (!IsValid || !earlier.IsValid || earlier.Ticks > Ticks)
            throw new ArgumentOutOfRangeException(nameof(earlier), "The earlier world time must be valid and not later than this value.");

        return TimeSpan.FromTicks(Ticks - earlier.Ticks);
    }

    /// <summary>
    /// Safely compares timestamps loaded from round-local or persisted state without turning stale data into a
    /// server-fatal exception. Callers must explicitly decide how to invalidate a future timestamp.
    /// </summary>
    public bool TryElapsedSince(NCActiveWorldTime earlier, out TimeSpan elapsed)
    {
        if (!IsValid || !earlier.IsValid || earlier.Ticks > Ticks)
        {
            elapsed = default;
            return false;
        }

        elapsed = TimeSpan.FromTicks(Ticks - earlier.Ticks);
        return true;
    }

    public int CompareTo(NCActiveWorldTime other)
    {
        return Ticks.CompareTo(other.Ticks);
    }

    public static bool operator <(NCActiveWorldTime left, NCActiveWorldTime right) => left.Ticks < right.Ticks;
    public static bool operator <=(NCActiveWorldTime left, NCActiveWorldTime right) => left.Ticks <= right.Ticks;
    public static bool operator >(NCActiveWorldTime left, NCActiveWorldTime right) => left.Ticks > right.Ticks;
    public static bool operator >=(NCActiveWorldTime left, NCActiveWorldTime right) => left.Ticks >= right.Ticks;
}
