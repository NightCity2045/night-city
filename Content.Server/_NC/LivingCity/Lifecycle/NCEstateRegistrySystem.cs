// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Lifecycle;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Lifecycle;

public sealed record NCEstateRecord(NCEstateSnapshot Snapshot, long AggregateVersion, NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

/// <summary>Irreversible, idempotent estate/claims authority. Domain assets retain their own transfer authority.</summary>
public sealed partial class NCEstateRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.estate";
    public const int SnapshotFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy =
        JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyDirectorySystem _directory = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    private readonly ConcurrentDictionary<NCEstateId, NCEstateRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<NCEstateRecord?> GetAsync(NCEstateId id, bool reload = false)
    {
        if (!id.IsValid) return null;
        if (!reload && _cache.TryGetValue(id, out var cached)) return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, id.ToString());
        var loaded = Deserialize(stored, id);
        if (loaded != null) _cache[id] = loaded;
        return loaded;
    }

    public async Task<NCEstateRecord?> OpenAsync(NCPartyRef decedent, NCPartyRef beneficiary,
        Guid deathEventId, string displayName)
    {
        if (!decedent.IsValid || deathEventId == Guid.Empty || !_clock.TryGetCurrentTime(out var epoch, out var now))
            return null;
        var id = NCPersistentIdentity.Estate(decedent);
        var party = NCPartyRef.ForEstate(id);
        await _directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            NCPersistentIdentity.Command("estate", "register", id.ToString()), party,
            string.IsNullOrWhiteSpace(displayName) ? $"Estate {id}" : displayName.Trim(),
            NCPersistentIdentity.Correlation("estate", "register", id.ToString()), deathEventId,
            "estate-death-workflow"));
        await _finance.EnsurePrimaryAccountAsync(party, "estate-death-workflow", deathEventId);
        var gate = Gate(id.Value);
        await gate.WaitAsync();
        try
        {
            if (await GetAsync(id, true) is { } existing) return existing;
            var eventId = Guid.NewGuid();
            var snapshot = new NCEstateSnapshot(id, party, decedent,
                NCPersistentIdentity.CityService("estate-administrator"), beneficiary, default, NCEstateStatus.Open,
                deathEventId, [], [], now, null, 1, eventId);
            return await Persist(snapshot, 0, "open", deathEventId, epoch, now);
        }
        finally { gate.Release(); }
    }

    public Task<NCEstateRecord?> AddAssetAsync(NCEstateId id, long version, NCEstateAssetRef asset) =>
        Mutate(id, version, $"asset.{asset.Kind}.{asset.AssetId}", current =>
        {
            if (current.Status is NCEstateStatus.Distributed or NCEstateStatus.Closed || asset.Value < 0 ||
                string.IsNullOrWhiteSpace(asset.AssetId) || asset.AssetId.Length > 160 ||
                current.Assets.Any(value => value.Kind == asset.Kind && value.AssetId == asset.AssetId)) return null;
            return current with { Assets = current.Assets.Append(asset).ToArray() };
        });

    public Task<NCEstateRecord?> FileClaimAsync(NCEstateId id, long version, NCEstateClaim claim) =>
        Mutate(id, version, $"claim.{claim.ClaimId}", current => claim.ClaimId == Guid.Empty ||
            !claim.Claimant.IsValid || claim.Amount <= 0 || claim.ReasonKey.Length is < 1 or > 128 ||
            current.Claims.Any(value => value.ClaimId == claim.ClaimId) ? null : current with
            { Status = NCEstateStatus.Claims, Claims = current.Claims.Append(claim).ToArray() });

    public Task<NCEstateRecord?> SetAssetTransferredAsync(NCEstateId id, long version,
        NCEstateAssetKind kind, string assetId) => Mutate(id, version, $"transferred.{kind}.{assetId}", current =>
    {
        var matches = current.Assets.Count(value => value.Kind == kind && value.AssetId == assetId);
        return matches != 1 ? null : current with { Assets = current.Assets.Select(value =>
            value.Kind == kind && value.AssetId == assetId ? value with { Transferred = true } : value).ToArray() };
    });

    public Task<NCEstateRecord?> SetClaimStatusAsync(NCEstateId id, long version, Guid claimId,
        NCEstateClaimStatus status) => Mutate(id, version, $"claim-status.{claimId}.{status}", current =>
    {
        var claim = current.Claims.SingleOrDefault(value => value.ClaimId == claimId);
        if (claim == null || status is NCEstateClaimStatus.Filed || claim.Status is NCEstateClaimStatus.Paid or
            NCEstateClaimStatus.Rejected) return null;
        return current with { Claims = current.Claims.Select(value => value.ClaimId == claimId
            ? value with { Status = status } : value).ToArray() };
    });

    public Task<NCEstateRecord?> SetSuccessionAsync(NCEstateId id, long version, NCPartyRef beneficiary,
        NCPartyRef replacement) => Mutate(id, version, $"succession.{beneficiary}.{replacement}", current =>
        beneficiary.IsValid && beneficiary != current.Decedent &&
        (!replacement.IsValid || replacement != current.Decedent) ? current with
        { Beneficiary = beneficiary, Replacement = replacement } : null);

    public Task<NCEstateRecord?> CloseAsync(NCEstateId id, long version) => Mutate(id, version, "close", current =>
        current.Assets.All(value => value.Transferred) && current.Claims.All(value => value.Status is
            NCEstateClaimStatus.Paid or NCEstateClaimStatus.Rejected) ? current with
            { Status = NCEstateStatus.Closed } : null, close: true);

    private async Task<NCEstateRecord?> Mutate(NCEstateId id, long version, string operation,
        Func<NCEstateSnapshot, NCEstateSnapshot?> transition, bool close = false)
    {
        if (!id.IsValid || version < 1 || !_clock.TryGetCurrentTime(out var epoch, out var now)) return null;
        var gate = Gate(id.Value); await gate.WaitAsync();
        try
        {
            var current = await GetAsync(id, true);
            if (current == null || current.AggregateVersion != version) return null;
            var next = transition(current.Snapshot);
            if (next == null) return null;
            var eventId = Guid.NewGuid();
            next = next with { ClosedAt = close ? now : next.ClosedAt,
                DataVersion = current.Snapshot.DataVersion + 1, LastAppliedEventId = eventId };
            return await Persist(next, current.AggregateVersion, operation, current.Snapshot.LastAppliedEventId,
                epoch, now);
        }
        finally { gate.Release(); }
    }

    private async Task<NCEstateRecord?> Persist(NCEstateSnapshot snapshot, long version, string operation,
        Guid causation, NCSessionEpoch epoch, NCActiveWorldTime now)
    {
        var stable = $"{snapshot.EstateId}.{operation}";
        var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
            NCPersistentIdentity.Command("estate", operation, stable), $"living-city.estate.{operation}", "estate",
            snapshot.EstateId.ToString(), snapshot.LastAppliedEventId, $"living-city.estate.{operation}", AggregateType,
            snapshot.EstateId.ToString(), version, SnapshotFormatVersion, JsonSerializer.Serialize(snapshot, JsonOptions),
            JsonSerializer.Serialize(new EstateEvent(snapshot.EstateId, operation), JsonOptions),
            JsonSerializer.Serialize(snapshot.EstateId, JsonOptions),
            NCPersistentIdentity.Correlation("estate", operation, stable), causation, epoch, now, []));
        if (result.Status is not (NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted))
            return null;
        var loaded = await GetAsync(snapshot.EstateId, true);
        if (loaded != null) RaiseLocalEvent(new NCEstatePersistedEvent(loaded));
        return loaded;
    }
    private static NCEstateRecord? Deserialize(NCAggregateSnapshotData? stored, NCEstateId id)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion) return null;
        try { var value = JsonSerializer.Deserialize<NCEstateSnapshot>(stored.Payload, JsonOptions);
            return value?.EstateId == id ? new(value, stored.AggregateVersion, stored.SessionEpoch,
                stored.ActiveWorldTime) : null; }
        catch (JsonException) { return null; }
    }
    private SemaphoreSlim Gate(Guid id) => _gates[(id.GetHashCode() & int.MaxValue) % _gates.Length];
    private sealed record EstateEvent(NCEstateId EstateId, string Operation);
}

public sealed class NCEstatePersistedEvent(NCEstateRecord estate) : EntityEventArgs
{ public NCEstateRecord Estate { get; } = estate; }
