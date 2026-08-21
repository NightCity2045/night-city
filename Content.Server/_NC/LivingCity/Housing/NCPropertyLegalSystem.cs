// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Housing.Prototypes;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Housing;

public sealed record NCPropertyLegalMutationResult(
    NCPropertyAggregateMutationStatus Status,
    NCPropertyAggregateRecord? Property,
    NCEconomyMutationResult? Economy,
    string? Error);

/// <summary>
/// Applies legal holds, condemnation and destruction through explicit city-service authority. Compensation, when
/// configured, commits atomically and must come from an actually funded service account.
/// </summary>
public sealed partial class NCPropertyLegalSystem : EntitySystem
{
    private static readonly ProtoId<NCPropertyLegalPolicyPrototype> DefaultPolicy = "Default";

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCPropertyRegistrySystem _properties = default!;
    [Dependency] private NCHousingServiceSystem _housing = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;

    public Task<NCPropertyLegalMutationResult> PlaceHoldAsync(
        NCApartmentId propertyId,
        long expectedVersion,
        string reason) => ChangeWithoutMoneyAsync(propertyId, expectedVersion,
        NCPropertyLegalState.Hold, NCPropertyStatus.LegalHold, "hold", reason);

    public Task<NCPropertyLegalMutationResult> ReleaseHoldAsync(
        NCApartmentId propertyId,
        long expectedVersion,
        string reason) => ChangeWithoutMoneyAsync(propertyId, expectedVersion,
        NCPropertyLegalState.Clear, NCPropertyStatus.Available, "hold-released", reason);

    public async Task<NCPropertyLegalMutationResult> CondemnAsync(
        NCApartmentId propertyId,
        long expectedVersion,
        string reason)
    {
        if (!TryPolicy(out var policy) || !Valid(propertyId, expectedVersion, reason) ||
            !await _housing.EnsureReadyAsync("property-condemnation"))
            return Reject(NCPropertyAggregateMutationStatus.Rejected,
                "The condemnation request, policy, or city service is unavailable.");
        var property = await _properties.GetAsync(propertyId, true);
        if (property == null)
            return Reject(NCPropertyAggregateMutationStatus.NotFound, "The property does not exist.");
        if (property.AggregateVersion != expectedVersion)
            return Reject(NCPropertyAggregateMutationStatus.VersionConflict, "The property version changed.", property);
        if (property.Snapshot.LegalState is NCPropertyLegalState.Condemned or NCPropertyLegalState.Destroyed)
            return Reject(NCPropertyAggregateMutationStatus.Rejected, "The property is already terminal.", property);
        var proposed = property.Snapshot with
        {
            LegalState = NCPropertyLegalState.Condemned,
            Status = NCPropertyStatus.Condemned,
            Occupants = [],
            AccessGrants = [],
            AccessRevision = checked(property.Snapshot.AccessRevision + 1),
            NextRentDueAt = null,
            DelinquentSince = null,
            EvictionAt = null,
            SecurityDepositAmount = 0,
            SecurityDepositAccountId = default,
        };
        var compensation = checked(property.Snapshot.PurchasePrice *
            policy.CondemnationCompensationBasisPoints / 10_000);
        var stable = $"{propertyId}.{expectedVersion}";
        var deposit = property.Snapshot.SecurityDepositAmount;
        if (compensation == 0 && deposit == 0 ||
            compensation > 0 && property.Snapshot.Owner == _housing.Party && deposit == 0)
            return FromMutation(await _properties.UpdateAsync(new NCUpdatePropertyRequest(
                NCPersistentIdentity.Command("housing-legal", "condemn", stable), expectedVersion, proposed,
                "living-city.property.legal-condemned", _housing.Party,
                NCPersistentIdentity.Correlation("housing-legal", "condemn", stable),
                property.Snapshot.LastAppliedEventId, $"property-condemnation:{reason}")));
        var legs = new List<NCLedgerLegRequest>();
        if (compensation > 0 && property.Snapshot.Owner != _housing.Party)
        {
            var payer = await _finance.EnsurePrimaryAccountAsync(_housing.Party, "property-condemnation");
            var payee = await _finance.EnsurePrimaryAccountAsync(property.Snapshot.Owner, "property-condemnation");
            if (!payer.IsResolved || !payee.IsResolved)
                return Reject(NCPropertyAggregateMutationStatus.StorageFailure,
                    "A condemnation compensation account is unavailable.", property);
            legs.Add(new NCLedgerLegRequest(payer.AccountId, payer.Account!.AggregateVersion, -compensation));
            legs.Add(new NCLedgerLegRequest(payee.AccountId, payee.Account!.AggregateVersion, compensation));
        }
        if (deposit > 0)
        {
            var tenantParty = property.Snapshot.Occupants.FirstOrDefault(value =>
                value.Kind == NCPropertyOccupancyKind.Tenancy)?.Occupant;
            var escrow = await _ledger.GetAccountAsync(property.Snapshot.SecurityDepositAccountId, true);
            var tenant = tenantParty is { } resolvedTenant
                ? await _finance.EnsurePrimaryAccountAsync(resolvedTenant, "property-condemnation")
                : null;
            if (tenant == null || !tenant.IsResolved || escrow == null || escrow.Snapshot.Balance < deposit)
                return Reject(NCPropertyAggregateMutationStatus.StorageFailure,
                    "The condemned lease deposit cannot be refunded safely.", property);
            legs.Add(new NCLedgerLegRequest(escrow.Snapshot.AccountId, escrow.AggregateVersion, -deposit));
            legs.Add(new NCLedgerLegRequest(tenant.AccountId, tenant.Account!.AggregateVersion, deposit));
        }
        if (legs.Count == 0)
            return FromMutation(await _properties.UpdateAsync(new NCUpdatePropertyRequest(
                NCPersistentIdentity.Command("housing-legal", "condemn", stable), expectedVersion, proposed,
                "living-city.property.legal-condemned", _housing.Party,
                NCPersistentIdentity.Correlation("housing-legal", "condemn", stable),
                property.Snapshot.LastAppliedEventId, $"property-condemnation:{reason}")));
        var transactionId = NCPersistentIdentity.Transaction("housing-legal", "condemn", stable);
        if (!_properties.TryPrepareSettlementWrite(property, proposed, transactionId,
                "living-city.property.legal-condemned", $"property-condemnation:{reason}", out var participant,
                out _, out var error))
            return Reject(NCPropertyAggregateMutationStatus.Rejected,
                error ?? "The condemned property state is invalid.", property);
        var economy = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
            NCPersistentIdentity.Command("housing-legal", "condemn", stable), transactionId,
            NCLedgerTransactionKind.Adjustment, $"property condemnation {propertyId}",
            legs.ToArray(), _housing.Party, NCPersistentIdentity.Correlation("housing-legal", "condemn", stable),
            property.Snapshot.LastAppliedEventId, $"property-condemnation:{reason}", [participant!]));
        if (economy.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
            return Reject(economy.Status == NCEconomyMutationStatus.VersionConflict
                    ? NCPropertyAggregateMutationStatus.VersionConflict
                    : NCPropertyAggregateMutationStatus.StorageFailure,
                economy.Error ?? "Condemnation compensation was not committed.", property, economy);
        var loaded = await _properties.GetAsync(propertyId, true);
        return loaded == null
            ? Reject(NCPropertyAggregateMutationStatus.StorageFailure,
                "The condemned property could not be reloaded.", null, economy)
            : new(economy.Status == NCEconomyMutationStatus.Applied
                ? NCPropertyAggregateMutationStatus.Applied
                : NCPropertyAggregateMutationStatus.Duplicate, loaded, economy, null);
    }

    public Task<NCPropertyLegalMutationResult> DestroyAsync(
        NCApartmentId propertyId,
        long expectedVersion,
        string reason) => ChangeWithoutMoneyAsync(propertyId, expectedVersion,
        NCPropertyLegalState.Destroyed, NCPropertyStatus.Destroyed, "destroyed", reason);

    private async Task<NCPropertyLegalMutationResult> ChangeWithoutMoneyAsync(
        NCApartmentId propertyId,
        long expectedVersion,
        NCPropertyLegalState legalState,
        NCPropertyStatus status,
        string operation,
        string reason)
    {
        if (!Valid(propertyId, expectedVersion, reason) || !await _housing.EnsureReadyAsync("property-legal"))
            return Reject(NCPropertyAggregateMutationStatus.Rejected, "The property legal request is invalid.");
        var current = await _properties.GetAsync(propertyId, true);
        if (current == null)
            return Reject(NCPropertyAggregateMutationStatus.NotFound, "The property does not exist.");
        if (current.AggregateVersion != expectedVersion)
            return Reject(NCPropertyAggregateMutationStatus.VersionConflict, "The property version changed.", current);
        if (!Allowed(current.Snapshot.LegalState, legalState))
            return Reject(NCPropertyAggregateMutationStatus.Rejected,
                "The property legal transition is not allowed.", current);
        var terminal = legalState == NCPropertyLegalState.Destroyed;
        var resolvedStatus = legalState == NCPropertyLegalState.Clear
            ? ResolveTenureStatus(current.Snapshot)
            : status;
        var proposed = current.Snapshot with
        {
            LegalState = legalState,
            Status = resolvedStatus,
            Occupants = terminal ? [] : current.Snapshot.Occupants,
            AccessGrants = terminal ? [] : current.Snapshot.AccessGrants,
            AccessRevision = terminal ? checked(current.Snapshot.AccessRevision + 1) : current.Snapshot.AccessRevision,
            NextRentDueAt = terminal ? null : current.Snapshot.NextRentDueAt,
            DelinquentSince = terminal ? null : current.Snapshot.DelinquentSince,
            EvictionAt = terminal ? null : current.Snapshot.EvictionAt,
            SecurityDepositAmount = terminal ? 0 : current.Snapshot.SecurityDepositAmount,
            SecurityDepositAccountId = terminal ? default : current.Snapshot.SecurityDepositAccountId,
            FixtureItemIds = terminal ? [] : current.Snapshot.FixtureItemIds,
            IncludedAssetIds = terminal ? [] : current.Snapshot.IncludedAssetIds,
        };
        var stable = $"{propertyId}.{expectedVersion}";
        return FromMutation(await _properties.UpdateAsync(new NCUpdatePropertyRequest(
            NCPersistentIdentity.Command("housing-legal", operation, stable), expectedVersion, proposed,
            $"living-city.property.legal-{operation}", _housing.Party,
            NCPersistentIdentity.Correlation("housing-legal", operation, stable),
            current.Snapshot.LastAppliedEventId, $"property-legal:{reason}")));
    }

    private bool TryPolicy(out NCPropertyLegalPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPolicy, out policy!) &&
        policy.CondemnationCompensationBasisPoints is >= 0 and <= 10_000;
    private static NCPropertyStatus ResolveTenureStatus(NCPropertyAggregateSnapshot property)
    {
        if (property.Occupants.Any(value => value.Kind == NCPropertyOccupancyKind.Tenancy))
        {
            if (property.EvictionAt != null)
                return NCPropertyStatus.EvictionPending;
            return property.DelinquentSince != null ? NCPropertyStatus.Delinquent : NCPropertyStatus.Rented;
        }
        return property.Occupants.Any(value => value.Kind == NCPropertyOccupancyKind.Ownership)
            ? NCPropertyStatus.OwnerOccupied
            : NCPropertyStatus.Available;
    }
    private static bool Allowed(NCPropertyLegalState from, NCPropertyLegalState to) => (from, to) switch
    {
        (NCPropertyLegalState.Clear, NCPropertyLegalState.Hold) => true,
        (NCPropertyLegalState.Hold, NCPropertyLegalState.Clear) => true,
        (NCPropertyLegalState.Clear or NCPropertyLegalState.Hold, NCPropertyLegalState.Condemned) => true,
        (NCPropertyLegalState.Condemned, NCPropertyLegalState.Destroyed) => true,
        _ => false,
    };
    private static bool Valid(NCApartmentId id, long version, string reason) => id.IsValid && version > 0 &&
        !string.IsNullOrWhiteSpace(reason) && reason.Length <= 128;
    private static NCPropertyLegalMutationResult FromMutation(NCPropertyAggregateMutationResult result) =>
        new(result.Status, result.Property, null, result.Error);
    private static NCPropertyLegalMutationResult Reject(NCPropertyAggregateMutationStatus status, string error,
        NCPropertyAggregateRecord? property = null, NCEconomyMutationResult? economy = null) =>
        new(status, property, economy, error);
}
