// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Persistence.Time;
using NUnit.Framework;

namespace Content.Tests.Server._NC.Meta;

[TestFixture]
public sealed class NCMetaSnapshotIntegrityTest
{
    private static readonly NCSessionEpoch Epoch = NCSessionEpoch.New();
    private static NCAggregateSnapshotData State(long version, string payload = "{\"text\":\"проверка 😀\"}") =>
        new("meta.project", "project-a", version, 1, Epoch, new(version * 10), payload, DateTime.UnixEpoch);

    private static NCPersistentEventData Event(NCAggregateSnapshotData current, NCAggregateSnapshotData previous = null)
    {
        return new(Guid.NewGuid(), "meta.project.changed", current.AggregateType, current.AggregateId, current.AggregateVersion,
            Guid.NewGuid(), null, current.SessionEpoch, current.ActiveWorldTime,
            NCMetaJournalIntegrity.AttestSnapshot(current.AggregateType, current.AggregateId, current.AggregateVersion,
                current.FormatVersion, current.SessionEpoch.Value, current.ActiveWorldTime.Ticks, current.Payload,
                "{\"business\":\"retained\",\"_ncMetaSnapshot\":\"forged\"}", previous), DateTime.UnixEpoch);
    }

    [Test]
    public void ContiguousTailAttestsExactUnicodeContentAndPreservesBusinessPayload()
    {
        var first = State(1); var second = State(2, "{\"value\":2}"); var third = State(3, "{\"value\":3}");
        var events = new[] { Event(third, second), Event(second, first), Event(first) };
        Assert.That(NCMetaJournalIntegrity.CanValidateTail(third, null, events), Is.True);
        Assert.That(NCMetaJournalIntegrity.CanValidateTail(third, first, events.Take(2).ToArray()), Is.True);
        using var payload = JsonDocument.Parse(events[0].Payload);
        Assert.That(payload.RootElement.GetProperty("business").GetString(), Is.EqualTo("retained"));
        Assert.That(payload.RootElement.GetProperty(NCMetaJournalIntegrity.Field).ValueKind, Is.EqualTo(JsonValueKind.Object));
        Assert.That(NCMetaJournalIntegrity.AttestSnapshot("test.normal", "id", 1, 1, Epoch.Value, 0, "{}", "business", null), Is.EqualTo("business"));
    }

    [Test]
    public void MissingMiddleEventAndTamperedLiveContentOrEventIdentityFailClosed()
    {
        var first = State(1); var second = State(2); var third = State(3);
        var newest = Event(third, second); var middle = Event(second, first);
        Assert.Multiple(() =>
        {
            Assert.That(NCMetaJournalIntegrity.CanValidateTail(third, first, [newest]), Is.False);
            Assert.That(NCMetaJournalIntegrity.CanValidateTail(third with { Payload = "{\"injected\":true}" }, first, [newest, middle]), Is.False);
            Assert.That(NCMetaJournalIntegrity.CanValidateTail(third, first, [newest with { SessionEpoch = NCSessionEpoch.New() }, middle]), Is.False);
            Assert.That(NCMetaJournalIntegrity.CanValidateTail(third, first, [newest with { AggregateId = "other" }, middle]), Is.False);
            Assert.That(NCMetaJournalIntegrity.CanValidateTail(third, first, [middle, newest]), Is.False);
        });
    }

    [Test]
    public void LegacyStateRequiresExactImmutableAnchorAndNeverAuthorizesAnUnattestedTail()
    {
        var first = State(1); var second = State(2);
        Assert.Multiple(() =>
        {
            Assert.That(NCMetaJournalIntegrity.CanValidateTail(first, first, []), Is.True);
            Assert.That(NCMetaJournalIntegrity.CanValidateTail(first with { Payload = "{}" }, first, []), Is.False);
            Assert.That(NCMetaJournalIntegrity.CanValidateTail(first with { FormatVersion = 2 }, first, []), Is.False);
            Assert.That(NCMetaJournalIntegrity.CanValidateTail(second, first, [Event(second, first) with { Payload = "{}" }]), Is.False);
            Assert.That(NCMetaJournalIntegrity.CanValidateTail(State(NCMetaJournalIntegrity.MaximumTailEvents + 1), null, []), Is.False);
        });
    }

    [Test]
    public void ExplicitMetaDescriptorRejectsTruncatedReorderedAndAlteredChunks()
    {
        var id = Guid.NewGuid();
        var payloads = new[] { (Payload: JsonSerializer.Serialize(new[] { State(1) }), Count: 1),
            (Payload: JsonSerializer.Serialize(new[] { State(2) with { AggregateId = "project-b" } }), Count: 1) };
        var descriptor = NCMetaCheckpointIntegrity.Describe(payloads);
        var sections = payloads.Select((value, index) => new NCRoundSnapshotSectionData(id,
            NCMetaCheckpointIntegrity.SectionType, "world", "city", index, 2, value.Count, value.Payload,
            NCMetaJournalIntegrity.HashPayload(value.Payload))).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(NCMetaCheckpointIntegrity.CanValidate(descriptor, sections), Is.True);
            Assert.That(NCMetaCheckpointIntegrity.CanValidate(descriptor, sections.Take(1).ToArray()), Is.False);
            Assert.That(NCMetaCheckpointIntegrity.CanValidate(descriptor, sections.Reverse().ToArray()), Is.False);
            Assert.That(NCMetaCheckpointIntegrity.CanValidate(descriptor, [sections[0], sections[1] with { Payload = "[]" }]), Is.False);
            Assert.That(NCMetaCheckpointIntegrity.CanValidate(descriptor with { Records = 1 }, sections), Is.False);
        });
    }

    [Test]
    public void SelectedManifestInventoryAuthenticatesEverySubsystemAndItsTotalBytes()
    {
        var sections = new[]
        {
            new NCRoundSnapshotInventorySection("meta-aggregates", "world", "city", 0, 2, 1, 50, new string('a', 64)),
            new NCRoundSnapshotInventorySection("city-map-delta", "world", "city", 0, 1, 10, 100, new string('b', 64)),
        };
        var manifest = new NCRoundSnapshotManifestData(Guid.NewGuid(), Epoch.Value, 50, 1, "test", 0, "", 2, 150,
            NCRoundSnapshotInventoryIntegrity.Checksum(sections), DateTime.UnixEpoch, DateTime.UnixEpoch);
        var inventory = new NCRoundSnapshotInventory(manifest, sections);
        Assert.Multiple(() =>
        {
            Assert.That(NCRoundSnapshotInventoryIntegrity.CanValidate(inventory, manifest), Is.True);
            Assert.That(NCRoundSnapshotInventoryIntegrity.CanValidate(inventory with { Sections = sections.Take(1).ToArray() }, manifest), Is.False);
            Assert.That(NCRoundSnapshotInventoryIntegrity.CanValidate(inventory with { Sections = [sections[0], sections[1] with { EntityCount = 9 }] }, manifest), Is.False);
            Assert.That(NCRoundSnapshotInventoryIntegrity.CanValidate(inventory with { Sections = [sections[0], sections[1] with { PayloadBytes = 99 }] }, manifest), Is.False);
            Assert.That(NCRoundSnapshotInventoryIntegrity.CanValidate(inventory, manifest with { SnapshotId = Guid.NewGuid() }), Is.False);
        });
    }
}
