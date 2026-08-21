// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.CityServices;

public sealed record NCCityNewsRecord(NCCityNewsSnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);

/// <summary>Persistent structured source for CitiNet; stores localization keys, never generated prose.</summary>
public sealed partial class NCCityNewsRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.city-news";
    public const int SnapshotFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy =
        JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    private readonly ConcurrentDictionary<NCCityNewsId, NCCityNewsRecord> _cache = [];

    public async Task<NCCityNewsRecord?> GetAsync(NCCityNewsId id, bool reload = false)
    {
        if (!id.IsValid) return null;
        if (!reload && _cache.TryGetValue(id, out var cached)) return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, id.ToString());
        var loaded = Deserialize(stored, id);
        if (loaded != null) _cache[id] = loaded;
        return loaded;
    }

    public async Task<IReadOnlyList<NCCityNewsRecord>> GetPageAsync(NCCityNewsId? after, int limit)
    {
        if (limit is < 1 or > 200) return [];
        var page = await _database.GetNCAggregateSnapshotPageAsync(AggregateType,
            after is { IsValid: true } cursor ? cursor.ToString() : null, limit);
        return page.Select(stored => Guid.TryParse(stored.AggregateId, out var raw)
                ? Deserialize(stored, new NCCityNewsId(raw)) : null).OfType<NCCityNewsRecord>()
            .OrderByDescending(value => value.Snapshot.PublishedAt.Ticks).ToArray();
    }

    public async Task<NCCityNewsRecord?> PublishIncidentAsync(NCCityIncidentRecord incident,
        NCCityNewsKind kind, string headlineKey, string bodyKey)
    {
        var id = NCPersistentIdentity.CityNews(incident.Snapshot.IncidentId, incident.Snapshot.Stage);
        if (!_clock.TryGetCurrentTime(out var epoch, out var now) || headlineKey.Length is < 1 or > 128 ||
            bodyKey.Length is < 1 or > 128) return null;
        if (await GetAsync(id, true) is { } existing) return existing;
        var eventId = Guid.NewGuid();
        var snapshot = new NCCityNewsSnapshot(id, incident.Snapshot.IncidentId, kind,
            incident.Snapshot.LocationZone, incident.Snapshot.Severity, headlineKey, bodyKey,
            [incident.Snapshot.Kind.ToString().ToLowerInvariant()], incident.Snapshot.LastAppliedEventId,
            now, false, 1, eventId);
        var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
            NCPersistentIdentity.Command("city-news", "publish", id.ToString()), "living-city.city-news.publish",
            "city-news", id.ToString(), eventId, "living-city.city-news.published", AggregateType, id.ToString(),
            0, SnapshotFormatVersion, JsonSerializer.Serialize(snapshot, JsonOptions),
            JsonSerializer.Serialize(new NewsEvent(id, snapshot.IncidentId), JsonOptions),
            JsonSerializer.Serialize(id, JsonOptions),
            NCPersistentIdentity.Correlation("city-news", "publish", id.ToString()),
            incident.Snapshot.LastAppliedEventId, epoch, now, []));
        if (result.Status is not (NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted))
            return null;
        var loaded = await GetAsync(id, true);
        if (loaded != null) RaiseLocalEvent(new NCCityNewsPersistedEvent(loaded));
        return loaded;
    }

    private static NCCityNewsRecord? Deserialize(NCAggregateSnapshotData? stored, NCCityNewsId id)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion) return null;
        try
        {
            var value = JsonSerializer.Deserialize<NCCityNewsSnapshot>(stored.Payload, JsonOptions);
            return value?.NewsId == id ? new(value, stored.AggregateVersion, stored.SessionEpoch,
                stored.ActiveWorldTime) : null;
        }
        catch (JsonException) { return null; }
    }
    private sealed record NewsEvent(NCCityNewsId NewsId, NCCityIncidentId IncidentId);
}

public sealed class NCCityNewsPersistedEvent(NCCityNewsRecord news) : EntityEventArgs
{
    public NCCityNewsRecord News { get; } = news;
}
