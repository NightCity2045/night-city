// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.CityServices;

public sealed record NCHumanityRecord(NCHumanitySnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);

/// <summary>
/// Read-only pre-Gate-5 actor records used for migration. The living brain's medical snapshot owns new H/S state.
/// </summary>
public sealed partial class NCHumanityRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.humanity";
    public const int SnapshotFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    private readonly ConcurrentDictionary<NCActorId, NCHumanityRecord> _cache = [];

    public async Task<NCHumanityRecord?> GetAsync(NCActorId actorId, bool forceReload = false)
    {
        if (!actorId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(actorId, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, actorId.ToString());
        var loaded = Deserialize(stored, actorId);
        if (loaded != null)
            _cache[actorId] = loaded;
        return loaded;
    }

    private static NCHumanityRecord? Deserialize(NCAggregateSnapshotData? stored, NCActorId actorId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var value = JsonSerializer.Deserialize<NCHumanitySnapshot>(stored.Payload, JsonOptions);
            return value?.ActorId == actorId ? new NCHumanityRecord(value, stored.AggregateVersion,
                stored.SessionEpoch, stored.ActiveWorldTime) : null;
        }
        catch (JsonException) { return null; }
    }
}
