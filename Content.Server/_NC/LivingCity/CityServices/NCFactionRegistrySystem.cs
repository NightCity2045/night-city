// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

public enum NCFactionMutationStatus : byte { Applied, Duplicate, NotFound, VersionConflict, Rejected, StorageFailure }
public sealed record NCFactionRecord(NCFactionSnapshot Snapshot, long AggregateVersion, NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);
public sealed record NCFactionMutationResult(NCFactionMutationStatus Status, NCFactionRecord? Faction, string? Error);
public sealed record NCCreateFactionRequest(Guid CommandId, NCFactionSnapshot Snapshot, Guid CorrelationId,
    Guid? CausationId = null, string AuditSource = "faction-create");
public sealed record NCSetFactionTerritoryRequest(Guid CommandId, NCOrganizationId FactionId, long ExpectedVersion,
    NCFactionTerritoryStake Stake, NCPartyRef Actor, Guid CorrelationId, Guid? CausationId = null,
    string AuditSource = "faction-territory");
public sealed record NCSetFactionRelationRequest(Guid CommandId, NCOrganizationId FactionId, long ExpectedVersion,
    NCFactionRelation Relation, NCPartyRef Actor, Guid CorrelationId, Guid? CausationId = null,
    string AuditSource = "faction-relation");

/// <summary>Persistent faction policy and territory influence authority keyed by the existing organization identity.</summary>
public sealed partial class NCFactionRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.faction";
    public const int SnapshotFormatVersion = 1;
    private static readonly ProtoId<NCFactionPolicyPrototype> DefaultPolicy = "Default";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;

    private readonly ConcurrentDictionary<NCOrganizationId, NCFactionRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public int CachedCount => _cache.Count;
    public IReadOnlyList<NCFactionRecord> Cached => _cache.Values.OrderBy(value => value.Snapshot.FactionId.Value)
        .ToArray();

    public async Task<NCFactionRecord?> GetAsync(NCOrganizationId id, bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(id, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, id.ToString());
        var loaded = Deserialize(stored, id);
        if (loaded != null)
            _cache[id] = loaded;
        return loaded;
    }

    public async Task<NCFactionMutationResult> CreateAsync(NCCreateFactionRequest request)
    {
        if (!TryPolicy(out var policy) || request.CommandId == Guid.Empty || request.CorrelationId == Guid.Empty ||
            !Valid(request.Snapshot, policy) || !_clock.TryGetCurrentTime(out var epoch, out var now) ||
            request.Snapshot.Party != NCPartyRef.ForOrganization(request.Snapshot.FactionId) ||
            !(await _parties.ResolveAsync(request.Snapshot.Party, true)).IsResolved)
            return Reject("The faction creation request is invalid.");
        var gate = Gate(request.Snapshot.FactionId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(request.Snapshot.FactionId, true);
            if (current != null)
                return current.Snapshot.StableKey == request.Snapshot.StableKey
                    ? new(NCFactionMutationStatus.Duplicate, current, null)
                    : Reject("The faction identity belongs to different data.");
            var eventId = Guid.NewGuid();
            var proposed = request.Snapshot with
            {
                Territories = request.Snapshot.Territories.OrderBy(value => value.ZoneId.Value).ToArray(),
                Relations = request.Snapshot.Relations.OrderBy(value => value.OtherFactionId.Value).ToArray(),
                DataVersion = 1,
                LastAppliedEventId = eventId,
            };
            return await Persist(request.CommandId, "living-city.faction.create", null, proposed, eventId,
                "living-city.faction.created", request.CorrelationId, request.CausationId, request.AuditSource,
                epoch, now);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<NCFactionMutationResult> SetTerritoryAsync(NCSetFactionTerritoryRequest request) =>
        MutateAsync(request.CommandId, request.FactionId, request.ExpectedVersion, request.Actor,
            request.CorrelationId, request.CausationId, request.AuditSource, (current, policy, now) =>
            {
                if (!request.Stake.ZoneId.IsValid || request.Stake.Influence is < 0 ||
                    request.Stake.Influence > policy.MaximumInfluence)
                    return null;
                var stakes = current.Territories.Where(value => value.ZoneId != request.Stake.ZoneId).ToList();
                if (stakes.Count >= policy.MaximumTerritories)
                    return null;
                stakes.Add(request.Stake with { UpdatedAt = now });
                return current with { Territories = stakes.OrderBy(value => value.ZoneId.Value).ToArray() };
            }, "living-city.faction.territory-changed");

    public Task<NCFactionMutationResult> SetRelationAsync(NCSetFactionRelationRequest request) =>
        MutateAsync(request.CommandId, request.FactionId, request.ExpectedVersion, request.Actor,
            request.CorrelationId, request.CausationId, request.AuditSource, (current, policy, _) =>
            {
                if (!request.Relation.OtherFactionId.IsValid || request.Relation.OtherFactionId == request.FactionId ||
                    request.Relation.Trust is < -1000 or > 1000 || request.Relation.Hostility is < 0 or > 1000)
                    return null;
                var values = current.Relations.Where(value =>
                    value.OtherFactionId != request.Relation.OtherFactionId).ToList();
                if (values.Count >= policy.MaximumRelations)
                    return null;
                values.Add(request.Relation);
                return current with { Relations = values.OrderBy(value => value.OtherFactionId.Value).ToArray() };
            }, "living-city.faction.relation-changed");

    private async Task<NCFactionMutationResult> MutateAsync(Guid commandId, NCOrganizationId factionId,
        long expectedVersion, NCPartyRef actor, Guid correlationId, Guid? causationId, string auditSource,
        Func<NCFactionSnapshot, NCFactionPolicyPrototype, NCActiveWorldTime, NCFactionSnapshot?> mutate,
        string eventType)
    {
        if (!TryPolicy(out var policy) || commandId == Guid.Empty || !factionId.IsValid || expectedVersion <= 0 ||
            actor != NCPartyRef.ForOrganization(factionId) || correlationId == Guid.Empty ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject("The faction mutation request is invalid.");
        var gate = Gate(factionId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(factionId, true);
            if (current == null)
                return new(NCFactionMutationStatus.NotFound, null, "The faction does not exist.");
            if (current.AggregateVersion != expectedVersion)
                return new(NCFactionMutationStatus.VersionConflict, current, "The faction version changed.");
            var proposed = mutate(current.Snapshot, policy, now);
            if (proposed == null)
                return Reject("The faction mutation is outside policy bounds.");
            var eventId = Guid.NewGuid();
            proposed = proposed with { DataVersion = current.Snapshot.DataVersion + 1,
                LastAppliedEventId = eventId };
            return await Persist(commandId, "living-city.faction.update", current, proposed, eventId, eventType,
                correlationId, causationId, auditSource, epoch, now);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NCFactionMutationResult> Persist(Guid commandId, string commandType, NCFactionRecord? current,
        NCFactionSnapshot proposed, Guid eventId, string eventType, Guid correlationId, Guid? causationId,
        string auditSource, NCSessionEpoch epoch, NCActiveWorldTime now)
    {
        var id = proposed.FactionId.ToString();
        var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(commandId,
            commandType, "faction", id, eventId, eventType, AggregateType, id, current?.AggregateVersion ?? 0,
            SnapshotFormatVersion, JsonSerializer.Serialize(proposed, JsonOptions),
            JsonSerializer.Serialize(new FactionEvent(proposed.FactionId, eventType, auditSource), JsonOptions),
            JsonSerializer.Serialize(new FactionReceipt(proposed.FactionId), JsonOptions), correlationId,
            causationId, epoch, now, []));
        if (result.Status is not (NCPersistentMutationStatus.Applied or
            NCPersistentMutationStatus.DuplicateCompleted))
            return new(result.Status == NCPersistentMutationStatus.VersionConflict
                ? NCFactionMutationStatus.VersionConflict : NCFactionMutationStatus.StorageFailure,
                current, result.Error ?? "The faction mutation failed.");
        var loaded = await GetAsync(proposed.FactionId, true);
        if (loaded == null)
            return Reject("The faction could not be reloaded.");
        RaiseLocalEvent(new NCFactionPersistedEvent(loaded));
        return new(result.Status == NCPersistentMutationStatus.Applied ? NCFactionMutationStatus.Applied :
            NCFactionMutationStatus.Duplicate, loaded, null);
    }

    private bool TryPolicy(out NCFactionPolicyPrototype policy) => _prototypes.TryIndex(DefaultPolicy, out policy!) &&
        policy.MaximumTerritories is > 0 and <= 2048 && policy.MaximumRelations is > 0 and <= 1024 &&
        policy.MaximumInfluence > 0 && policy.ControlThreshold is > 0 &&
        policy.ControlThreshold <= policy.MaximumInfluence && policy.ControlMargin >= 0;
    private static bool Valid(NCFactionSnapshot value, NCFactionPolicyPrototype policy) =>
        value.FactionId.IsValid && value.Party.IsValid && value.StableKey.Length is > 0 and <= 96 &&
        value.DisplayName.Length is > 0 and <= 128 && value.IdeologyKeys.Count is > 0 and <= 32 &&
        value.Territories.Count <= policy.MaximumTerritories && value.Relations.Count <= policy.MaximumRelations &&
        value.Policy.TaxBasisPoints is >= 0 and <= 10_000 && value.Policy.ProtectionBasisPoints is >= 0 and <= 10_000 &&
        value.Resources.AccountId.IsValid && value.Resources.AvailableManpower >= 0 &&
        value.Resources.ReservedManpower >= 0;
    private static NCFactionRecord? Deserialize(NCAggregateSnapshotData? stored, NCOrganizationId id)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var value = JsonSerializer.Deserialize<NCFactionSnapshot>(stored.Payload, JsonOptions);
            return value?.FactionId == id ? new NCFactionRecord(value, stored.AggregateVersion,
                stored.SessionEpoch, stored.ActiveWorldTime) : null;
        }
        catch (JsonException) { return null; }
    }
    private SemaphoreSlim Gate(Guid id) => _gates[(id.GetHashCode() & int.MaxValue) % _gates.Length];
    private static NCFactionMutationResult Reject(string error) => new(NCFactionMutationStatus.Rejected, null, error);
    private sealed record FactionReceipt(NCOrganizationId FactionId);
    private sealed record FactionEvent(NCOrganizationId FactionId, string EventType, string AuditSource);
}

public sealed class NCFactionPersistedEvent(NCFactionRecord faction) : EntityEventArgs
{
    public NCFactionRecord Faction { get; } = faction;
}
