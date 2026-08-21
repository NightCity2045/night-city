// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Housing;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Housing;

/// <summary>
/// Common purchase/rent settlement authority. Property rules are validated here, then the property participant and
/// balanced money legs commit together through the neutral ledger boundary.
/// </summary>
public sealed partial class NCPropertySettlementSystem : EntitySystem
{
    private const int MutationGateStripeCount = 64;
    private const int MaximumAuditSourceLength = 128;

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private NCPropertyRegistrySystem _properties = default!;
    [Dependency] private NCHousingServiceSystem _housing = default!;

    private readonly SemaphoreSlim[] _gates = CreateGates();

    public async Task<NCPropertySettlementResult> PurchaseAsync(NCPurchasePropertySettlementRequest request)
    {
        var replay = await TryReplayAsync(
            request.CommandId,
            request.TransactionId,
            request.PropertyId,
            request.CorrelationId,
            request.AuditSource);
        if (replay != null)
            return replay;
        if (!ValidMetadata(request.CommandId, request.TransactionId, request.PropertyId,
                request.ExpectedPropertyVersion, request.Buyer, request.Price, request.AccessGrantId,
                request.CorrelationId, request.AuditSource) || !ValidResident(request.Buyer) ||
            !ValidPermissions(request.Permissions))
            return Reject(NCPropertySettlementStatus.Rejected, "The property purchase request is invalid.");

        var gate = GetGate(request.PropertyId);
        await gate.WaitAsync();
        try
        {
            var current = await _properties.GetAsync(request.PropertyId, true);
            if (current == null)
                return Reject(NCPropertySettlementStatus.NotFound, "The property does not exist.");
            if (current.AggregateVersion != request.ExpectedPropertyVersion)
                return Reject(NCPropertySettlementStatus.VersionConflict, "The property version changed.", current);
            if (current.Snapshot.Status != NCPropertyStatus.Available ||
                current.Snapshot.LegalState != NCPropertyLegalState.Clear ||
                current.Snapshot.Occupants.Any(value => value.Kind == NCPropertyOccupancyKind.Tenancy))
                return Reject(NCPropertySettlementStatus.NotAvailable, "The property is not available.", current);
            if (!_clock.TryGetCurrentTime(out _, out var now))
                return Reject(NCPropertySettlementStatus.StorageFailure, "The active-world clock is unavailable.");

            var accessRevision = checked(current.Snapshot.AccessRevision + 1);
            var proposed = current.Snapshot with
            {
                Owner = request.Buyer,
                Occupants =
                [
                    new NCPropertyOccupantSnapshot(request.Buyer, NCPropertyOccupancyKind.Ownership, now, null),
                ],
                AccessGrants =
                [
                    new NCPropertyAccessGrantSnapshot(
                        request.AccessGrantId,
                        request.Buyer,
                        request.Permissions,
                        accessRevision,
                        now,
                        null),
                ],
                Status = NCPropertyStatus.OwnerOccupied,
                PurchasePrice = request.Price,
                AcquiredAt = now,
                AccessRevision = accessRevision,
                RentPolicyId = string.Empty,
                NextRentDueAt = null,
                DelinquentSince = null,
                EvictionAt = null,
            };
            return await CommitAsync(
                request.CommandId,
                request.TransactionId,
                current,
                proposed,
                request.Buyer,
                current.Snapshot.Owner,
                request.Price,
                NCLedgerTransactionKind.Purchase,
                $"property purchase {request.PropertyId}",
                "living-city.property.purchased",
                request.CorrelationId,
                request.CausationId,
                request.AuditSource);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCPropertySettlementResult> RentAsync(NCRentPropertySettlementRequest request)
    {
        var replay = await TryReplayAsync(
            request.CommandId,
            request.TransactionId,
            request.PropertyId,
            request.CorrelationId,
            request.AuditSource);
        if (replay != null)
            return replay;
        if (!ValidMetadata(request.CommandId, request.TransactionId, request.PropertyId,
                request.ExpectedPropertyVersion, request.Tenant, request.InitialCharge, request.AccessGrantId,
                request.CorrelationId, request.AuditSource) || !ValidResident(request.Tenant) ||
            !ValidPermissions(request.Permissions) || request.BillingIntervalTicks <= 0 ||
            string.IsNullOrWhiteSpace(request.RentPolicyId) || request.RentPolicyId.Length > 64 ||
            string.IsNullOrWhiteSpace(request.TemplateId) || request.TemplateId.Length > 64 ||
            request.TemplateRevision <= 0)
            return Reject(NCPropertySettlementStatus.Rejected, "The property rental request is invalid.");

        var gate = GetGate(request.PropertyId);
        await gate.WaitAsync();
        try
        {
            var current = await _properties.GetAsync(request.PropertyId, true);
            if (current == null)
                return Reject(NCPropertySettlementStatus.NotFound, "The property does not exist.");
            if (current.AggregateVersion != request.ExpectedPropertyVersion)
                return Reject(NCPropertySettlementStatus.VersionConflict, "The property version changed.", current);
            if (current.Snapshot.Status != NCPropertyStatus.Available ||
                current.Snapshot.LegalState != NCPropertyLegalState.Clear ||
                current.Snapshot.Occupants.Any(value => value.Kind == NCPropertyOccupancyKind.Tenancy))
                return Reject(NCPropertySettlementStatus.NotAvailable, "The property is not available.", current);
            if (current.Snapshot.Owner == request.Tenant || !_clock.TryGetCurrentTime(out _, out var now))
                return Reject(NCPropertySettlementStatus.Rejected, "The rental parties or active-world clock are invalid.");

            NCActiveWorldTime due;
            try
            {
                due = now.Advance(TimeSpan.FromTicks(request.BillingIntervalTicks));
            }
            catch (ArgumentOutOfRangeException)
            {
                return Reject(NCPropertySettlementStatus.Rejected, "The rental interval is invalid.");
            }
            var accessRevision = checked(current.Snapshot.AccessRevision + 1);
            var proposed = current.Snapshot with
            {
                Occupants = current.Snapshot.Occupants
                    .Where(value => value.Kind != NCPropertyOccupancyKind.Tenancy)
                    .Append(new NCPropertyOccupantSnapshot(
                        request.Tenant,
                        NCPropertyOccupancyKind.Tenancy,
                        now,
                        null))
                    .ToArray(),
                AccessGrants =
                [
                    new NCPropertyAccessGrantSnapshot(
                        request.AccessGrantId,
                        request.Tenant,
                        request.Permissions,
                        accessRevision,
                        now,
                        null),
                ],
                Status = NCPropertyStatus.Rented,
                AcquiredAt = now,
                AccessRevision = accessRevision,
                RentPolicyId = request.RentPolicyId.Trim(),
                CurrentTemplateId = request.TemplateId.Trim(),
                CurrentTemplateRevision = request.TemplateRevision,
                InteriorState = "ready",
                NextRentDueAt = due,
                DelinquentSince = null,
                EvictionAt = null,
            };
            if (request.InitialCharge == 0)
            {
                return await CommitWithoutMoneyAsync(
                    request.CommandId,
                    current,
                    proposed,
                    request.Tenant,
                    "living-city.property.rented-free",
                    request.CorrelationId,
                    request.CausationId,
                    request.AuditSource);
            }
            return await CommitAsync(
                request.CommandId,
                request.TransactionId,
                current,
                proposed,
                request.Tenant,
                current.Snapshot.Owner,
                request.InitialCharge,
                NCLedgerTransactionKind.Rent,
                $"initial property rent {request.PropertyId}",
                "living-city.property.rented",
                request.CorrelationId,
                request.CausationId,
                request.AuditSource);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCPropertySettlementResult> RenewAsync(NCRenewPropertyRentSettlementRequest request)
    {
        var replay = await TryReplayAsync(
            request.CommandId,
            request.TransactionId,
            request.PropertyId,
            request.CorrelationId,
            request.AuditSource);
        if (replay != null)
            return replay;
        if (!ValidMetadata(request.CommandId, request.TransactionId, request.PropertyId,
                request.ExpectedPropertyVersion, request.Tenant, request.Charge, request.CommandId,
                request.CorrelationId, request.AuditSource) || !ValidResident(request.Tenant) ||
            request.BillingIntervalTicks <= 0)
            return Reject(NCPropertySettlementStatus.Rejected, "The rent renewal request is invalid.");

        var gate = GetGate(request.PropertyId);
        await gate.WaitAsync();
        try
        {
            var current = await _properties.GetAsync(request.PropertyId, true);
            if (current == null)
                return Reject(NCPropertySettlementStatus.NotFound, "The property does not exist.");
            if (current.AggregateVersion != request.ExpectedPropertyVersion)
                return Reject(NCPropertySettlementStatus.VersionConflict, "The property version changed.", current);
            if (current.Snapshot.Status is not (NCPropertyStatus.Rented or NCPropertyStatus.Delinquent) ||
                current.Snapshot.Occupants.All(value => value.Kind != NCPropertyOccupancyKind.Tenancy ||
                    value.Occupant != request.Tenant))
                return Reject(NCPropertySettlementStatus.NotAuthorized, "The party is not an active tenant.", current);
            if (!_clock.TryGetCurrentTime(out _, out var now))
                return Reject(NCPropertySettlementStatus.StorageFailure, "The active-world clock is unavailable.");

            NCActiveWorldTime due;
            try
            {
                var basis = current.Snapshot.NextRentDueAt is { } currentDue && currentDue > now ? currentDue : now;
                due = basis.Advance(TimeSpan.FromTicks(request.BillingIntervalTicks));
            }
            catch (ArgumentOutOfRangeException)
            {
                return Reject(NCPropertySettlementStatus.Rejected, "The rental interval is invalid.");
            }
            var proposed = current.Snapshot with
            {
                Status = NCPropertyStatus.Rented,
                NextRentDueAt = due,
                DelinquentSince = null,
                EvictionAt = null,
            };
            if (request.Charge == 0)
            {
                return await CommitWithoutMoneyAsync(
                    request.CommandId,
                    current,
                    proposed,
                    request.Tenant,
                    "living-city.property.rent-renewed-free",
                    request.CorrelationId,
                    request.CausationId,
                    request.AuditSource);
            }
            return await CommitAsync(
                request.CommandId,
                request.TransactionId,
                current,
                proposed,
                request.Tenant,
                current.Snapshot.Owner,
                request.Charge,
                NCLedgerTransactionKind.Rent,
                $"property rent renewal {request.PropertyId}",
                "living-city.property.rent-renewed",
                request.CorrelationId,
                request.CausationId,
                request.AuditSource);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCPropertySettlementResult> ReleaseLeaseAsync(
        NCReleasePropertyLeaseSettlementRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.TransactionId.IsValid || !request.PropertyId.IsValid ||
            request.ExpectedPropertyVersion <= 0 || !request.Tenant.IsValid || !request.Initiator.IsValid ||
            !Enum.IsDefined(request.Reason) || request.DepositRefundBasisPoints is < 0 or > 10_000 ||
            request.CorrelationId == Guid.Empty || string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Length > MaximumAuditSourceLength)
            return Reject(NCPropertySettlementStatus.Rejected, "The lease release request is invalid.");
        var gate = GetGate(request.PropertyId);
        await gate.WaitAsync();
        try
        {
            var current = await _properties.GetAsync(request.PropertyId, true);
            if (current == null)
                return Reject(NCPropertySettlementStatus.NotFound, "The property does not exist.");
            if (current.AggregateVersion != request.ExpectedPropertyVersion)
                return Reject(NCPropertySettlementStatus.VersionConflict, "The property version changed.", current);
            if (!current.Snapshot.Occupants.Any(occupant =>
                    occupant.Kind == NCPropertyOccupancyKind.Tenancy && occupant.Occupant == request.Tenant) ||
                request.Initiator != request.Tenant && request.Initiator != current.Snapshot.Owner)
                return Reject(NCPropertySettlementStatus.NotAuthorized,
                    "The lease release parties are not authorized.", current);
            var proposed = current.Snapshot with
            {
                Occupants = current.Snapshot.Occupants
                    .Where(occupant => occupant.Kind != NCPropertyOccupancyKind.Tenancy &&
                                       occupant.Kind != NCPropertyOccupancyKind.Resident &&
                                       occupant.Kind != NCPropertyOccupancyKind.Guest).ToArray(),
                AccessGrants = [],
                Status = NCPropertyStatus.Available,
                AcquiredAt = null,
                AccessRevision = checked(current.Snapshot.AccessRevision + 1),
                RentPolicyId = string.Empty,
                CurrentTemplateId = null,
                CurrentTemplateRevision = null,
                ActiveDeploymentId = default,
                InteriorState = "empty",
                NextRentDueAt = null,
                SecurityDepositAmount = 0,
                SecurityDepositAccountId = default,
                DelinquentSince = null,
                EvictionAt = null,
            };
            if (current.Snapshot.SecurityDepositAmount == 0)
                return await CommitWithoutMoneyAsync(request.CommandId, current, proposed, request.Initiator,
                    "living-city.property.lease-released", request.CorrelationId, request.CausationId,
                    request.AuditSource);
            var escrow = await _ledger.GetAccountAsync(current.Snapshot.SecurityDepositAccountId, true);
            var tenant = await _finance.EnsurePrimaryAccountAsync(request.Tenant, request.AuditSource,
                request.CausationId);
            var owner = await _finance.EnsurePrimaryAccountAsync(current.Snapshot.Owner, request.AuditSource,
                request.CausationId);
            if (escrow == null || !tenant.IsResolved || !owner.IsResolved ||
                escrow.Snapshot.Balance < current.Snapshot.SecurityDepositAmount)
                return Reject(NCPropertySettlementStatus.StorageFailure,
                    "The lease deposit accounts are inconsistent.", current);
            if (!_properties.TryPrepareSettlementWrite(current, proposed, request.TransactionId,
                    "living-city.property.lease-released", request.AuditSource, out var participant, out _,
                    out var error))
                return Reject(NCPropertySettlementStatus.Rejected,
                    error ?? "The released property state is invalid.", current);
            var refund = checked(current.Snapshot.SecurityDepositAmount * request.DepositRefundBasisPoints / 10_000);
            var retained = checked(current.Snapshot.SecurityDepositAmount - refund);
            var legs = new List<NCLedgerLegRequest>
            {
                new(escrow.Snapshot.AccountId, escrow.AggregateVersion,
                    -current.Snapshot.SecurityDepositAmount),
            };
            if (refund > 0)
                legs.Add(new NCLedgerLegRequest(tenant.AccountId, tenant.Account!.AggregateVersion, refund));
            if (retained > 0)
                legs.Add(new NCLedgerLegRequest(owner.AccountId, owner.Account!.AggregateVersion, retained));
            var economy = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
                request.CommandId, request.TransactionId, NCLedgerTransactionKind.Refund,
                $"property lease release {request.PropertyId}", legs.ToArray(), request.Initiator,
                request.CorrelationId, request.CausationId, request.AuditSource, [participant!]));
            if (economy.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
                return Reject(Map(economy.Status), economy.Error ?? "The deposit release was rejected.",
                    current, economy);
            var property = await _properties.GetAsync(request.PropertyId, true);
            return property == null
                ? Reject(NCPropertySettlementStatus.StorageFailure,
                    "The released property could not be reloaded.", null, economy)
                : new(economy.Status == NCEconomyMutationStatus.Applied
                    ? NCPropertySettlementStatus.Applied
                    : NCPropertySettlementStatus.Duplicate, property, economy, null);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCPropertySettlementResult> RenovateAsync(NCRenovatePropertySettlementRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.TransactionId.IsValid || !request.PropertyId.IsValid ||
            request.ExpectedPropertyVersion <= 0 || !request.Actor.IsValid || request.Price < 0 ||
            string.IsNullOrWhiteSpace(request.TemplateId) || request.TemplateId.Length > 64 ||
            request.TemplateRevision <= 0 || request.CorrelationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.AuditSource) || request.AuditSource.Length > MaximumAuditSourceLength ||
            !_clock.TryGetCurrentTime(out _, out var now))
            return Reject(NCPropertySettlementStatus.Rejected, "The renovation request is invalid.");
        var gate = GetGate(request.PropertyId);
        await gate.WaitAsync();
        try
        {
            var current = await _properties.GetAsync(request.PropertyId, true);
            if (current == null)
                return Reject(NCPropertySettlementStatus.NotFound, "The property does not exist.");
            if (current.AggregateVersion != request.ExpectedPropertyVersion)
                return Reject(NCPropertySettlementStatus.VersionConflict, "The property version changed.", current);
            var authorized = current.Snapshot.Owner == request.Actor || current.Snapshot.AccessGrants.Any(grant =>
                grant.Subject == request.Actor && grant.AccessRevision == current.Snapshot.AccessRevision &&
                grant.Permissions.HasFlag(NCPropertyPermission.Renovate) &&
                (grant.ValidUntil == null || grant.ValidUntil > now));
            if (!authorized || current.Snapshot.LegalState != NCPropertyLegalState.Clear ||
                current.Snapshot.Status is NCPropertyStatus.Destroyed or NCPropertyStatus.Condemned or
                    NCPropertyStatus.LegalHold)
                return Reject(NCPropertySettlementStatus.NotAuthorized,
                    "The actor or property state does not permit renovation.", current);
            var proposed = current.Snapshot with
            {
                CurrentTemplateId = request.TemplateId.Trim(),
                CurrentTemplateRevision = request.TemplateRevision,
                ActiveDeploymentId = default,
                InteriorState = "renovation-pending",
                RenovationRevision = checked(current.Snapshot.RenovationRevision + 1),
            };
            if (request.Price == 0)
                return await CommitWithoutMoneyAsync(request.CommandId, current, proposed, request.Actor,
                    "living-city.property.renovation-paid-free", request.CorrelationId, request.CausationId,
                    request.AuditSource);
            if (!await _housing.EnsureReadyAsync(request.AuditSource))
                return Reject(NCPropertySettlementStatus.StorageFailure,
                    "The renovation service is unavailable.", current);
            return await CommitAsync(request.CommandId, request.TransactionId, current, proposed, request.Actor,
                _housing.Party, request.Price, NCLedgerTransactionKind.Purchase,
                $"property renovation {request.PropertyId}", "living-city.property.renovation-paid",
                request.CorrelationId, request.CausationId, request.AuditSource);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCPropertySettlementResult> RollbackRenovationAsync(
        NCRollbackPropertyRenovationRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.RefundTransactionId.IsValid || !request.PropertyId.IsValid ||
            request.ExpectedPropertyVersion <= 0 || !request.Actor.IsValid || request.RefundAmount < 0 ||
            (request.PreviousTemplateId == null) != (request.PreviousTemplateRevision == null) ||
            request.PreviousTemplateRevision is <= 0 || string.IsNullOrWhiteSpace(request.PreviousInteriorState) ||
            request.PreviousInteriorState.Length > 128 || request.PreviousRenovationRevision < 0 ||
            request.CorrelationId == Guid.Empty || string.IsNullOrWhiteSpace(request.AuditSource) ||
            request.AuditSource.Length > MaximumAuditSourceLength)
            return Reject(NCPropertySettlementStatus.Rejected, "The renovation rollback request is invalid.");
        var gate = GetGate(request.PropertyId);
        await gate.WaitAsync();
        try
        {
            var current = await _properties.GetAsync(request.PropertyId, true);
            if (current == null)
                return Reject(NCPropertySettlementStatus.NotFound, "The property does not exist.");
            if (current.AggregateVersion != request.ExpectedPropertyVersion)
                return Reject(NCPropertySettlementStatus.VersionConflict, "The property version changed.", current);
            if (current.Snapshot.InteriorState != "renovation-pending" ||
                current.Snapshot.RenovationRevision != request.PreviousRenovationRevision + 1)
                return Reject(NCPropertySettlementStatus.Rejected, "The renovation is not rollback-safe.", current);
            var proposed = current.Snapshot with
            {
                CurrentTemplateId = request.PreviousTemplateId,
                CurrentTemplateRevision = request.PreviousTemplateRevision,
                ActiveDeploymentId = default,
                InteriorState = request.PreviousInteriorState,
                RenovationRevision = request.PreviousRenovationRevision,
            };
            if (request.RefundAmount == 0)
                return await CommitWithoutMoneyAsync(request.CommandId, current, proposed, request.Actor,
                    "living-city.property.renovation-rolled-back-free", request.CorrelationId,
                    request.CausationId, request.AuditSource);
            if (!await _housing.EnsureReadyAsync(request.AuditSource))
                return Reject(NCPropertySettlementStatus.StorageFailure,
                    "The renovation refund service is unavailable.", current);
            var payer = await _finance.EnsurePrimaryAccountAsync(_housing.Party, request.AuditSource,
                request.CausationId);
            var payee = await _finance.EnsurePrimaryAccountAsync(request.Actor, request.AuditSource,
                request.CausationId);
            if (!payer.IsResolved || !payee.IsResolved)
                return Reject(NCPropertySettlementStatus.StorageFailure,
                    "The renovation refund accounts are unavailable.", current);
            if (!_properties.TryPrepareSettlementWrite(current, proposed, request.RefundTransactionId,
                    "living-city.property.renovation-rolled-back", request.AuditSource,
                    out var participant, out _, out var error))
                return Reject(NCPropertySettlementStatus.StorageFailure,
                    error ?? "The renovation refund participant is unavailable.", current);
            var economy = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
                request.CommandId, request.RefundTransactionId, NCLedgerTransactionKind.Refund,
                $"property renovation rollback {request.PropertyId}",
                [
                    new NCLedgerLegRequest(payer.AccountId, payer.Account!.AggregateVersion, -request.RefundAmount),
                    new NCLedgerLegRequest(payee.AccountId, payee.Account!.AggregateVersion, request.RefundAmount),
                ], _housing.Party, request.CorrelationId, request.CausationId, request.AuditSource, [participant!]));
            if (economy.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
                return Reject(Map(economy.Status), economy.Error ?? "The renovation refund was rejected.",
                    current, economy);
            var loaded = await _properties.GetAsync(request.PropertyId, true);
            return loaded == null
                ? Reject(NCPropertySettlementStatus.StorageFailure,
                    "The rolled-back property could not be reloaded.", null, economy)
                : new(economy.Status == NCEconomyMutationStatus.Applied
                    ? NCPropertySettlementStatus.Applied
                    : NCPropertySettlementStatus.Duplicate, loaded, economy, null);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NCPropertySettlementResult> CommitWithoutMoneyAsync(
        Guid commandId,
        NCPropertyAggregateRecord current,
        NCPropertyAggregateSnapshot proposed,
        NCPartyRef initiator,
        string eventType,
        Guid correlationId,
        Guid? causationId,
        string auditSource)
    {
        // Zero-price offers have no monetary fact to record. The property remains one exactly-once aggregate mutation
        // instead of fabricating invalid zero-value ledger legs.
        var mutation = await _properties.UpdateAsync(new NCUpdatePropertyRequest(
            commandId,
            current.AggregateVersion,
            proposed,
            eventType,
            initiator,
            correlationId,
            causationId,
            auditSource));
        return mutation.Status switch
        {
            NCPropertyAggregateMutationStatus.Applied =>
                new NCPropertySettlementResult(NCPropertySettlementStatus.Applied, mutation.Property, null, null),
            NCPropertyAggregateMutationStatus.Duplicate =>
                new NCPropertySettlementResult(NCPropertySettlementStatus.Duplicate, mutation.Property, null, null),
            NCPropertyAggregateMutationStatus.NotFound => Reject(NCPropertySettlementStatus.NotFound,
                mutation.Error ?? "The property does not exist.", mutation.Property),
            NCPropertyAggregateMutationStatus.VersionConflict => Reject(NCPropertySettlementStatus.VersionConflict,
                mutation.Error ?? "The property version changed.", mutation.Property),
            _ => Reject(NCPropertySettlementStatus.StorageFailure,
                mutation.Error ?? "The property mutation was not committed.", mutation.Property),
        };
    }

    private async Task<NCPropertySettlementResult> CommitAsync(
        Guid commandId,
        NCTransactionId transactionId,
        NCPropertyAggregateRecord current,
        NCPropertyAggregateSnapshot proposed,
        NCPartyRef payer,
        NCPartyRef payee,
        long amount,
        NCLedgerTransactionKind kind,
        string reference,
        string eventType,
        Guid correlationId,
        Guid? causationId,
        string auditSource)
    {
        if (payer == payee)
            return Reject(NCPropertySettlementStatus.Rejected, "The settlement parties are identical.", current);
        if (!_properties.TryPrepareSettlementWrite(
                current,
                proposed,
                transactionId,
                eventType,
                auditSource,
                out var participant,
                out _,
                out var preparationError))
            return Reject(NCPropertySettlementStatus.Rejected,
                preparationError ?? "The property participant is invalid.", current);

        var payerAccount = await _finance.EnsurePrimaryAccountAsync(payer, auditSource, causationId);
        var payeeAccount = await _finance.EnsurePrimaryAccountAsync(payee, auditSource, causationId);
        if (!payerAccount.IsResolved || !payeeAccount.IsResolved)
            return Reject(NCPropertySettlementStatus.Rejected,
                payerAccount.Error ?? payeeAccount.Error ?? "A settlement account is unavailable.", current);

        var economy = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
            commandId,
            transactionId,
            kind,
            reference,
            [
                new NCLedgerLegRequest(payerAccount.AccountId, payerAccount.Account!.AggregateVersion, -amount),
                new NCLedgerLegRequest(payeeAccount.AccountId, payeeAccount.Account!.AggregateVersion, amount),
            ],
            payer,
            correlationId,
            causationId,
            auditSource,
            [participant!]));
        if (economy.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
            return Reject(Map(economy.Status), economy.Error ?? "The ledger settlement was rejected.", current, economy);

        var property = await _properties.GetAsync(current.Snapshot.PropertyId, true);
        if (property == null || !await ReceiptContainsProperty(commandId, transactionId, current.Snapshot.PropertyId))
            return Reject(NCPropertySettlementStatus.StorageFailure,
                "The committed property participant cannot be verified.", property, economy);
        return new NCPropertySettlementResult(
            economy.Status == NCEconomyMutationStatus.Applied
                ? NCPropertySettlementStatus.Applied
                : NCPropertySettlementStatus.Duplicate,
            property,
            economy,
            null);
    }

    private async Task<NCPropertySettlementResult?> TryReplayAsync(
        Guid commandId,
        NCTransactionId transactionId,
        NCApartmentId propertyId,
        Guid correlationId,
        string auditSource)
    {
        if (commandId == Guid.Empty || !transactionId.IsValid || !propertyId.IsValid)
            return null;
        var receipt = await _database.GetNCPersistentCommandResultAsync(commandId);
        if (receipt is
            {
                CommandType: "living-city.property.update",
                ScopeType: "property",
                Status: NCPersistentMutationStatus.DuplicateCompleted,
            } && receipt.ScopeId == propertyId.Value.ToString("D") && receipt.Aggregates.Any(value =>
                value.AggregateType == NCPropertyRegistrySystem.AggregateType &&
                value.AggregateId == propertyId.Value.ToString("D")))
        {
            var freeProperty = await _properties.GetAsync(propertyId, true);
            return freeProperty == null
                ? Reject(NCPropertySettlementStatus.StorageFailure,
                    "The completed zero-price property settlement cannot be reloaded.")
                : new NCPropertySettlementResult(NCPropertySettlementStatus.Duplicate, freeProperty, null, null);
        }
        if (receipt is not
            {
                CommandType: "living-city.ledger.post",
                ScopeType: "transaction",
                Status: NCPersistentMutationStatus.DuplicateCompleted,
            } || receipt.ScopeId != transactionId.Value.ToString("D") ||
            !receipt.Aggregates.Any(value => value.AggregateType == NCPropertyRegistrySystem.AggregateType &&
                value.AggregateId == propertyId.Value.ToString("D")))
            return null;

        var transaction = await _ledger.GetTransactionAsync(transactionId, true);
        var property = await _properties.GetAsync(propertyId, true);
        if (transaction == null || property == null)
            return Reject(NCPropertySettlementStatus.StorageFailure,
                "The completed property settlement cannot be reloaded.", property);
        var legs = new List<NCLedgerLegRequest>(transaction.Snapshot.Legs.Length);
        foreach (var leg in transaction.Snapshot.Legs)
        {
            var account = await _ledger.GetAccountAsync(leg.AccountId, true);
            if (account == null)
                return Reject(NCPropertySettlementStatus.StorageFailure,
                    "A completed settlement account cannot be reloaded.", property);
            legs.Add(new NCLedgerLegRequest(leg.AccountId, account.AggregateVersion, leg.Amount));
        }

        var replay = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
            commandId,
            transactionId,
            transaction.Snapshot.Kind,
            transaction.Snapshot.Reference,
            legs.ToArray(),
            transaction.Snapshot.Initiator,
            correlationId == Guid.Empty ? Guid.NewGuid() : correlationId,
            AuditSource: string.IsNullOrWhiteSpace(auditSource) ? "property-settlement-replay" : auditSource));
        return replay.Status == NCEconomyMutationStatus.Duplicate
            ? new NCPropertySettlementResult(NCPropertySettlementStatus.Duplicate, property, replay, null)
            : Reject(NCPropertySettlementStatus.StorageFailure,
                replay.Error ?? "The completed property settlement receipt is invalid.", property, replay);
    }

    private async Task<bool> ReceiptContainsProperty(Guid commandId, NCTransactionId transactionId, NCApartmentId propertyId)
    {
        var receipt = await _database.GetNCPersistentCommandResultAsync(commandId);
        return receipt is
        {
            CommandType: "living-city.ledger.post",
            ScopeType: "transaction",
            Status: NCPersistentMutationStatus.DuplicateCompleted,
        } && receipt.ScopeId == transactionId.Value.ToString("D") && receipt.Aggregates.Any(value =>
            value.AggregateType == NCPropertyRegistrySystem.AggregateType &&
            value.AggregateId == propertyId.Value.ToString("D"));
    }

    private static bool ValidMetadata(
        Guid commandId,
        NCTransactionId transactionId,
        NCApartmentId propertyId,
        long expectedVersion,
        NCPartyRef actor,
        long amount,
        Guid accessGrantId,
        Guid correlationId,
        string auditSource)
    {
        return commandId != Guid.Empty && transactionId.IsValid && propertyId.IsValid && expectedVersion >= 1 &&
               actor.IsValid && amount >= 0 && accessGrantId != Guid.Empty && correlationId != Guid.Empty &&
               !string.IsNullOrWhiteSpace(auditSource) && auditSource.Length <= MaximumAuditSourceLength;
    }

    private static bool ValidResident(NCPartyRef party) =>
        party.Kind is NCPartyKind.Character or NCPartyKind.Actor;

    private static bool ValidPermissions(NCPropertyPermission permissions) =>
        permissions != NCPropertyPermission.None &&
        (permissions & ~NCPropertyPermission.All) == 0;

    private static NCPropertySettlementStatus Map(NCEconomyMutationStatus status)
    {
        return status switch
        {
            NCEconomyMutationStatus.InsufficientFunds => NCPropertySettlementStatus.InsufficientFunds,
            NCEconomyMutationStatus.VersionConflict => NCPropertySettlementStatus.VersionConflict,
            NCEconomyMutationStatus.NotFound => NCPropertySettlementStatus.NotFound,
            NCEconomyMutationStatus.StorageFailure => NCPropertySettlementStatus.StorageFailure,
            _ => NCPropertySettlementStatus.Rejected,
        };
    }

    private SemaphoreSlim GetGate(NCApartmentId propertyId) =>
        _gates[(propertyId.GetHashCode() & int.MaxValue) % _gates.Length];

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static NCPropertySettlementResult Reject(
        NCPropertySettlementStatus status,
        string error,
        NCPropertyAggregateRecord? property = null,
        NCEconomyMutationResult? economy = null)
    {
        return new(status, property, economy, error);
    }
}
