// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using NUnit.Framework;

namespace Content.Meta.Tests.Commerce;

[TestFixture]
public sealed class MetaQuotePoolTests
{
    private const long Second = TimeSpan.TicksPerSecond;
    [Test]
    public void OwnerCapacityReturnsAfterExpiryWithoutGrowingCurrentSlotsOrRefreshingOldIdentifiers()
    {
        var pool = MetaQuotePoolPolicy.Empty;
        for (var generation = 0; generation < 100; generation++)
        {
            var now = generation * 121 * Second;
            for (var index = 0; index < MetaQuotePoolPolicy.BuyerSlots; index++)
            {
                var result = MetaQuotePoolPolicy.Reserve(pool, "buyer", Guid.NewGuid(), now, now + 120 * Second);
                Assert.That(result, Is.Not.Null);
                pool = result!.Next;
                Assert.That(result.Slot, Is.LessThan(MetaQuotePoolPolicy.BuyerSlots));
            }
            Assert.That(MetaQuotePoolPolicy.Reserve(pool, "buyer", Guid.NewGuid(), now, now + 120 * Second), Is.Null);
            Assert.That(pool.Leases.Length, Is.EqualTo(MetaQuotePoolPolicy.BuyerSlots));
        }
        Assert.That(pool.Revision, Is.EqualTo(800));
    }
    [Test]
    public void ConsumingAQuoteFreesCapacityAndOnlyItsExactBuyerAndIdentifierCanReleaseIt()
    {
        var quote = Guid.NewGuid();
        var pool = MetaQuotePoolPolicy.Reserve(MetaQuotePoolPolicy.Empty, "buyer", quote, 0, 120 * Second)!.Next;
        Assert.That(MetaQuotePoolPolicy.Release(pool, "other", quote, 1), Is.Null);
        Assert.That(MetaQuotePoolPolicy.Release(pool, "buyer", Guid.NewGuid(), 1), Is.Null);
        var consumed = MetaQuotePoolPolicy.Release(pool, "buyer", quote, 1)!;
        Assert.That(consumed.Leases, Is.Empty);
        var next = MetaQuotePoolPolicy.Reserve(consumed, "other", Guid.NewGuid(), 1, 120 * Second)!;
        Assert.That(next.Slot, Is.EqualTo(0));
        Assert.That(next.Next.Revision, Is.EqualTo(3));
        Assert.That(pool.Leases.Single().QuoteId, Is.EqualTo(quote), "Preparing a CAS never mutates the earlier snapshot.");
    }
    [Test]
    public void GlobalAdmissionBurstRefillsAtFourPerSecondAndDoesNotSpendCreditOnDenial()
    {
        var pool = MetaQuotePoolPolicy.Empty;
        for (var index = 0; index < MetaQuotePoolPolicy.Burst; index++)
            pool = MetaQuotePoolPolicy.Reserve(pool, "buyer" + index, Guid.NewGuid(), 0, 120 * Second)!.Next;
        Assert.That(pool.CreditTicks, Is.Zero);
        Assert.That(MetaQuotePoolPolicy.Reserve(pool, "new", Guid.NewGuid(), 0, 120 * Second), Is.Null);
        for (var index = 0; index < 4; index++) pool = MetaQuotePoolPolicy.Reserve(pool, "new" + index, Guid.NewGuid(), Second, 120 * Second)!.Next;
        Assert.That(MetaQuotePoolPolicy.Reserve(pool, "new", Guid.NewGuid(), Second, 120 * Second), Is.Null);
        Assert.That(MetaQuotePoolPolicy.Reserve(pool, "new", Guid.NewGuid(), Second - 1, 120 * Second), Is.Null);
    }
    [Test]
    public void FullGlobalPoolIsBoundedAndAnExpiredGenerationBecomesReusable()
    {
        var pool = new MetaQuotePool(100, 0, 32 * Second, Enumerable.Range(0, MetaQuotePoolPolicy.Slots)
            .Select(index => new MetaQuoteLease(index, Guid.NewGuid(), "buyer" + index / 8, 120 * Second)).ToArray());
        Assert.That(MetaQuotePoolPolicy.Valid(pool), Is.True);
        Assert.That(MetaQuotePoolPolicy.Reserve(pool, "new", Guid.NewGuid(), 0, Second), Is.Null);
        var next = MetaQuotePoolPolicy.Reserve(pool, "new", Guid.NewGuid(), 120 * Second, 121 * Second)!;
        Assert.That(next.Slot, Is.Zero);
        Assert.That(next.Next.Leases.Length, Is.EqualTo(1));
    }
    [Test]
    public void ARepeatedIdentifierCannotCreateAnotherLeaseAndParallelPreparationsShareTheSameCasRevision()
    {
        var id = Guid.NewGuid(); var original = MetaQuotePoolPolicy.Empty;
        var first = MetaQuotePoolPolicy.Reserve(original, "buyer", id, 0, 120 * Second)!;
        var concurrent = MetaQuotePoolPolicy.Reserve(original, "other", Guid.NewGuid(), 0, 120 * Second)!;
        Assert.That(first.Next.Revision, Is.EqualTo(concurrent.Next.Revision));
        Assert.That(first.Slot, Is.EqualTo(concurrent.Slot));
        Assert.That(MetaQuotePoolPolicy.Reserve(first.Next, "buyer", id, 1, 120 * Second), Is.Null);
    }
    [Test]
    public void InvalidClockLifetimeDuplicateSlotsAndVersionOverflowFailClosed()
    {
        var id = Guid.NewGuid();
        Assert.That(MetaQuotePoolPolicy.Reserve(MetaQuotePoolPolicy.Empty, "buyer", id, -1, Second), Is.Null);
        Assert.That(MetaQuotePoolPolicy.Reserve(MetaQuotePoolPolicy.Empty, "buyer", id, 0, 121 * Second), Is.Null);
        var lease = new MetaQuoteLease(0, id, "buyer", Second);
        Assert.That(MetaQuotePoolPolicy.Valid(new(1, 0, 0, [lease, lease])), Is.False);
        Assert.That(MetaQuotePoolPolicy.Reserve(MetaQuotePoolPolicy.Empty with { Revision = long.MaxValue - 1 }, "buyer", id, 0, Second), Is.Null);
    }
}
