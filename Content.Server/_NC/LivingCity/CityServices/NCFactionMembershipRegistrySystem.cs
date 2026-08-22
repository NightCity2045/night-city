// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

/// <summary>
/// Persistent many-affiliation authority for city actors. Group membership remains tactical and singular; faction
/// membership is social/organizational and may coexist with formal employment.
/// </summary>
public sealed partial class NCFactionMembershipRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.actor-faction-memberships";
    public const int SnapshotFormatVersion = 1;
    private const int MaximumMemberships = 16;
    private const int MaximumRoleLength = 64;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;

    private readonly ConcurrentDictionary<NCActorId, NCFactionMembershipRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public int CachedActorCount => _cache.Count;

    public async Task<NCFactionMembershipRecord?> GetAsync(NCActorId actorId, bool forceReload = false)
    {
        if (!actorId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(actorId, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, actorId.ToString());
        var record = Deserialize(stored, actorId);
        if (record != null)
            _cache[actorId] = record;
        return record;
    }

    public async Task<NCFactionMembershipMutationResult> EnsureAsync(NCEnsureFactionMembershipRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.ActorId.IsValid || request.CorrelationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.Role) || request.Role.Trim().Length > MaximumRoleLength ||
            string.IsNullOrWhiteSpace(request.AuditSource) || request.AuditSource.Length > 128 ||
            !_prototypes.TryIndex(request.Faction, out NCFactionProfilePrototype? profile) || profile == null ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject("The faction membership request is invalid.");
        }

        var actorParty = NCPartyRef.ForActor(request.ActorId);
        var actor = await _parties.ResolveAsync(actorParty, true);
        var factionParty = NCPersistentIdentity.Organization(profile.StableKey);
        var registered = await _finance.EnsureOrganizationAsync(
            profile.StableKey,
            profile.DisplayName,
            request.AuditSource);
        if (!actor.IsResolved || !registered.IsResolved)
            return Reject(actor.Error ?? registered.Error ?? "A membership party is unavailable.");

        var factionId = new NCOrganizationId(factionParty.PersistentId);
        var gate = Gate(request.ActorId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(request.ActorId, true);
            var existing = current?.Snapshot.Memberships.FirstOrDefault(value =>
                value.FactionId == factionId && value.Status != NCFactionMembershipStatus.Former);
            if (existing != null)
            {
                return existing.Role == request.Role.Trim()
                    ? new(NCFactionMembershipMutationStatus.Duplicate, current, null)
                    : new(NCFactionMembershipMutationStatus.Conflict, current,
                        "The actor already has another active role in this faction.");
            }

            var memberships = current?.Snapshot.Memberships.ToList() ?? [];
            if (memberships.Count >= MaximumMemberships)
                return Reject("The actor affiliation limit was reached.");
            memberships.Add(new NCFactionMembership(
                factionId,
                request.Role.Trim(),
                NCFactionMembershipStatus.Active,
                now,
                now));
            var eventId = Guid.NewGuid();
            var snapshot = new NCActorFactionMembershipSnapshot(
                request.ActorId,
                memberships.OrderBy(static value => value.FactionId.Value).ToArray(),
                (current?.Snapshot.DataVersion ?? 0) + 1,
                eventId);
            var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                request.CommandId,
                "living-city.faction-membership.ensure",
                "actor",
                request.ActorId.ToString(),
                eventId,
                "living-city.faction-membership.added",
                AggregateType,
                request.ActorId.ToString(),
                current?.AggregateVersion ?? 0,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                JsonSerializer.Serialize(new MembershipEvent(request.ActorId, factionId, request.Role.Trim()),
                    JsonOptions),
                JsonSerializer.Serialize(new MembershipReceipt(request.ActorId, factionId), JsonOptions),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now,
                []));
            if (result.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
            {
                return new(result.Status == NCPersistentMutationStatus.VersionConflict
                        ? NCFactionMembershipMutationStatus.Conflict
                        : NCFactionMembershipMutationStatus.StorageFailure,
                    current,
                    result.Error ?? "The faction membership mutation failed.");
            }

            var loaded = await GetAsync(request.ActorId, true);
            return loaded == null
                ? Reject("The faction membership could not be reloaded.")
                : new(result.Status == NCPersistentMutationStatus.Applied
                        ? NCFactionMembershipMutationStatus.Applied
                        : NCFactionMembershipMutationStatus.Duplicate,
                    loaded,
                    null);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to ensure faction membership for actor {request.ActorId}: {exception}");
            return new(NCFactionMembershipMutationStatus.StorageFailure, null, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private static NCFactionMembershipRecord? Deserialize(NCAggregateSnapshotData? stored, NCActorId actorId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCActorFactionMembershipSnapshot>(stored.Payload, JsonOptions);
            return snapshot?.ActorId == actorId && snapshot.DataVersion >= 1 &&
                   snapshot.Memberships.Count <= MaximumMemberships
                ? new NCFactionMembershipRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SemaphoreSlim Gate(NCActorId actorId) =>
        _gates[(actorId.GetHashCode() & int.MaxValue) % _gates.Length];

    private static NCFactionMembershipMutationResult Reject(string error) =>
        new(NCFactionMembershipMutationStatus.Rejected, null, error);

    private sealed record MembershipEvent(NCActorId ActorId, NCOrganizationId FactionId, string Role);
    private sealed record MembershipReceipt(NCActorId ActorId, NCOrganizationId FactionId);
}
