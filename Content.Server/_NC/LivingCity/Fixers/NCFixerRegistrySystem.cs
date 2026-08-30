// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Affordances;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Social;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Affordances;
using Content.Shared._NC.LivingCity.Fixers;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Fixers;

/// <summary>
/// Canonical Fixer network, deal and Night Market authority. It stores durable intent and references other domain
/// aggregates; physical delivery, money and social consequences remain authoritative in their owning systems.
/// </summary>
public sealed partial class NCFixerRegistrySystem : EntitySystem
{
    public const string NetworkAggregateType = "living-city.fixer-network";
    public const string DealAggregateType = "living-city.fixer-deal";
    public const string NightMarketAggregateType = "living-city.night-market";
    public const int SnapshotFormatVersion = 1;
    private static readonly ProtoId<NCFixerPolicyPrototype> DefaultPolicy = "Default";
    private static readonly ProtoId<NCNightMarketPolicyPrototype> DefaultNightMarketPolicy = "Default";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCRelationshipRegistrySystem _relationships = default!;
    [Dependency] private NCReputationRegistrySystem _reputations = default!;
    [Dependency] private NCSocialObligationRegistrySystem _obligations = default!;
    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;
    [Dependency] private NCCityAffordanceCatalogSystem _affordances = default!;
    [Dependency] private ITaskManager _tasks = default!;

    private readonly ConcurrentDictionary<NCFixerNetworkId, NCFixerNetworkRecord> _networks = [];
    private readonly ConcurrentDictionary<NCFixerDealId, NCFixerDealRecord> _deals = [];
    private readonly ConcurrentDictionary<NCNightMarketId, NCNightMarketRecord> _nightMarkets = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public int CachedNetworkCount => _networks.Count;
    public int CachedDealCount => _deals.Count;
    public int CachedNightMarketCount => _nightMarkets.Count;

    public IReadOnlyList<NCFixerNetworkRecord> GetCachedNetworks(int limit = 256) =>
        limit <= 0 ? [] : _networks.Values.OrderBy(value => value.Snapshot.NetworkId.Value)
            .Take(Math.Min(limit, 4096)).ToArray();

    public IReadOnlyList<NCNightMarketRecord> GetCachedNightMarkets(int limit = 256) =>
        limit <= 0 ? [] : _nightMarkets.Values.OrderBy(value => value.Snapshot.NightMarketId.Value)
            .Take(Math.Min(limit, 4096)).ToArray();

    public IReadOnlyList<NCFixerDealRecord> GetCachedDealsForParty(NCPartyRef party, int limit,
        bool includeClosed = false)
    {
        if (!party.IsValid || limit <= 0)
            return [];
        return _deals.Values.Where(value => IsParty(value.Snapshot, party) &&
                (includeClosed || value.Snapshot.Status is not (NCFixerDealStatus.Fulfilled or
                    NCFixerDealStatus.Cancelled or NCFixerDealStatus.Expired)))
            .OrderByDescending(value => value.Snapshot.UpdatedAt.Ticks)
            .Take(Math.Min(limit, 256)).ToArray();
    }

    public Task<NCFixerNetworkRecord?> GetNetworkAsync(NCFixerNetworkId id, bool forceReload = false) =>
        GetAsync(id, NetworkAggregateType, _networks, DeserializeNetwork, forceReload);
    public Task<NCFixerDealRecord?> GetDealAsync(NCFixerDealId id, bool forceReload = false) =>
        GetAsync(id, DealAggregateType, _deals, DeserializeDeal, forceReload);
    public Task<NCNightMarketRecord?> GetNightMarketAsync(NCNightMarketId id, bool forceReload = false) =>
        GetAsync(id, NightMarketAggregateType, _nightMarkets, DeserializeNightMarket, forceReload);

    /// <summary>Creates the empty durable contact network owned by a known Fixer without inventing a client.</summary>
    public async Task<NCFixerMutationResult> EnsureNetworkAsync(NCEnsureFixerNetworkRequest request)
    {
        if (!TryPolicy(out _) || request.CommandId == Guid.Empty || !request.Fixer.IsValid ||
            request.CorrelationId == Guid.Empty || string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Length > 128 || !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCFixerMutationStatus.Rejected, "The fixer network request is invalid.");
        if (!(await _parties.ResolveAsync(request.Fixer, true)).IsResolved)
            return Reject(NCFixerMutationStatus.Rejected, "The fixer party is unavailable.");

        var networkId = NCPersistentIdentity.FixerNetwork(request.Fixer);
        var gate = Gate(networkId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetNetworkAsync(networkId, true);
            if (current != null)
                return current.Snapshot.Fixer == request.Fixer
                    ? new(NCFixerMutationStatus.Duplicate, current, null, null, null)
                    : Reject(NCFixerMutationStatus.Rejected, "The fixer network identity is already occupied.");

            var eventId = Guid.NewGuid();
            var snapshot = new NCFixerNetworkSnapshot(networkId, request.Fixer, [], [], 1, eventId);
            var persisted = await Persist(request.CommandId, "living-city.fixer-network.ensure", "fixer-network",
                networkId.ToString(), [Write(NetworkAggregateType, networkId.ToString(), 0, snapshot, eventId,
                    "living-city.fixer-network.created", request.AuditSource)], request.CorrelationId,
                request.CausationId, epoch, now);
            return await CompleteNetwork(persisted, networkId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> HasAccessAsync(NCPartyRef fixer, NCPartyRef client, bool forceReload = false)
    {
        if (!fixer.IsValid || !client.IsValid || !_clock.TryGetCurrentTime(out _, out var now))
            return false;
        if (fixer == client)
            return true;
        var network = await GetNetworkAsync(NCPersistentIdentity.FixerNetwork(fixer), forceReload);
        var grant = network?.Snapshot.Access.FirstOrDefault(value => value.Client == client);
        return grant != null && await SatisfiesGrantAsync(fixer, grant, now, forceReload);
    }

    public async Task<NCFixerMutationResult> GrantAccessAsync(NCGrantFixerAccessRequest request)
    {
        if (!TryPolicy(out var policy) || request.CommandId == Guid.Empty || !request.Fixer.IsValid ||
            !request.Client.IsValid || !request.Sponsor.IsValid || request.Fixer == request.Client ||
            request.MinimumTrust < 0 || request.MinimumReputation < 0 ||
            request.ReputationAudienceKey.Length > 96 || request.RequiredObligations.Count > 16 ||
            request.RequiredObligations.Any(value => !value.IsValid) || request.Reason.Length > 128 ||
            request.CorrelationId == Guid.Empty || !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCFixerMutationStatus.Rejected, "The fixer access request is invalid.");
        if (!(await _parties.ResolveAsync(request.Fixer, true)).IsResolved ||
            !(await _parties.ResolveAsync(request.Client, true)).IsResolved ||
            !(await _parties.ResolveAsync(request.Sponsor, true)).IsResolved)
            return Reject(NCFixerMutationStatus.Rejected, "A fixer access party is unavailable.");
        var networkId = NCPersistentIdentity.FixerNetwork(request.Fixer);
        var gate = Gate(networkId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetNetworkAsync(networkId, true);
            var sponsorAuthorized = request.Sponsor == request.Fixer ||
                current?.Snapshot.Delegates.Contains(request.Sponsor) == true ||
                current?.Snapshot.Access.Any(value => value.Client == request.Sponsor &&
                    value.Status == NCFixerAccessStatus.Active &&
                    (value.ExpiresAt == null || value.ExpiresAt > now)) == true;
            if (!sponsorAuthorized)
                return Reject(NCFixerMutationStatus.NotAuthorized, "The sponsor cannot introduce this client.");
            var grant = new NCFixerAccessGrant(request.Client, request.Sponsor, request.MinimumTrust,
                request.MinimumReputation, request.ReputationAudienceKey.Trim(),
                request.RequiredObligations.Distinct().ToArray(), NCFixerAccessStatus.Active, now,
                request.ExpiresAt, request.Reason.Trim());
            if (!await SatisfiesGrantAsync(request.Fixer, grant, now, true))
                return Reject(NCFixerMutationStatus.NotAuthorized,
                    "The relationship, reputation, or obligation requirements are not satisfied.");
            var entries = current?.Snapshot.Access.Where(value => value.Client != request.Client).ToList() ?? [];
            if (entries.Count >= policy.MaximumContacts)
                return Reject(NCFixerMutationStatus.CapacityExceeded, "The fixer network is at capacity.");
            entries.Add(grant);
            entries.Sort((left, right) => string.CompareOrdinal(left.Client.ToString(), right.Client.ToString()));
            var eventId = Guid.NewGuid();
            var proposed = new NCFixerNetworkSnapshot(networkId, request.Fixer, entries,
                current?.Snapshot.Delegates ?? [], (current?.Snapshot.DataVersion ?? 0) + 1, eventId);
            var persisted = await Persist(request.CommandId, "living-city.fixer-network.grant", "fixer-network",
                networkId.ToString(), [Write(NetworkAggregateType, networkId.ToString(), current?.AggregateVersion ?? 0,
                    proposed, eventId, "living-city.fixer-network.access-granted", request.AuditSource)],
                request.CorrelationId, request.CausationId, epoch, now);
            return await CompleteNetwork(persisted, networkId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCFixerMutationResult> CreateDealAsync(NCCreateFixerDealRequest request)
    {
        if (!TryPolicy(out var policy) || !Valid(request, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(_clock.IsReady ? NCFixerMutationStatus.Rejected : NCFixerMutationStatus.ClockUnavailable,
                "The fixer deal request is invalid.");
        if (!(await _parties.ResolveAsync(request.Fixer, true)).IsResolved ||
            !(await _parties.ResolveAsync(request.Buyer, true)).IsResolved ||
            !(await _parties.ResolveAsync(request.Seller, true)).IsResolved ||
            !await HasAccessAsync(request.Fixer, request.Buyer, true))
            return Reject(NCFixerMutationStatus.NotAuthorized,
                "The parties or buyer access to this fixer network are unavailable.");
        var gate = Gate(request.DealId.Value);
        await gate.WaitAsync();
        try
        {
            var existing = await GetDealAsync(request.DealId, true);
            if (existing != null)
                return existing.Snapshot.NetworkId == request.NetworkId && existing.Snapshot.Buyer == request.Buyer &&
                    existing.Snapshot.Seller == request.Seller
                    ? new(NCFixerMutationStatus.Duplicate, null, existing, null, null)
                    : Reject(NCFixerMutationStatus.Rejected, "The deal identity belongs to different parties.");
            var network = await GetNetworkAsync(request.NetworkId, true);
            if (network?.Snapshot.Fixer != request.Fixer)
                return Reject(NCFixerMutationStatus.NotAuthorized, "The fixer network is unavailable.");
            var eventId = Guid.NewGuid();
            var snapshot = new NCFixerDealSnapshot(request.DealId, request.NetworkId, request.Fixer, request.Buyer,
                request.Seller, Normalize(request.Plan, policy), NCFixerDealStatus.Offered, request.QuotedAmount, default,
                null, null, FindRequest(request.Plan, service: false), FindRequest(request.Plan, service: true),
                request.Plan.Legs.Select(value => value.ShipmentId).FirstOrDefault(value => value?.IsValid == true),
                null, now.Advance(policy.QuoteLifetime), null, now, now, 1, eventId);
            var persisted = await Persist(request.CommandId, "living-city.fixer-deal.create", "fixer-deal",
                request.DealId.ToString(), [Write(DealAggregateType, request.DealId.ToString(), 0, snapshot, eventId,
                    "living-city.fixer-deal.offered", request.AuditSource)], request.CorrelationId,
                request.CausationId, epoch, now);
            return await CompleteDeal(persisted, request.DealId);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<NCFixerMutationResult> ChangeDealAsync(NCChangeFixerDealRequest request)
    {
        if (!TryPolicy(out var policy) || request.CommandId == Guid.Empty || !request.DealId.IsValid ||
            request.ExpectedVersion <= 0 || !request.Actor.IsValid || request.CorrelationId == Guid.Empty ||
            (request.Status == NCFixerDealStatus.Breached) !=
            (request.BreachReason != NCFixerBreachReason.None) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCFixerMutationStatus.Rejected, "The fixer deal transition request is invalid.");
        var gate = Gate(request.DealId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetDealAsync(request.DealId, true);
            if (current == null)
                return Reject(NCFixerMutationStatus.NotFound, "The fixer deal does not exist.");
            if (current.AggregateVersion != request.ExpectedVersion)
                return Reject(NCFixerMutationStatus.VersionConflict, "The fixer deal version changed.");
            if (!IsParty(current.Snapshot, request.Actor) || !Allowed(current.Snapshot.Status, request.Status))
                return Reject(NCFixerMutationStatus.NotAuthorized, "The actor or transition is not allowed.");
            var eventId = Guid.NewGuid();
            var proposed = current.Snapshot with
            {
                Status = request.Status,
                ReservationExpiresAt = request.Status == NCFixerDealStatus.Reserved
                    ? now.Advance(policy.ReservationLifetime)
                    : current.Snapshot.ReservationExpiresAt,
                ExecutionDeadline = request.Status == NCFixerDealStatus.InProgress
                    ? current.Snapshot.ExecutionDeadline ?? now.Advance(
                        current.Snapshot.Plan.ExecutionDuration > TimeSpan.Zero
                            ? current.Snapshot.Plan.ExecutionDuration
                            : policy.DefaultExecutionDuration)
                    : current.Snapshot.ExecutionDeadline,
                BreachedAt = request.Status == NCFixerDealStatus.Breached ? now : current.Snapshot.BreachedAt,
                BreachReason = request.Status == NCFixerDealStatus.Breached
                    ? request.BreachReason
                    : current.Snapshot.BreachReason,
                UpdatedAt = now,
                DataVersion = checked(current.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            return await PersistDealMutation(request.CommandId, current, proposed, eventId,
                $"living-city.fixer-deal.{request.Status.ToString().ToLowerInvariant()}", request.CorrelationId,
                request.CausationId, request.AuditSource, epoch, now);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<NCFixerMutationResult> AdvanceLegAsync(NCAdvanceFixerDealLegRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.DealId.IsValid || request.ExpectedVersion <= 0 ||
            request.LegId == Guid.Empty || !request.Actor.IsValid || request.CorrelationId == Guid.Empty ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCFixerMutationStatus.Rejected, "The deal leg request is invalid.");
        var gate = Gate(request.DealId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetDealAsync(request.DealId, true);
            if (current == null)
                return Reject(NCFixerMutationStatus.NotFound, "The fixer deal does not exist.");
            if (current.AggregateVersion != request.ExpectedVersion)
                return Reject(NCFixerMutationStatus.VersionConflict, "The fixer deal version changed.");
            if (!IsParty(current.Snapshot, request.Actor))
                return Reject(NCFixerMutationStatus.NotAuthorized, "The actor is not a deal participant.");
            var index = current.Snapshot.Plan.Legs.ToList().FindIndex(value => value.LegId == request.LegId);
            if (index < 0 || !Allowed(current.Snapshot.Plan.Legs[index].Status, request.Status) ||
                request.Status == NCFixerDealLegStatus.Delivered && request.ProofWorldEventId == null)
                return Reject(NCFixerMutationStatus.Rejected, "The deal leg transition is not allowed.");
            var legs = current.Snapshot.Plan.Legs.ToArray();
            legs[index] = legs[index] with { Status = request.Status, ProofWorldEventId = request.ProofWorldEventId };
            var eventId = Guid.NewGuid();
            var allDelivered = legs.All(value => value.Status is NCFixerDealLegStatus.Delivered or
                NCFixerDealLegStatus.Waived);
            var proposed = current.Snapshot with
            {
                Plan = current.Snapshot.Plan with { Legs = legs },
                Status = request.Status == NCFixerDealLegStatus.Failed
                    ? NCFixerDealStatus.Breached
                    : allDelivered ? NCFixerDealStatus.AwaitingHandoff : current.Snapshot.Status,
                BreachedAt = request.Status == NCFixerDealLegStatus.Failed ? now : current.Snapshot.BreachedAt,
                BreachReason = request.Status == NCFixerDealLegStatus.Failed
                    ? NCFixerBreachReason.RequiredLegFailed
                    : current.Snapshot.BreachReason,
                UpdatedAt = now,
                DataVersion = checked(current.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            return await PersistDealMutation(request.CommandId, current, proposed, eventId,
                "living-city.fixer-deal.leg-advanced", request.CorrelationId, request.CausationId,
                request.AuditSource, epoch, now);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<NCFixerMutationResult> OpenDisputeAsync(NCOpenFixerDisputeRequest request) =>
        MutateDisputeAsync(request.CommandId, request.DealId, request.ExpectedVersion, request.Actor,
            request.CorrelationId, request.CausationId, request.AuditSource, (current, now, policy) =>
            {
                if (request.DisputeId == Guid.Empty || string.IsNullOrWhiteSpace(request.ReasonKey) ||
                    request.ReasonKey.Length > 128 || current.Dispute != null ||
                    current.Status is not (NCFixerDealStatus.EscrowFunded or NCFixerDealStatus.InProgress or
                        NCFixerDealStatus.AwaitingHandoff or NCFixerDealStatus.Breached))
                    return null;
                return current with
                {
                    Status = NCFixerDealStatus.Disputed,
                    Dispute = new NCFixerDispute(request.DisputeId, request.Actor, request.ReasonKey.Trim(), [],
                        NCFixerArbitrationOutcome.None, default, string.Empty, now, null),
                };
            }, "living-city.fixer-deal.disputed");

    public Task<NCFixerMutationResult> SubmitEvidenceAsync(NCSubmitFixerEvidenceRequest request) =>
        MutateDisputeAsync(request.CommandId, request.DealId, request.ExpectedVersion, request.Actor,
            request.CorrelationId, request.CausationId, request.AuditSource, (current, _, policy) =>
            {
                if (current.Dispute == null || request.Evidence.EvidenceId == Guid.Empty ||
                    request.Evidence.SubmittedBy != request.Actor || request.Evidence.ReferenceId.Length > 128 ||
                    request.Evidence.SummaryKey.Length > 128 ||
                    current.Dispute.Evidence.Count >= policy.MaximumEvidence ||
                    current.Dispute.Evidence.Any(value => value.EvidenceId == request.Evidence.EvidenceId))
                    return null;
                return current with { Dispute = current.Dispute with
                    { Evidence = current.Dispute.Evidence.Append(request.Evidence).ToArray() } };
            }, "living-city.fixer-deal.evidence-submitted");

    public Task<NCFixerMutationResult> ArbitrateAsync(NCArbitrateFixerDisputeRequest request) =>
        MutateDisputeAsync(request.CommandId, request.DealId, request.ExpectedVersion, request.Arbitrator,
            request.CorrelationId, request.CausationId, request.AuditSource, (current, now, _) =>
            {
                if (current.Dispute == null || request.Outcome == NCFixerArbitrationOutcome.None ||
                    string.IsNullOrWhiteSpace(request.ResolutionKey) || request.ResolutionKey.Length > 128 ||
                    request.Arbitrator != current.Fixer)
                    return null;
                return current with
                {
                    Status = NCFixerDealStatus.Arbitrated,
                    Dispute = current.Dispute with
                    {
                        Outcome = request.Outcome,
                        Arbitrator = request.Arbitrator,
                        ResolutionKey = request.ResolutionKey.Trim(),
                        ResolvedAt = now,
                    },
                };
            }, "living-city.fixer-deal.arbitrated");

    public async Task<NCFixerMutationResult> ScheduleNightMarketAsync(NCScheduleNightMarketRequest request)
    {
        if (!_prototypes.TryIndex(DefaultNightMarketPolicy, out NCNightMarketPolicyPrototype? policy) ||
            request.CommandId == Guid.Empty || !request.NightMarketId.IsValid ||
            request.TemplateId.Length > 96 ||
            !request.VenueZone.IsValid || !request.Organizer.IsValid || !request.SecurityProvider.IsValid ||
            request.Vendors.Count == 0 || request.Vendors.Count > policy.MaximumVendors ||
            request.Vendors.Select(value => value.Vendor).Distinct().Count() != request.Vendors.Count ||
            request.Vendors.Any(value => !value.Vendor.IsValid || !value.StockNodeId.IsValid ||
                !value.NetworkId.IsValid || value.Attendant.IsValid && !value.Attendant.TryGetActor(out _)) ||
            request.AssemblyAt >= request.OpensAt || request.OpensAt >= request.ClosesAt ||
            request.ClosesAt.Ticks - request.OpensAt.Ticks > policy.MaximumDuration.Ticks ||
            request.CorrelationId == Guid.Empty || !_clock.TryGetCurrentTime(out var epoch, out var now) ||
            request.AssemblyAt.Ticks - now.Ticks < policy.MinimumAssemblyLead.Ticks)
            return Reject(NCFixerMutationStatus.Rejected, "The Night Market schedule is invalid.");
        var resourceError = await ValidateNightMarketResourcesAsync(request.TemplateId, request.VenueZone,
            request.Organizer, request.SecurityProvider, request.Vendors, true);
        if (resourceError != null)
            return Reject(NCFixerMutationStatus.Rejected, resourceError);
        var gate = Gate(request.NightMarketId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetNightMarketAsync(request.NightMarketId, true);
            if (current != null)
                return new(NCFixerMutationStatus.Duplicate, null, null, current, null);
            var eventId = Guid.NewGuid();
            var snapshot = new NCNightMarketSnapshot(request.NightMarketId, request.TemplateId.Trim(),
                request.VenueZone, request.Organizer, request.SecurityProvider, request.Vendors,
                NCNightMarketStatus.Scheduled, request.AssemblyAt, request.OpensAt, request.ClosesAt,
                null, null, [], now, now, 1, eventId);
            var persisted = await Persist(request.CommandId, "living-city.night-market.schedule", "night-market",
                request.NightMarketId.ToString(), [Write(NightMarketAggregateType,
                    request.NightMarketId.ToString(), 0, snapshot, eventId,
                    "living-city.night-market.scheduled", request.AuditSource)], request.CorrelationId,
                request.CausationId, epoch, now);
            return await CompleteNightMarket(persisted, request.NightMarketId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCFixerMutationResult> AdvanceNightMarketAsync(NCNightMarketId id, long expectedVersion,
        NCNightMarketStatus status, Guid worldEventId, string auditSource)
    {
        if (!id.IsValid || expectedVersion <= 0 || worldEventId == Guid.Empty ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCFixerMutationStatus.Rejected, "The Night Market transition is invalid.");
        var gate = Gate(id.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetNightMarketAsync(id, true);
            if (current == null)
                return Reject(NCFixerMutationStatus.NotFound, "The Night Market does not exist.");
            if (current.AggregateVersion != expectedVersion)
                return Reject(NCFixerMutationStatus.VersionConflict, "The Night Market version changed.");
            if (!Allowed(current.Snapshot.Status, status))
                return Reject(NCFixerMutationStatus.Rejected, "The Night Market transition is not allowed.");
            if (status is NCNightMarketStatus.Assembling or NCNightMarketStatus.Open &&
                await ValidateNightMarketAsync(current.Snapshot, true) is { } resourceError)
                return Reject(NCFixerMutationStatus.Rejected, resourceError);
            var eventId = Guid.NewGuid();
            var proposed = current.Snapshot with
            {
                Status = status,
                OpeningWorldEventId = status == NCNightMarketStatus.Open ? worldEventId :
                    current.Snapshot.OpeningWorldEventId,
                ClosingWorldEventId = status == NCNightMarketStatus.Completed ? worldEventId :
                    current.Snapshot.ClosingWorldEventId,
                UpdatedAt = now,
                DataVersion = checked(current.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            var command = NCPersistentIdentity.Command("night-market", status.ToString(),
                $"{id}.{expectedVersion}");
            var persisted = await Persist(command, "living-city.night-market.advance", "night-market", id.ToString(),
                [Write(NightMarketAggregateType, id.ToString(), current.AggregateVersion, proposed, eventId,
                    $"living-city.night-market.{status.ToString().ToLowerInvariant()}", auditSource)],
                NCPersistentIdentity.Correlation("night-market", status.ToString(), $"{id}.{expectedVersion}"),
                current.Snapshot.LastAppliedEventId, epoch, now);
            return await CompleteNightMarket(persisted, id);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<NCFixerMutationResult> RecordNightMarketIncidentAsync(NCNightMarketId id, long expectedVersion,
        Guid worldEventId) => MutateNightMarketAsync(id, expectedVersion, "incident", (current, now, policy) =>
    {
        if (worldEventId == Guid.Empty || current.IncidentWorldEventIds.Contains(worldEventId) ||
            current.IncidentWorldEventIds.Count >= policy.MaximumIncidents)
            return null;
        return current with
        {
            IncidentWorldEventIds = current.IncidentWorldEventIds.Append(worldEventId).ToArray(),
            UpdatedAt = now,
        };
    });

    public Task<NCFixerMutationResult> SetVendorAttendanceAsync(NCNightMarketId id, long expectedVersion,
        NCPartyRef vendor, bool present) => MutateNightMarketAsync(id, expectedVersion,
        present ? "vendor-arrived" : "vendor-departed", (current, now, _) =>
    {
        var vendors = current.Vendors.ToArray();
        var index = Array.FindIndex(vendors, value => value.Vendor == vendor);
        if (index < 0 || present && vendors[index].ArrivedAt != null || !present && vendors[index].DepartedAt != null)
            return null;
        vendors[index] = vendors[index] with
        {
            Confirmed = true,
            ArrivedAt = present ? now : vendors[index].ArrivedAt,
            DepartedAt = present ? vendors[index].DepartedAt : now,
        };
        return current with { Vendors = vendors, UpdatedAt = now };
    });

    public Task<string?> ValidateNightMarketAsync(NCNightMarketSnapshot market, bool forceReload = false) =>
        ValidateNightMarketResourcesAsync(market.TemplateId, market.VenueZone, market.Organizer,
            market.SecurityProvider, market.Vendors, forceReload);

    private async Task<string?> ValidateNightMarketResourcesAsync(string templateId,
        Content.Shared._NC.City.Zones.NCZoneId venueZone, NCPartyRef organizer, NCPartyRef securityProvider,
        IReadOnlyList<NCNightMarketVendor> vendors, bool forceReload)
    {
        if (!_prototypes.TryIndex(DefaultNightMarketPolicy, out NCNightMarketPolicyPrototype? policy) ||
            policy.AllowedVenueAffordances.Count == 0 ||
            !policy.AllowedVenueAffordances.Any(kind => _affordances.TrySelect(kind, venueZone, 0, out _)))
            return "The Night Market venue is not an authored shop or warehouse building.";
        if (!string.IsNullOrWhiteSpace(templateId) &&
            !_prototypes.HasIndex(new ProtoId<NCZoneTemplatePrototype>(templateId.Trim())))
            return "The requested Night Market venue template is unavailable.";
        if (!(await _parties.ResolveAsync(organizer, forceReload)).IsResolved ||
            !(await _parties.ResolveAsync(securityProvider, forceReload)).IsResolved ||
            !organizer.TryGetActor(out _) || !securityProvider.TryGetActor(out _))
            return "The Night Market organizer or security provider is not an available persistent actor.";
        if (await GetNetworkAsync(NCPersistentIdentity.FixerNetwork(organizer), forceReload) is not { } organizerNetwork ||
            organizerNetwork.Snapshot.Fixer != organizer)
            return "The Night Market organizer has no Fixer network.";

        foreach (var vendor in vendors)
        {
            var attendant = vendor.Attendant.IsValid ? vendor.Attendant : vendor.Vendor;
            if (!attendant.TryGetActor(out _) ||
                !(await _parties.ResolveAsync(vendor.Vendor, forceReload)).IsResolved ||
                !(await _parties.ResolveAsync(attendant, forceReload)).IsResolved)
                return $"Night Market vendor {vendor.Vendor} has no available persistent actor attendant.";
            var network = await GetNetworkAsync(vendor.NetworkId, forceReload);
            if (network == null || network.Snapshot.Fixer != attendant &&
                !await HasAccessAsync(network.Snapshot.Fixer, attendant, forceReload))
                return $"Night Market vendor attendant {attendant} has no access to the declared Fixer network.";
            var stock = await _assets.GetStockNodeAsync(vendor.StockNodeId, forceReload);
            if (stock?.Snapshot.Owner != vendor.Vendor ||
                stock.Snapshot.CommodityBatchIds.Count + stock.Snapshot.ItemIds.Count == 0)
                return $"Night Market vendor {vendor.Vendor} has no real stock in the declared stock node.";
        }

        return null;
    }

    private async Task<NCFixerMutationResult> MutateNightMarketAsync(NCNightMarketId id, long expectedVersion,
        string operation, Func<NCNightMarketSnapshot, NCActiveWorldTime, NCNightMarketPolicyPrototype,
            NCNightMarketSnapshot?> mutate)
    {
        if (!id.IsValid || expectedVersion <= 0 ||
            !_prototypes.TryIndex(DefaultNightMarketPolicy, out NCNightMarketPolicyPrototype? policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCFixerMutationStatus.Rejected, "The Night Market mutation is invalid.");
        var gate = Gate(id.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetNightMarketAsync(id, true);
            if (current == null)
                return Reject(NCFixerMutationStatus.NotFound, "The Night Market does not exist.");
            if (current.AggregateVersion != expectedVersion)
                return Reject(NCFixerMutationStatus.VersionConflict, "The Night Market version changed.");
            var proposed = mutate(current.Snapshot, now, policy);
            if (proposed == null)
                return Reject(NCFixerMutationStatus.Rejected, "The Night Market mutation is not allowed.");
            var eventId = Guid.NewGuid();
            proposed = proposed with { DataVersion = current.Snapshot.DataVersion + 1,
                LastAppliedEventId = eventId };
            var stable = $"{id}.{expectedVersion}";
            var persisted = await Persist(NCPersistentIdentity.Command("night-market", operation, stable),
                "living-city.night-market.update", "night-market", id.ToString(),
                [Write(NightMarketAggregateType, id.ToString(), current.AggregateVersion, proposed, eventId,
                    $"living-city.night-market.{operation}", $"night-market-{operation}")],
                NCPersistentIdentity.Correlation("night-market", operation, stable),
                current.Snapshot.LastAppliedEventId, epoch, now);
            return await CompleteNightMarket(persisted, id);
        }
        finally
        {
            gate.Release();
        }
    }

    public bool TryPrepareDealParticipant(NCFixerDealRecord current, NCFixerDealSnapshot proposed,
        string eventType, string auditSource, out NCAtomicLedgerParticipantWrite? participant)
    {
        participant = null;
        if (!Valid(proposed, current.Snapshot.DealId) || proposed.DataVersion != current.Snapshot.DataVersion + 1)
            return false;
        var eventId = proposed.LastAppliedEventId;
        participant = new NCAtomicLedgerParticipantWrite(eventId, eventType, DealAggregateType,
            proposed.DealId.ToString(), current.AggregateVersion, SnapshotFormatVersion,
            JsonSerializer.Serialize(proposed, JsonOptions), JsonSerializer.Serialize(new FixerEvent(
                proposed.DealId.ToString(), auditSource), JsonOptions));
        return true;
    }

    private async Task<NCFixerMutationResult> MutateDisputeAsync(Guid commandId, NCFixerDealId dealId,
        long expectedVersion, NCPartyRef actor, Guid correlationId, Guid? causationId, string auditSource,
        Func<NCFixerDealSnapshot, NCActiveWorldTime, NCFixerPolicyPrototype, NCFixerDealSnapshot?> mutate,
        string eventType)
    {
        if (!TryPolicy(out var policy) || commandId == Guid.Empty || !dealId.IsValid || expectedVersion <= 0 ||
            !actor.IsValid || correlationId == Guid.Empty || !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(NCFixerMutationStatus.Rejected, "The dispute request is invalid.");
        var gate = Gate(dealId.Value);
        await gate.WaitAsync();
        try
        {
            var current = await GetDealAsync(dealId, true);
            if (current == null)
                return Reject(NCFixerMutationStatus.NotFound, "The deal does not exist.");
            if (current.AggregateVersion != expectedVersion)
                return Reject(NCFixerMutationStatus.VersionConflict, "The deal version changed.");
            if (!IsParty(current.Snapshot, actor))
                return Reject(NCFixerMutationStatus.NotAuthorized, "The actor is not a deal participant.");
            var proposed = mutate(current.Snapshot, now, policy);
            if (proposed == null)
                return Reject(NCFixerMutationStatus.Rejected, "The dispute mutation is not allowed.");
            var eventId = Guid.NewGuid();
            proposed = proposed with { UpdatedAt = now, DataVersion = current.Snapshot.DataVersion + 1,
                LastAppliedEventId = eventId };
            return await PersistDealMutation(commandId, current, proposed, eventId, eventType, correlationId,
                causationId, auditSource, epoch, now);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> SatisfiesGrantAsync(NCPartyRef fixer, NCFixerAccessGrant grant,
        NCActiveWorldTime now, bool forceReload)
    {
        if (grant.Status != NCFixerAccessStatus.Active || grant.ExpiresAt is { } expiry && expiry <= now)
            return false;
        var relationship = await _relationships.GetAsync(fixer, grant.Client, forceReload);
        if (grant.MinimumTrust > 0 && (relationship?.Snapshot.Trust ?? 0) < grant.MinimumTrust)
            return false;
        if (grant.MinimumReputation > 0)
        {
            var reputation = await _reputations.GetAsync(grant.Client, forceReload);
            var score = reputation?.Snapshot.Entries.Where(value =>
                value.Audience.DomainKey == grant.ReputationAudienceKey).Select(value => value.Score)
                .DefaultIfEmpty(0).Max() ?? 0;
            if (score < grant.MinimumReputation)
                return false;
        }
        foreach (var id in grant.RequiredObligations)
        {
            var obligation = await _obligations.GetAsync(id, forceReload);
            if (obligation?.Snapshot.Status != NCSocialObligationStatus.Active ||
                obligation.Snapshot.Debtor != grant.Client || obligation.Snapshot.Creditor != fixer)
                return false;
        }
        return true;
    }

    private async Task<NCFixerMutationResult> PersistDealMutation(Guid commandId, NCFixerDealRecord current,
        NCFixerDealSnapshot proposed, Guid eventId, string eventType, Guid correlationId, Guid? causationId,
        string auditSource, NCSessionEpoch epoch, NCActiveWorldTime now)
    {
        var persisted = await Persist(commandId, "living-city.fixer-deal.update", "fixer-deal",
            current.Snapshot.DealId.ToString(), [Write(DealAggregateType, current.Snapshot.DealId.ToString(),
                current.AggregateVersion, proposed, eventId, eventType, auditSource)], correlationId, causationId,
            epoch, now);
        return await CompleteDeal(persisted, current.Snapshot.DealId);
    }

    private Task<TRecord?> GetAsync<TId, TRecord>(TId id, string aggregateType,
        ConcurrentDictionary<TId, TRecord> cache, Func<NCAggregateSnapshotData?, TId, TRecord?> deserialize,
        bool forceReload) where TId : notnull where TRecord : class
    {
        if (!forceReload && cache.TryGetValue(id, out var cached))
            return Task.FromResult<TRecord?>(cached);
        return LoadAsync(id, aggregateType, cache, deserialize);
    }

    private async Task<TRecord?> LoadAsync<TId, TRecord>(TId id, string aggregateType,
        ConcurrentDictionary<TId, TRecord> cache, Func<NCAggregateSnapshotData?, TId, TRecord?> deserialize)
        where TId : notnull where TRecord : class
    {
        var stored = await _database.GetNCAggregateSnapshotAsync(aggregateType, id.ToString()!);
        var loaded = deserialize(stored, id);
        if (loaded != null)
            cache[id] = loaded;
        return loaded;
    }

    private Task<NCPersistentBatchMutationResult> Persist(Guid commandId, string commandType, string scopeType,
        string scopeId, IReadOnlyList<NCPersistentAggregateWrite> writes, Guid correlationId, Guid? causationId,
        NCSessionEpoch epoch, NCActiveWorldTime now) => _database.ApplyNCPersistentBatchMutationAsync(
        new NCPersistentBatchMutationRequest(commandId, commandType, scopeType, scopeId, writes,
            JsonSerializer.Serialize(new FixerReceipt(scopeId), JsonOptions), correlationId, causationId,
            epoch, now, []));

    private static NCPersistentAggregateWrite Write<T>(string aggregateType, string aggregateId,
        long expectedVersion, T snapshot, Guid eventId, string eventType, string auditSource) => new(eventId,
        eventType, aggregateType, aggregateId, expectedVersion, SnapshotFormatVersion,
        JsonSerializer.Serialize(snapshot, JsonOptions),
        JsonSerializer.Serialize(new FixerEvent(aggregateId, auditSource), JsonOptions));

    private async Task<NCFixerMutationResult> CompleteNetwork(NCPersistentBatchMutationResult result,
        NCFixerNetworkId id)
    {
        if (!Committed(result, out var status))
            return Reject(Map(result.Status), result.Error ?? "The fixer network mutation failed.");
        var record = await GetNetworkAsync(id, true);
        if (record != null)
            Publish(new NCFixerAggregateChangedEvent(NetworkAggregateType, id.ToString()));
        return record == null ? Reject(NCFixerMutationStatus.StorageFailure, "The fixer network could not reload.") :
            new(status, record, null, null, null);
    }

    private async Task<NCFixerMutationResult> CompleteDeal(NCPersistentBatchMutationResult result, NCFixerDealId id)
    {
        if (!Committed(result, out var status))
            return Reject(Map(result.Status), result.Error ?? "The fixer deal mutation failed.");
        var record = await GetDealAsync(id, true);
        if (record != null)
            Publish(new NCFixerAggregateChangedEvent(DealAggregateType, id.ToString()));
        return record == null ? Reject(NCFixerMutationStatus.StorageFailure, "The fixer deal could not reload.") :
            new(status, null, record, null, null);
    }

    private async Task<NCFixerMutationResult> CompleteNightMarket(NCPersistentBatchMutationResult result,
        NCNightMarketId id)
    {
        if (!Committed(result, out var status))
            return Reject(Map(result.Status), result.Error ?? "The Night Market mutation failed.");
        var record = await GetNightMarketAsync(id, true);
        if (record != null)
            Publish(new NCFixerAggregateChangedEvent(NightMarketAggregateType, id.ToString()));
        return record == null ? Reject(NCFixerMutationStatus.StorageFailure, "The Night Market could not reload.") :
            new(status, null, null, record, null);
    }

    private void Publish(NCFixerAggregateChangedEvent args) =>
        _tasks.RunOnMainThread(() => RaiseLocalEvent(args));

    private bool TryPolicy(out NCFixerPolicyPrototype policy) => _prototypes.TryIndex(DefaultPolicy, out policy!) &&
        policy.MaximumContacts is > 0 and <= 4096 && policy.MaximumDealLegs is > 0 and <= 24 &&
        policy.MaximumEvidence is > 0 and <= 128 && policy.QuoteLifetime > TimeSpan.Zero &&
        policy.ReservationLifetime > TimeSpan.Zero && policy.DefaultExecutionDuration > TimeSpan.Zero &&
        policy.MaximumExecutionDuration >= policy.DefaultExecutionDuration &&
        policy.MaximumBrokerFeeBasisPoints is >= 0 and <= 10_000 &&
        policy.DealBoardBrokerFeeBasisPoints is >= 0 and <= 10_000 &&
        policy.DealBoardListingLifetime > TimeSpan.Zero &&
        policy.DealBoardServiceOffers.Count <= 32 &&
        policy.DealBoardServiceOffers.All(value => _prototypes.HasIndex(value)) &&
        Valid(DefaultConsequences(policy));

    private bool Valid(NCCreateFixerDealRequest request, NCFixerPolicyPrototype policy) =>
        request.CommandId != Guid.Empty && request.DealId.IsValid && request.NetworkId.IsValid &&
        request.Fixer.IsValid && request.Buyer.IsValid && request.Seller.IsValid && request.Buyer != request.Seller &&
        request.Fixer != request.Buyer &&
        request.QuotedAmount >= 0 && request.CorrelationId != Guid.Empty && request.Plan.Legs.Count is > 0 &&
        request.Plan.Legs.Count <= policy.MaximumDealLegs && ValidEscrowTotal(request.QuotedAmount,
            request.Plan.BrokerFeeBasisPoints, request.Plan.EscrowAmount) &&
        request.Plan.BrokerFeeBasisPoints is >= 0 && request.Plan.BrokerFeeBasisPoints <=
            policy.MaximumBrokerFeeBasisPoints && request.Plan.RiskBasisPoints is >= 0 &&
        request.Plan.RiskBasisPoints <= policy.MaximumRiskBasisPoints &&
        (request.Plan.ExecutionDuration == TimeSpan.Zero || request.Plan.ExecutionDuration > TimeSpan.Zero &&
            request.Plan.ExecutionDuration <= policy.MaximumExecutionDuration) &&
        (request.Plan.Consequences == null || Valid(request.Plan.Consequences)) &&
        request.Plan.Legs.All(Valid);

    private static bool Valid(NCFixerDealLeg value) => value.LegId != Guid.Empty && value.From.IsValid &&
        value.To.IsValid && value.From != value.To && value.DefinitionId.Length is > 0 and <= 128 &&
        value.Quantity >= 0 && value.MoneyAmount >= 0 && Enum.IsDefined(value.Kind) &&
        value.Status == NCFixerDealLegStatus.Planned &&
        (!value.HandoffZone.IsValid || value.HandoffKind != null) &&
        (value.Kind == NCFixerDealLegKind.Obligation) == (value.ObligationKind != null);

    private static bool ValidEscrowTotal(long quotedAmount, int feeBasisPoints, long escrowAmount)
    {
        if (quotedAmount < 0 || feeBasisPoints < 0 || escrowAmount < 0)
            return false;
        var total = decimal.Ceiling((decimal) quotedAmount +
            (decimal) quotedAmount * feeBasisPoints / 10_000m);
        return total <= long.MaxValue && escrowAmount == (long) total;
    }

    private static bool Valid(NCFixerDealSnapshot value, NCFixerDealId expected) => value.DealId == expected &&
        value.NetworkId.IsValid && value.Fixer.IsValid && value.Buyer.IsValid && value.Seller.IsValid &&
        value.DataVersion > 0 && value.LastAppliedEventId != Guid.Empty;

    private static NCFixerDealPlan Normalize(NCFixerDealPlan plan, NCFixerPolicyPrototype policy) => plan with
    {
        Legs = plan.Legs.OrderBy(value => value.LegId).ToArray(),
        ExecutionDuration = plan.ExecutionDuration > TimeSpan.Zero
            ? plan.ExecutionDuration
            : policy.DefaultExecutionDuration,
        Consequences = plan.Consequences ?? DefaultConsequences(policy),
    };

    private static NCFixerDealConsequences DefaultConsequences(NCFixerPolicyPrototype policy) => new(
        policy.SuccessRelationshipEffectId,
        policy.BreachRelationshipEffectId,
        policy.SuccessReputationEffectId,
        policy.BreachReputationEffectId,
        policy.ReputationDomainId);

    private bool Valid(NCFixerDealConsequences value) =>
        Stable(value.SuccessRelationshipEffectId, 96) && Stable(value.BreachRelationshipEffectId, 96) &&
        Stable(value.SuccessReputationEffectId, 96) && Stable(value.BreachReputationEffectId, 96) &&
        Stable(value.ReputationDomainId, 96) &&
        _prototypes.HasIndex(new ProtoId<NCRelationshipEffectPrototype>(value.SuccessRelationshipEffectId)) &&
        _prototypes.HasIndex(new ProtoId<NCRelationshipEffectPrototype>(value.BreachRelationshipEffectId)) &&
        _prototypes.HasIndex(new ProtoId<NCReputationEffectPrototype>(value.SuccessReputationEffectId)) &&
        _prototypes.HasIndex(new ProtoId<NCReputationEffectPrototype>(value.BreachReputationEffectId)) &&
               _prototypes.HasIndex(new ProtoId<NCReputationDomainPrototype>(value.ReputationDomainId));

    private static bool Stable(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && value == value.Trim();

    private static NCMarketRequestId? FindRequest(NCFixerDealPlan plan, bool service) => plan.Legs
        .Where(value => service ? value.Kind == NCFixerDealLegKind.Service : value.Kind != NCFixerDealLegKind.Service)
        .Select(value => value.MarketRequestId).FirstOrDefault(value => value?.IsValid == true);

    private static bool IsParty(NCFixerDealSnapshot deal, NCPartyRef actor) =>
        actor == deal.Fixer || actor == deal.Buyer || actor == deal.Seller;

    private static bool Allowed(NCFixerDealStatus from, NCFixerDealStatus to) => (from, to) switch
    {
        (NCFixerDealStatus.Offered, NCFixerDealStatus.Quoted or NCFixerDealStatus.Cancelled or
            NCFixerDealStatus.Expired) => true,
        (NCFixerDealStatus.Quoted, NCFixerDealStatus.Reserved or NCFixerDealStatus.Cancelled or
            NCFixerDealStatus.Expired) => true,
        (NCFixerDealStatus.Reserved, NCFixerDealStatus.EscrowFunded or NCFixerDealStatus.Cancelled or
            NCFixerDealStatus.Expired) => true,
        (NCFixerDealStatus.EscrowFunded, NCFixerDealStatus.InProgress or NCFixerDealStatus.Disputed) => true,
        (NCFixerDealStatus.InProgress, NCFixerDealStatus.AwaitingHandoff or NCFixerDealStatus.Disputed) => true,
        (NCFixerDealStatus.InProgress, NCFixerDealStatus.Breached) => true,
        (NCFixerDealStatus.AwaitingHandoff, NCFixerDealStatus.Fulfilled or NCFixerDealStatus.Disputed) => true,
        (NCFixerDealStatus.Disputed, NCFixerDealStatus.Arbitrated) => true,
        (NCFixerDealStatus.Arbitrated, NCFixerDealStatus.Fulfilled or NCFixerDealStatus.Cancelled) => true,
        _ => false,
    };

    private static bool Allowed(NCFixerDealLegStatus from, NCFixerDealLegStatus to) => (from, to) switch
    {
        (NCFixerDealLegStatus.Planned, NCFixerDealLegStatus.Ready or NCFixerDealLegStatus.Waived) => true,
        (NCFixerDealLegStatus.Ready, NCFixerDealLegStatus.InProgress or NCFixerDealLegStatus.Failed or
            NCFixerDealLegStatus.Waived) => true,
        (NCFixerDealLegStatus.InProgress, NCFixerDealLegStatus.Delivered or NCFixerDealLegStatus.Failed) => true,
        (NCFixerDealLegStatus.Failed, NCFixerDealLegStatus.Ready or NCFixerDealLegStatus.Waived) => true,
        _ => false,
    };

    private static bool Allowed(NCNightMarketStatus from, NCNightMarketStatus to) => (from, to) switch
    {
        (NCNightMarketStatus.Scheduled, NCNightMarketStatus.Assembling or NCNightMarketStatus.Cancelled) => true,
        (NCNightMarketStatus.Assembling, NCNightMarketStatus.Open or NCNightMarketStatus.Cancelled) => true,
        (NCNightMarketStatus.Open, NCNightMarketStatus.Closing) => true,
        (NCNightMarketStatus.Closing, NCNightMarketStatus.Completed) => true,
        _ => false,
    };

    private static bool Committed(NCPersistentBatchMutationResult result, out NCFixerMutationStatus status)
    {
        status = result.Status == NCPersistentMutationStatus.Applied
            ? NCFixerMutationStatus.Applied
            : NCFixerMutationStatus.Duplicate;
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
    }

    private static NCFixerMutationStatus Map(NCPersistentMutationStatus status) => status switch
    {
        NCPersistentMutationStatus.VersionConflict => NCFixerMutationStatus.VersionConflict,
        NCPersistentMutationStatus.ClockRejected => NCFixerMutationStatus.ClockUnavailable,
        _ => NCFixerMutationStatus.StorageFailure,
    };

    private static NCFixerNetworkRecord? DeserializeNetwork(NCAggregateSnapshotData? stored, NCFixerNetworkId id) =>
        Deserialize<NCFixerNetworkSnapshot, NCFixerNetworkRecord>(stored, value => value.NetworkId == id,
            value => new NCFixerNetworkRecord(value,
            stored!.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime));
    private static NCFixerDealRecord? DeserializeDeal(NCAggregateSnapshotData? stored, NCFixerDealId id) =>
        Deserialize<NCFixerDealSnapshot, NCFixerDealRecord>(stored, value => Valid(value, id),
            value => new NCFixerDealRecord(value,
            stored!.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime));
    private static NCNightMarketRecord? DeserializeNightMarket(NCAggregateSnapshotData? stored, NCNightMarketId id) =>
        Deserialize<NCNightMarketSnapshot, NCNightMarketRecord>(stored, value => value.NightMarketId == id,
            value => new NCNightMarketRecord(value,
            stored!.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime));

    private static TRecord? Deserialize<TSnapshot, TRecord>(NCAggregateSnapshotData? stored,
        Func<TSnapshot, bool> valid, Func<TSnapshot, TRecord> create) where TRecord : class
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<TSnapshot>(stored.Payload, JsonOptions);
            return snapshot != null && valid(snapshot) ? create(snapshot) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SemaphoreSlim Gate(Guid id) => _gates[(id.GetHashCode() & int.MaxValue) % _gates.Length];
    private static NCFixerMutationResult Reject(NCFixerMutationStatus status, string error) =>
        new(status, null, null, null, error);
    private sealed record FixerReceipt(string SubjectId);
    private sealed record FixerEvent(string SubjectId, string AuditSource);
}

public sealed class NCFixerAggregateChangedEvent(string aggregateType, string aggregateId) : EntityEventArgs
{
    public string AggregateType { get; } = aggregateType;
    public string AggregateId { get; } = aggregateId;
}
