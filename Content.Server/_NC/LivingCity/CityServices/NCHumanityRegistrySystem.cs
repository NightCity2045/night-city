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
/// Read-only pre-Gate-5 actor records used for migration. The living brain's medical snapshot owns new H/S state.
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

    /// <summary>Legacy anonymous costs are retired. Commit a physical functional package through NC medicine.</summary>
    public Task<NCHumanityMutationResult> ApplyCyberwareCostAsync(NCActorId actorId, int humanityCost,
        Guid sourceWorldEventId, NCZoneId currentZone, NCPartyRef initiator)
    {
        return Task.FromResult(new NCHumanityMutationResult(false, null,
            "Legacy Humanity writes are retired; use the physical medical package authority."));
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
