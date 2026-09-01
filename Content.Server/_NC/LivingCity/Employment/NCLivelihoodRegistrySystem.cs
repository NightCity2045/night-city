// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Employment.Components;
using Content.Server._NC.LivingCity.Fixers;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Employment;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Employment;

/// <summary>
/// Persistent one-per-actor registry for informal and civilian occupations. No per-frame work or entity scans occur.
/// </summary>
public sealed partial class NCLivelihoodRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.actor-livelihood";
    public const int SnapshotFormatVersion = 1;
    private static readonly ProtoId<NCLivelihoodProfilePrototype> FixerLivelihood = "NCFixer";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCCityActorRuntimeIndexSystem _runtime = default!;
    [Dependency] private ITaskManager _taskManager = default!;
    [Dependency] private NCFixerRegistrySystem _fixers = default!;

    private readonly ConcurrentDictionary<NCActorId, NCLivelihoodRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public int CachedActorCount => _cache.Count;

    public bool TryGetCached(NCActorId actorId, out NCLivelihoodRecord record) =>
        _cache.TryGetValue(actorId, out record!);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCPhysicalActorSpawnedEvent>(OnActorRehydrated);
    }

    public async Task<NCLivelihoodRecord?> GetAsync(NCActorId actorId, bool forceReload = false)
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

    public async Task<NCLivelihoodMutationResult> EnsureAsync(NCEnsureLivelihoodRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.ActorId.IsValid || request.CorrelationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.AuditSource) || request.AuditSource.Length > 128 ||
            !_prototypes.TryIndex(request.Profile, out NCLivelihoodProfilePrototype? profile) || profile == null ||
            string.IsNullOrWhiteSpace(profile.DisplayName) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject("The livelihood request is invalid.");
        }

        var actor = await _parties.ResolveAsync(NCPartyRef.ForActor(request.ActorId), true);
        if (!actor.IsResolved)
            return Reject(actor.Error ?? "The actor party is unavailable.");

        var gate = Gate(request.ActorId);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(request.ActorId, true);
            if (current != null)
            {
                if (current.Snapshot.ProfileId == profile.ID && profile.ID == FixerLivelihood.Id)
                    await EnsureFixerNetworkAsync(request.ActorId, current.Snapshot.LastAppliedEventId);
                return current.Snapshot.ProfileId == profile.ID
                    ? new(NCLivelihoodMutationStatus.Duplicate, current, null)
                    : new(NCLivelihoodMutationStatus.Conflict, current,
                        "The actor already has another livelihood profile.");
            }

            var eventId = Guid.NewGuid();
            var snapshot = new NCLivelihoodSnapshot(request.ActorId, profile.ID, now, 1, eventId);
            var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                request.CommandId,
                "living-city.livelihood.ensure",
                "actor",
                request.ActorId.ToString(),
                eventId,
                "living-city.livelihood.assigned",
                AggregateType,
                request.ActorId.ToString(),
                0,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                JsonSerializer.Serialize(new LivelihoodEvent(request.ActorId, profile.ID), JsonOptions),
                JsonSerializer.Serialize(new LivelihoodReceipt(request.ActorId, profile.ID), JsonOptions),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now,
                []));
            if (result.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
            {
                return new(result.Status == NCPersistentMutationStatus.VersionConflict
                        ? NCLivelihoodMutationStatus.Conflict
                        : NCLivelihoodMutationStatus.StorageFailure,
                    current,
                    result.Error ?? "The livelihood mutation failed.");
            }

            var loaded = await GetAsync(request.ActorId, true);
            if (loaded != null && profile.ID == FixerLivelihood.Id)
                await EnsureFixerNetworkAsync(request.ActorId, request.CausationId);
            return loaded == null
                ? Reject("The livelihood could not be reloaded.")
                : new(result.Status == NCPersistentMutationStatus.Applied
                        ? NCLivelihoodMutationStatus.Applied
                        : NCLivelihoodMutationStatus.Duplicate,
                    loaded,
                    null);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to ensure livelihood for actor {request.ActorId}: {exception}");
            return new(NCLivelihoodMutationStatus.StorageFailure, null, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private void OnActorRehydrated(NCPhysicalActorSpawnedEvent args)
    {
        _ = ApplyToPhysicalActorAsync(args.ActorId, args.EntityUid);
    }

    private async Task ApplyToPhysicalActorAsync(NCActorId actorId, EntityUid entityUid)
    {
        var record = await GetAsync(actorId);
        if (record == null || !_prototypes.HasIndex<NCLivelihoodProfilePrototype>(record.Snapshot.ProfileId))
            return;
        if (record.Snapshot.ProfileId == FixerLivelihood.Id)
            await EnsureFixerNetworkAsync(actorId, record.Snapshot.LastAppliedEventId);
        _taskManager.RunOnMainThread(() =>
        {
            if (!Exists(entityUid) || !_runtime.TryGetActor(entityUid, out var bound) || bound != actorId)
                return;
            EnsureComp<NCCityLivelihoodComponent>(entityUid).Profile = record.Snapshot.ProfileId;
        });
    }

    private Task<NCFixerMutationResult> EnsureFixerNetworkAsync(NCActorId actorId, Guid? causationId)
    {
        var stable = actorId.ToString();
        return _fixers.EnsureNetworkAsync(new NCEnsureFixerNetworkRequest(
            NCPersistentIdentity.Command("fixer", "ensure-network", stable),
            NCPartyRef.ForActor(actorId),
            NCPersistentIdentity.Correlation("fixer", "ensure-network", stable),
            causationId,
            "livelihood-fixer-bootstrap"));
    }

    private static NCLivelihoodRecord? Deserialize(NCAggregateSnapshotData? stored, NCActorId actorId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCLivelihoodSnapshot>(stored.Payload, JsonOptions);
            return snapshot?.ActorId == actorId && snapshot.DataVersion >= 1 &&
                   !string.IsNullOrWhiteSpace(snapshot.ProfileId)
                ? new NCLivelihoodRecord(snapshot, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SemaphoreSlim Gate(NCActorId actorId) =>
        _gates[(actorId.GetHashCode() & int.MaxValue) % _gates.Length];

    private static NCLivelihoodMutationResult Reject(string error) =>
        new(NCLivelihoodMutationStatus.Rejected, null, error);

    private sealed record LivelihoodEvent(NCActorId ActorId, string ProfileId);
    private sealed record LivelihoodReceipt(NCActorId ActorId, string ProfileId);
}
