// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Housing;

/// <summary>Canonical durable payload for one apartment's bounded materialized contents.</summary>
public sealed record NCPropertyContentSnapshot(
    NCApartmentId PropertyId,
    bool Present,
    string TemplateId,
    int TemplateRevision,
    int FormatVersion,
    string Payload,
    int EntityCount,
    int ResidentEntityCount,
    int FixtureEntityCount,
    int IncludedAssetEntityCount,
    int PersonalPropertyEntityCount,
    NCActiveWorldTime CapturedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCPropertyContentRecord(
    NCPropertyContentSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

/// <summary>
/// Stores apartment content independently from tenure. A deletion is a versioned tombstone, so an old payload cannot
/// reappear after a delayed write or restart.
/// </summary>
public sealed partial class NCPropertyContentSnapshotSystem : EntitySystem
{
    public const string AggregateType = "living-city.property-content";
    private const int SnapshotFormatVersion = 1;
    private const int MutationGateStripeCount = 32;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPropertyRegistrySystem _properties = default!;

    private readonly ConcurrentDictionary<NCApartmentId, NCPropertyContentRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();

    public async Task<NCPropertyContentRecord?> GetAsync(NCApartmentId propertyId, bool forceReload = false)
    {
        if (!propertyId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(propertyId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, Key(propertyId));
        var record = Deserialize(stored, propertyId);
        if (record != null)
            _cache[propertyId] = record;
        return record;
    }

    public Task<bool> UpsertAsync(NCPropertyContentSnapshot snapshot, string auditSource)
    {
        return PersistAsync(snapshot, present: true, auditSource);
    }

    public async Task<bool> DeleteAsync(NCApartmentId propertyId, string auditSource)
    {
        var current = await GetAsync(propertyId, true);
        if (current == null || !current.Snapshot.Present)
            return true;

        return await PersistAsync(current.Snapshot, present: false, auditSource);
    }

    private async Task<bool> PersistAsync(
        NCPropertyContentSnapshot submitted,
        bool present,
        string auditSource)
    {
        if (!submitted.PropertyId.IsValid || string.IsNullOrWhiteSpace(auditSource) || auditSource.Length > 128 ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return false;

        var gate = _gates[(submitted.PropertyId.GetHashCode() & int.MaxValue) % _gates.Length];
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(submitted.PropertyId, true);
            var expectedVersion = current?.AggregateVersion ?? 0;
            var eventId = Guid.NewGuid();
            var next = present
                ? submitted with
                {
                    Present = true,
                    CapturedAt = now,
                    DataVersion = checked((current?.Snapshot.DataVersion ?? 0) + 1),
                    LastAppliedEventId = eventId,
                }
                : submitted with
                {
                    Present = false,
                    TemplateId = string.Empty,
                    TemplateRevision = 0,
                    FormatVersion = 0,
                    Payload = string.Empty,
                    EntityCount = 0,
                    ResidentEntityCount = 0,
                    FixtureEntityCount = 0,
                    IncludedAssetEntityCount = 0,
                    PersonalPropertyEntityCount = 0,
                    CapturedAt = now,
                    DataVersion = checked((current?.Snapshot.DataVersion ?? 0) + 1),
                    LastAppliedEventId = eventId,
                };
            if (!Valid(next))
                return false;
            if (present)
            {
                var property = await _properties.GetAsync(next.PropertyId, true);
                if (property == null || next.FixtureEntityCount > property.Snapshot.StorageBudget.MaximumFixtureItems ||
                    next.IncludedAssetEntityCount > property.Snapshot.StorageBudget.MaximumIncludedAssets ||
                    next.PersonalPropertyEntityCount > property.Snapshot.StorageBudget.MaximumPersonalItems ||
                    next.EntityCount > property.Snapshot.StorageBudget.MaximumMaterializedEntities)
                    return false;
            }

            var receipt = new ContentReceipt(next, expectedVersion + 1, epoch, now);
            var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                Guid.NewGuid(),
                present ? "living-city.property-content.upsert" : "living-city.property-content.delete",
                "property-content",
                Key(next.PropertyId),
                eventId,
                present ? "living-city.property-content.captured" : "living-city.property-content.deleted",
                AggregateType,
                Key(next.PropertyId),
                expectedVersion,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(next, JsonOptions),
                JsonSerializer.Serialize(new ContentEvent(next.PropertyId, present, auditSource), JsonOptions),
                JsonSerializer.Serialize(receipt, JsonOptions),
                Guid.NewGuid(),
                null,
                epoch,
                now,
                []));
            if (result.Status != NCPersistentMutationStatus.Applied)
                return false;

            _cache[next.PropertyId] = new NCPropertyContentRecord(next, result.AggregateVersion, epoch, now);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool Valid(NCPropertyContentSnapshot snapshot)
    {
        if (!snapshot.PropertyId.IsValid || !snapshot.CapturedAt.IsValid || snapshot.DataVersion < 1 ||
            snapshot.LastAppliedEventId == Guid.Empty)
            return false;
        if (!snapshot.Present)
            return snapshot.TemplateId.Length == 0 && snapshot.TemplateRevision == 0 && snapshot.FormatVersion == 0 &&
                   snapshot.Payload.Length == 0 && snapshot.EntityCount == 0 && snapshot.ResidentEntityCount == 0 &&
                   snapshot.FixtureEntityCount == 0 && snapshot.IncludedAssetEntityCount == 0 &&
                   snapshot.PersonalPropertyEntityCount == 0;

        return snapshot.TemplateId.Length is > 0 and <= 64 && snapshot.TemplateRevision > 0 &&
               snapshot.FormatVersion > 0 && snapshot.Payload.Length > 0 && snapshot.EntityCount >= 0 &&
               snapshot.ResidentEntityCount >= 0 && snapshot.ResidentEntityCount <= snapshot.EntityCount &&
               snapshot.FixtureEntityCount >= 0 && snapshot.IncludedAssetEntityCount >= 0 &&
               snapshot.PersonalPropertyEntityCount >= 0 &&
               snapshot.FixtureEntityCount + snapshot.IncludedAssetEntityCount +
               snapshot.PersonalPropertyEntityCount == snapshot.EntityCount &&
               snapshot.ResidentEntityCount == snapshot.IncludedAssetEntityCount +
               snapshot.PersonalPropertyEntityCount;
    }

    private static NCPropertyContentRecord? Deserialize(NCAggregateSnapshotData? stored, NCApartmentId propertyId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = NormalizeLegacy(JsonSerializer.Deserialize<NCPropertyContentSnapshot>(stored.Payload,
                JsonOptions));
            return snapshot != null && snapshot.PropertyId == propertyId && Valid(snapshot)
                ? new NCPropertyContentRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static NCPropertyContentSnapshot? NormalizeLegacy(NCPropertyContentSnapshot? snapshot)
    {
        if (snapshot == null || !snapshot.Present || snapshot.EntityCount == 0 ||
            snapshot.FixtureEntityCount + snapshot.IncludedAssetEntityCount +
            snapshot.PersonalPropertyEntityCount == snapshot.EntityCount)
            return snapshot;
        return snapshot with
        {
            FixtureEntityCount = Math.Max(0, snapshot.EntityCount - snapshot.ResidentEntityCount),
            IncludedAssetEntityCount = 0,
            PersonalPropertyEntityCount = snapshot.ResidentEntityCount,
        };
    }

    private static string Key(NCApartmentId propertyId) => propertyId.Value.ToString("D");

    private sealed record ContentEvent(NCApartmentId PropertyId, bool Present, string AuditSource);
    private sealed record ContentReceipt(
        NCPropertyContentSnapshot Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
}
