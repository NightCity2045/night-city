// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Threading;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Time;
using NUnit.Framework;

namespace Content.Tests.Server._NC.Meta;

[TestFixture]
public sealed class NCMetaJournalStreamTest
{
    private static readonly NCSessionEpoch Epoch = NCSessionEpoch.New();
    private static NCAggregateSnapshotData State(long version, long? ticks = null) =>
        new("meta.volume", "hot-volume", version, 1, Epoch, new(ticks ?? version), "{\"text\":\"история 😀\"}", DateTime.UnixEpoch);
    private static NCPersistentEventData Event(NCAggregateSnapshotData current, NCAggregateSnapshotData previous = null) =>
        new(Guid.NewGuid(), "meta.volume.changed", current.AggregateType, current.AggregateId, current.AggregateVersion,
            Guid.NewGuid(), null, current.SessionEpoch, current.ActiveWorldTime,
            NCMetaJournalIntegrity.AttestSnapshot(current.AggregateType, current.AggregateId, current.AggregateVersion,
                current.FormatVersion, current.SessionEpoch.Value, current.ActiveWorldTime.Ticks, current.Payload, "{}", previous), DateTime.UnixEpoch);

    private static NCPersistentEventData[] History(int count)
    {
        var result = new NCPersistentEventData[count];
        NCAggregateSnapshotData previous = null;
        for (var version = 1; version <= count; version++)
        {
            var current = State(version);
            result[count - version] = Event(current, previous);
            previous = current;
        }
        return result;
    }

    [TestCase(1)]
    [TestCase(127)]
    [TestCase(128)]
    public void ColdConfirmedHistoryHasNo4096LifetimeLimitAndEveryPageRemainsBounded(int pageSize)
    {
        const int count = 5003;
        var history = History(count);
        Assert.That(NCMetaJournalIntegrity.CanValidateTail(State(count), null, history), Is.False, "Legacy bounded audit API retains its contract.");
        var stream = NCMetaJournalIntegrity.TailVerifier.Create(State(count), null);
        Assert.That(stream, Is.Not.Null);
        foreach (var page in history.Chunk(pageSize)) Assert.That(stream!.ConsumePage(page), Is.True);
        Assert.That(stream!.Complete, Is.True);
        Assert.That(stream.Remaining, Is.Zero);
    }

    [TestCase("gap")]
    [TestCase("order")]
    [TestCase("payload")]
    [TestCase("clock")]
    [TestCase("epoch")]
    [TestCase("identity")]
    public void ACorruptMiddlePageNeverBecomesAuthorityOrRecoversBySkippingIt(string fault)
    {
        var history = History(5003);
        var stream = NCMetaJournalIntegrity.TailVerifier.Create(State(5003), State(1));
        var middle = 2048;
        switch (fault)
        {
            case "gap": history = history.Where((_, index) => index != middle).ToArray(); break;
            case "order": (history[middle], history[middle + 1]) = (history[middle + 1], history[middle]); break;
            case "payload": history[middle] = history[middle] with { Payload = "{}" }; break;
            case "clock": history[middle] = history[middle] with { ActiveWorldTime = new(-1) }; break;
            case "epoch": history[middle] = history[middle] with { SessionEpoch = NCSessionEpoch.New() }; break;
            case "identity": history[middle] = history[middle] with { AggregateId = "other-volume" }; break;
        }
        var rejected = false;
        foreach (var page in history.Take(history.Length - 1).Chunk(128))
            if (!stream!.ConsumePage(page)) { rejected = true; break; }
        Assert.That(rejected, Is.True);
        Assert.That(stream!.Complete, Is.False);
        Assert.That(stream.ConsumePage([history[0]]), Is.False, "A failed chain cannot be resumed by omitting its bad page.");
    }

    [Test]
    public void ExactBaselineLegacyAndMalformedJsonCannotBypassTheBoundary()
    {
        var same = State(4);
        Assert.That(NCMetaJournalIntegrity.TailVerifier.Create(same, same)!.Complete, Is.True);
        Assert.That(NCMetaJournalIntegrity.TailVerifier.Create(same with { Payload = "{}" }, same)!.Complete, Is.False);
        Assert.That(NCMetaJournalIntegrity.TailVerifier.Create(State(3), same), Is.Null, "Confirmed newer versions cannot regress, including on normal code rollback.");
        var next = State(5);
        var malformed = Event(next, same) with { Payload = "{\"_ncMetaSnapshot\": [" };
        Assert.That(NCMetaJournalIntegrity.TailVerifier.Create(next, same)!.ConsumePage([malformed]), Is.False);
        Assert.That(NCMetaJournalIntegrity.TailVerifier.Create(next, same)!.ConsumePage([]), Is.False);
        Assert.That(NCMetaJournalIntegrity.TailVerifier.Create(next, same)!.ConsumePage(Enumerable.Repeat(malformed, 129).ToArray()), Is.False);
    }

    [Test]
    public void AttestedCapturedTimeSemanticsArePreservedAndTamperedClockIsRejected()
    {
        var previous = State(1, 100); var current = State(2, 90);
        var entry = Event(current, previous);
        // A delayed captured context is legal under the current native DB policy; do not invent another writer constraint here.
        var valid = NCMetaJournalIntegrity.TailVerifier.Create(current, previous)!;
        Assert.That(valid.ConsumePage([entry]), Is.True);
        Assert.That(valid.Complete, Is.True);
        Assert.That(NCMetaJournalIntegrity.TailVerifier.Create(current, previous)!.ConsumePage([entry with { ActiveWorldTime = new(89) }]), Is.False);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => NCMetaJournalIntegrity.TailVerifier.Create(current, previous)!.ConsumePage([entry], cancelled.Token));
    }
}
