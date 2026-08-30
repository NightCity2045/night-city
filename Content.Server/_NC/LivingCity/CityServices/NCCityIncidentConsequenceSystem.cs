// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Threading;
using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Market;
using Content.Server._NC.LivingCity.Social;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

/// <summary>
/// Projects a resolved incident into existing memory, faction and market authorities. Each target mutation uses a
/// stable command identity, so recovery may safely publish the same incident again without duplicating consequences.
/// </summary>
public sealed partial class NCCityIncidentConsequenceSystem : EntitySystem
{
    private const int MaximumFactionMutationAttempts = 2;
    private const int MaximumSourceEvents = 8;
    private static readonly ProtoId<NCIncidentPolicyPrototype> DefaultPolicy = "Default";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCFactionRegistrySystem _factions = default!;
    [Dependency] private NCMemoryRegistrySystem _memories = default!;
    [Dependency] private NCMarketRegistrySystem _market = default!;

    private readonly ConcurrentDictionary<NCCityIncidentId, byte> _inFlight = [];
    private long _completed;
    private long _failed;
    private string? _lastError;

    public int InFlightCount => _inFlight.Count;
    public long CompletedCount => Interlocked.Read(ref _completed);
    public long FailedCount => Interlocked.Read(ref _failed);
    public string? LastError => Volatile.Read(ref _lastError);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityIncidentPersistedEvent>(OnIncidentPersisted);
    }

    private void OnIncidentPersisted(NCCityIncidentPersistedEvent args)
    {
        if (args.Incident.Snapshot.Stage != NCCityIncidentStage.Resolved ||
            args.Incident.Snapshot.Outcome == null ||
            !_inFlight.TryAdd(args.Incident.Snapshot.IncidentId, 0))
            return;

        _ = ProjectAsync(args.Incident);
    }

    private async Task ProjectAsync(NCCityIncidentRecord incident)
    {
        var errors = new List<string>();
        try
        {
            if (!TryGetPolicy(out var policy, out var error))
            {
                errors.Add(error!);
            }
            else
            {
                await ProjectMemoriesAsync(incident, policy, errors);
                await ProjectServiceDemandAsync(incident, policy, errors);
                await ProjectFactionInfluenceAsync(incident, policy, errors);
            }
        }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
        }
        finally
        {
            _inFlight.TryRemove(incident.Snapshot.IncidentId, out _);
        }

        if (errors.Count == 0)
        {
            Interlocked.Increment(ref _completed);
            Volatile.Write(ref _lastError, null);
            return;
        }

        Interlocked.Increment(ref _failed);
        Volatile.Write(ref _lastError, string.Join("; ", errors.Take(4)));
    }

    private async Task ProjectMemoriesAsync(
        NCCityIncidentRecord incident,
        NCIncidentPolicyPrototype policy,
        List<string> errors)
    {
        var snapshot = incident.Snapshot;
        var owners = snapshot.Participants.Select(static value => value.Party)
            .Where(static party => party.Kind is NCPartyKind.Actor or NCPartyKind.Character)
            .Distinct()
            .ToArray();
        var participants = snapshot.Participants.Select(static value => value.Party)
            .Concat(snapshot.Outcome!.ResponsibleParties)
            .Where(static party => party.IsValid)
            .Distinct()
            .Take(policy.MaximumOutcomeMemoryParticipants)
            .ToArray();
        var sources = snapshot.Causes.Select(static value => value.SourceWorldEventId)
            .Where(static value => value != Guid.Empty)
            .Distinct()
            .Take(MaximumSourceEvents)
            .ToArray();

        foreach (var owner in owners)
        {
            var stable = $"{snapshot.IncidentId}.{owner}";
            var result = await _memories.RememberAsync(new NCRememberWorldEventRequest(
                NCPersistentIdentity.Command("incident-consequence", "remember", stable),
                owner,
                policy.OutcomeMemory,
                snapshot.IncidentId.Value,
                sources,
                participants.Where(value => value != owner).ToArray(),
                new NCMemorySource(NCMemorySourceKind.Inference, default),
                [],
                snapshot.Severity,
                1000,
                NCMemoryPrivacy.Restricted,
                owner,
                NCPersistentIdentity.Correlation("incident-consequence", "remember", stable),
                snapshot.LastAppliedEventId,
                "incident-consequence"));
            if (result.Status is not (NCMemoryMutationStatus.Applied or NCMemoryMutationStatus.Duplicate))
                errors.Add($"memory {owner}: {result.Error ?? result.Status.ToString()}");
        }
    }

    private async Task ProjectServiceDemandAsync(
        NCCityIncidentRecord incident,
        NCIncidentPolicyPrototype policy,
        List<string> errors)
    {
        var snapshot = incident.Snapshot;
        if (!policy.OutcomeServiceDemands.TryGetValue(snapshot.Kind, out var serviceId))
            return;
        if (!_prototypes.TryIndex(serviceId, out NCEconomicServicePrototype? service) || service == null)
        {
            errors.Add($"unknown outcome service {serviceId}");
            return;
        }

        var requester = snapshot.Participants
            .OrderBy(static value => value.Role is NCIncidentParticipantRole.Victim or
                NCIncidentParticipantRole.Subject or NCIncidentParticipantRole.Reporter ? 0 : 1)
            .Select(static value => value.Party)
            .FirstOrDefault(static value => value.IsValid);
        if (!requester.IsValid)
            return;

        var stable = $"incident-outcome.{snapshot.IncidentId}";
        var requestId = NCPersistentIdentity.MarketRequest(requester, stable);
        var maximumPrice = Math.Min(policy.MaximumOutcomeDemandPrice,
            Math.Max(service.BasePrice ?? 0, snapshot.Outcome!.EconomicLoss));
        var result = await _market.CreateRequestAsync(new NCCreateMarketRequestRequest(
            NCPersistentIdentity.Command("incident-consequence", "service-demand", stable),
            requestId,
            NCMarketRequestKind.Service,
            service.ID,
            1,
            maximumPrice,
            0,
            requester,
            NCMarketChannel.Agent,
            new NCMarketAccessPolicy(NCMarketVisibility.Public, [], string.Empty, 0, 0),
            snapshot.LocationZone,
            policy.OutcomeDemandLifetime,
            requester,
            NCPersistentIdentity.Correlation("incident-consequence", "service-demand", stable),
            snapshot.LastAppliedEventId,
            "incident-consequence"));
        if (result.Status is not (NCMarketMutationStatus.Applied or NCMarketMutationStatus.Duplicate))
            errors.Add($"service demand: {result.Error ?? result.Status.ToString()}");
    }

    private async Task ProjectFactionInfluenceAsync(
        NCCityIncidentRecord incident,
        NCIncidentPolicyPrototype policy,
        List<string> errors)
    {
        if (policy.ResponsibleFactionInfluenceLossPermille <= 0)
            return;
        var snapshot = incident.Snapshot;
        var factions = snapshot.Participants
            .Where(static value => value.Role == NCIncidentParticipantRole.ResponsibleFaction)
            .Select(static value => value.Party)
            .Concat(snapshot.Outcome!.ResponsibleParties)
            .Where(static party => party.Kind == NCPartyKind.Organization)
            .Distinct()
            .ToArray();
        foreach (var party in factions)
        {
            var factionId = new NCOrganizationId(party.PersistentId);
            var applied = false;
            for (var attempt = 0; attempt < MaximumFactionMutationAttempts; attempt++)
            {
                var faction = await _factions.GetAsync(factionId, true);
                if (faction == null || !TryFindTerritory(faction.Snapshot, snapshot.LocationZone, out var stake))
                {
                    applied = true;
                    break;
                }

                var loss = Math.Max(1,
                    snapshot.Severity * policy.ResponsibleFactionInfluenceLossPermille / 1000);
                var influence = Math.Max(0, stake.Influence - loss);
                if (influence == stake.Influence)
                {
                    applied = true;
                    break;
                }

                var stable = $"{snapshot.IncidentId}.{factionId}";
                var result = await _factions.SetTerritoryAsync(new NCSetFactionTerritoryRequest(
                    NCPersistentIdentity.Command("incident-consequence", "faction-influence", stable),
                    factionId,
                    faction.AggregateVersion,
                    stake with { Influence = influence },
                    party,
                    NCPersistentIdentity.Correlation("incident-consequence", "faction-influence", stable),
                    snapshot.LastAppliedEventId,
                    "incident-consequence"));
                if (result.Status is NCFactionMutationStatus.Applied or NCFactionMutationStatus.Duplicate)
                {
                    applied = true;
                    break;
                }
                if (result.Status != NCFactionMutationStatus.VersionConflict)
                {
                    errors.Add($"faction {factionId}: {result.Error ?? result.Status.ToString()}");
                    applied = true;
                    break;
                }
            }

            if (!applied)
                errors.Add($"faction {factionId}: version conflict");
        }
    }

    private bool TryFindTerritory(
        NCFactionSnapshot faction,
        Content.Shared._NC.City.Zones.NCZoneId location,
        out NCFactionTerritoryStake stake)
    {
        var current = location;
        for (var depth = 0; depth < 16 && current.IsValid; depth++)
        {
            var found = faction.Territories.FirstOrDefault(value => value.ZoneId == current);
            if (found != null)
            {
                stake = found;
                return true;
            }
            if (!_zones.TryGetZone(current, out var zone) || !zone.Parent.IsValid)
                break;
            current = zone.Parent;
        }

        stake = default!;
        return false;
    }

    private bool TryGetPolicy(out NCIncidentPolicyPrototype policy, out string? error)
    {
        if (!_prototypes.TryIndex(DefaultPolicy, out policy!) ||
            !_prototypes.HasIndex(policy.OutcomeMemory) ||
            policy.MaximumOutcomeMemoryParticipants is < 1 or > 64 ||
            policy.OutcomeDemandLifetime <= TimeSpan.Zero ||
            policy.OutcomeDemandLifetime > TimeSpan.FromDays(7) ||
            policy.MaximumOutcomeDemandPrice < 0 ||
            policy.ResponsibleFactionInfluenceLossPermille is < 0 or > 1000 ||
            policy.OutcomeServiceDemands.Count > 32 ||
            policy.OutcomeServiceDemands.Any(value => !Enum.IsDefined(value.Key) ||
                !_prototypes.HasIndex(value.Value)))
        {
            error = "Incident consequence policy is invalid.";
            return false;
        }

        error = null;
        return true;
    }
}
