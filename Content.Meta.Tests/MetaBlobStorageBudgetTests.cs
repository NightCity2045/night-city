// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Persistence;
using NUnit.Framework;

namespace Content.Meta.Tests;

[TestFixture]
public sealed class MetaBlobStorageBudgetTests
{
    [Test]
    public void SustainedIngressRemainsBelowBoundedDrainAndAnOutageHitsTheExactCeiling()
    {
        var now = DateTime.UnixEpoch.Ticks;
        var allocation = new MetaBlobStorageAllocation(0, 0, MetaBlobStorageBudget.CountBurst, MetaBlobStorageBudget.ByteBurst, now);
        for (var second = 0; second < 1000; second++)
        {
            now += TimeSpan.TicksPerSecond;
            for (var item = 0; item < MetaBlobStorageBudget.CountPerSecond; item++)
            {
                Assert.That(MetaBlobStorageBudget.TryReserve(allocation, 1024, now, out var reserved), Is.True);
                allocation = reserved;
            }
            Assert.That(allocation.Count, Is.LessThanOrEqualTo(8));
            // The collector has a bounded 32-row work unit and cannot drain live roots.
            var removed = Math.Min(32, allocation.Count);
            allocation = allocation with { Count = allocation.Count - removed, Bytes = allocation.Bytes - removed * 1024 };
        }
        allocation = allocation with { Count = MetaBlobStorageBudget.MaximumCount - 1 };
        Assert.That(MetaBlobStorageBudget.TryReserve(allocation, 1024, now, out var last), Is.True);
        Assert.That(last.Count, Is.EqualTo(MetaBlobStorageBudget.MaximumCount));
        Assert.That(MetaBlobStorageBudget.TryReserve(last, 1, now + TimeSpan.TicksPerDay, out _), Is.False);
        allocation = allocation with { Count = 0, Bytes = MetaBlobStorageBudget.MaximumBytes - 1024 };
        Assert.That(MetaBlobStorageBudget.TryReserve(allocation, 1024, now, out last), Is.True);
        Assert.That(last.Bytes, Is.EqualTo(MetaBlobStorageBudget.MaximumBytes));
        Assert.That(MetaBlobStorageBudget.TryReserve(last, 1, now + TimeSpan.TicksPerDay, out _), Is.False);
    }

    [Test]
    public void BurstRefillClockRollbackAndRestartCannotMintAdmissionTokens()
    {
        var now = DateTime.UnixEpoch.Ticks;
        var allocation = new MetaBlobStorageAllocation(0, 0, MetaBlobStorageBudget.CountBurst, MetaBlobStorageBudget.ByteBurst, now);
        for (var item = 0; item < MetaBlobStorageBudget.CountBurst; item++)
        {
            Assert.That(MetaBlobStorageBudget.TryReserve(allocation, 1, now, out var reserved), Is.True);
            allocation = reserved;
        }
        Assert.That(MetaBlobStorageBudget.TryReserve(allocation, 1, now, out _), Is.False);
        Assert.That(MetaBlobStorageBudget.TryReserve(allocation, 1, now - TimeSpan.TicksPerDay, out _), Is.False);
        Assert.That(MetaBlobStorageBudget.TryReserve(allocation, 1, now + TimeSpan.TicksPerSecond / 8, out var one), Is.True);
        Assert.That(MetaBlobStorageBudget.TryReserve(one, 1, one.RefilledAtUtcTicks, out _), Is.False);
        var serialized = System.Text.Json.JsonSerializer.Serialize(one);
        var restarted = System.Text.Json.JsonSerializer.Deserialize<MetaBlobStorageAllocation>(serialized);
        Assert.That(MetaBlobStorageBudget.TryReserve(restarted, 1, restarted.RefilledAtUtcTicks, out _), Is.False);
    }
}
