// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

public sealed record NCHumanityRecord(NCHumanitySnapshot Snapshot, long AggregateVersion,
    NCSessionEpoch SessionEpoch, NCActiveWorldTime PersistedAt);
public sealed record NCHumanityMutationResult(bool Applied, NCHumanityRecord? Humanity, string? Error);

/// <summary>
/// Authoritative permanent Humanity loss from cyberware operations. A source world event applies once; ordinary
/// incident code can observe crisis transitions but cannot change Humanity.
/// </summary>
public sealed partial class NCHumanityRegistrySystem : EntitySystem
{
    public const string AggregateType = "living-city.humanity";
    public const int SnapshotFormatVersion = 1;
    private static readonly ProtoId<NCHumanityPolicyPrototype> DefaultPolicy = "Default";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    private readonly ConcurrentDictionary<NCActorId, NCHumanityRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

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

    public async Task<NCHumanityMutationResult> ApplyCyberwareCostAsync(NCActorId actorId, int humanityCost,
        Guid sourceWorldEventId, NCZoneId currentZone, NCPartyRef initiator)
    {
        if (!TryPolicy(out var policy) || !actorId.IsValid || humanityCost <= 0 || sourceWorldEventId == Guid.Empty ||
            !currentZone.IsValid || !initiator.IsValid || !_clock.TryGetCurrentTime(out var epoch, out var now))
            return new(false, null, "The Humanity mutation request is invalid.");
        var gate = Gate(actorId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(actorId, true);
            if (current?.Snapshot.AppliedSourceEventIds.Contains(sourceWorldEventId) == true)
                return new(false, current, null);
            if ((current?.Snapshot.AppliedSourceEventIds.Count ?? 0) >= policy.MaximumAppliedSources)
                return new(false, current, "The Humanity provenance capacity is exhausted.");
            var maximum = current?.Snapshot.MaximumHumanity ?? policy.DefaultMaximumHumanity;
            var nextValue = Math.Max(0, (current?.Snapshot.CurrentHumanity ?? maximum) - humanityCost);
            var stage = Stage(nextValue, policy);
            var eventId = Guid.NewGuid();
            var snapshot = new NCHumanitySnapshot(actorId, maximum, nextValue,
                checked((current?.Snapshot.PermanentCyberwareLoss ?? 0) + humanityCost), stage,
                (current?.Snapshot.AppliedSourceEventIds ?? []).Append(sourceWorldEventId).ToArray(), now,
                (current?.Snapshot.DataVersion ?? 0) + 1, eventId);
            var commandId = NCPersistentIdentity.Command("humanity", "cyberware-cost",
                $"{actorId}.{sourceWorldEventId:D}");
            var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(commandId,
                "living-city.humanity.cyberware-cost", "humanity", actorId.ToString(), eventId,
                "living-city.humanity.changed", AggregateType, actorId.ToString(), current?.AggregateVersion ?? 0,
                SnapshotFormatVersion, JsonSerializer.Serialize(snapshot, JsonOptions), JsonSerializer.Serialize(
                    new HumanityEvent(actorId, humanityCost, stage, sourceWorldEventId), JsonOptions),
                JsonSerializer.Serialize(new HumanityReceipt(actorId), JsonOptions),
                NCPersistentIdentity.Correlation("humanity", "cyberware-cost",
                    $"{actorId}.{sourceWorldEventId:D}"), sourceWorldEventId, epoch, now, []));
            if (result.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return new(false, current, result.Error ?? "The Humanity mutation failed.");
            var loaded = await GetAsync(actorId, true);
            if (loaded == null)
                return new(false, null, "The Humanity state could not be reloaded.");
            RaiseLocalEvent(new NCHumanityCrisisPersistedEvent(loaded, currentZone, sourceWorldEventId, initiator,
                current?.Snapshot.CrisisStage ?? NCHumanityCrisisStage.Stable));
            return new(result.Status == NCPersistentMutationStatus.Applied, loaded, null);
        }
        finally { gate.Release(); }
    }

    private bool TryPolicy(out NCHumanityPolicyPrototype policy) => _prototypes.TryIndex(DefaultPolicy, out policy!) &&
        policy.DefaultMaximumHumanity > 0 && policy.StrainedThreshold > policy.DissociatingThreshold &&
        policy.DissociatingThreshold > policy.CyberpsychosisThreshold && policy.CyberpsychosisThreshold >= 0 &&
        policy.MaximumAppliedSources is > 0 and <= 4096;
    private static NCHumanityCrisisStage Stage(int value, NCHumanityPolicyPrototype policy) =>
        value <= policy.CyberpsychosisThreshold ? NCHumanityCrisisStage.Cyberpsychosis :
        value <= policy.DissociatingThreshold ? NCHumanityCrisisStage.Dissociating :
        value <= policy.StrainedThreshold ? NCHumanityCrisisStage.Strained : NCHumanityCrisisStage.Stable;
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
    private SemaphoreSlim Gate(Guid id) => _gates[(id.GetHashCode() & int.MaxValue) % _gates.Length];
    private sealed record HumanityReceipt(NCActorId ActorId);
    private sealed record HumanityEvent(NCActorId ActorId, int Cost, NCHumanityCrisisStage Stage,
        Guid SourceWorldEventId);
}

public sealed class NCHumanityCrisisPersistedEvent(NCHumanityRecord humanity, NCZoneId currentZone,
    Guid sourceWorldEventId, NCPartyRef initiator, NCHumanityCrisisStage previousStage) : EntityEventArgs
{
    public NCHumanityRecord Humanity { get; } = humanity;
    public NCZoneId CurrentZone { get; } = currentZone;
    public Guid SourceWorldEventId { get; } = sourceWorldEventId;
    public NCPartyRef Initiator { get; } = initiator;
    public NCHumanityCrisisStage PreviousStage { get; } = previousStage;
}
