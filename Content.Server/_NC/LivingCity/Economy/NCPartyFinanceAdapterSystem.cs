// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading.Tasks;
using System.Linq;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Migration;
using Content.Shared._NC.Bank;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Gives characters and persistent actors the same primary-account boundary. It never reads a runtime EntityUid and
/// never mutates a balance directly; callers receive ordinary ledger conflicts and may retry with one command ID.
/// </summary>
public sealed partial class NCPartyFinanceAdapterSystem : EntitySystem
{
    private static readonly ProtoId<NCBankConfigurationPrototype> DefaultConfiguration = "Default";

    [Dependency] private NCPartyDirectorySystem _directory = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public NCBankConfigurationPrototype Configuration => _prototypes.Index(DefaultConfiguration);

    public NCAccountId GetPrimaryAccountId(NCPartyRef owner)
    {
        return NCLegacyBankJobsIdentity.PrimaryAccount(owner);
    }

    public async Task<NCPartyAccountResolution> EnsurePrimaryAccountAsync(
        NCPartyRef owner,
        string auditSource,
        Guid? causationId = null)
    {
        if (!owner.IsValid)
            return new(owner, default, null, "The account owner is invalid.");

        var kind = owner.Kind switch
        {
            NCPartyKind.Character or NCPartyKind.Actor => NCAccountKind.Bank,
            NCPartyKind.Organization or NCPartyKind.CityService => NCAccountKind.Organization,
            NCPartyKind.Business => NCAccountKind.Business,
            NCPartyKind.Estate => NCAccountKind.Estate,
            _ => (NCAccountKind?) null,
        };
        if (kind == null)
            return new(owner, default, null, "This party kind cannot own a primary account.");

        var accountId = GetPrimaryAccountId(owner);
        var stableKey = owner.ToString();
        var result = await _ledger.CreateAccountAsync(new NCCreateAccountRequest(
            NCLegacyBankJobsIdentity.Command("ensure-primary-account", stableKey, "v1"),
            accountId,
            owner,
            kind.Value,
            Configuration.CurrencyId,
            false,
            NCLegacyBankJobsIdentity.Command("correlation-primary-account", stableKey, "v1"),
            causationId,
            auditSource));
        if (result.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate)
            return new(owner, accountId, result.Accounts.Single(), null);

        // A separately-created deterministic account is accepted only if every immutable role field matches.
        var existing = await _ledger.GetAccountAsync(accountId, true);
        if (existing != null && existing.Snapshot.Owner == owner && existing.Snapshot.Kind == kind &&
            existing.Snapshot.CurrencyId == Configuration.CurrencyId.Trim().ToUpperInvariant() &&
            !existing.Snapshot.AllowsNegativeBalance)
        {
            return new(owner, accountId, existing, null);
        }

        return new(owner, accountId, null, result.Error ?? "The primary account could not be created.");
    }

    public async Task<NCPartyAccountResolution> EnsureLegacyOrganizationAsync(
        string organizationPrototypeId,
        string displayName,
        string auditSource)
    {
        if (string.IsNullOrWhiteSpace(organizationPrototypeId) || string.IsNullOrWhiteSpace(displayName))
            return new(default, default, null, "The legacy organization identity is invalid.");

        var owner = NCLegacyBankJobsIdentity.OrganizationParty(organizationPrototypeId);
        var sourceKey = organizationPrototypeId.Trim();
        var registration = await _directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            NCLegacyBankJobsIdentity.Command("register-organization", sourceKey, "v1"),
            owner,
            displayName.Trim(),
            NCLegacyBankJobsIdentity.Command("correlation-organization", sourceKey, "v1"),
            AuditSource: auditSource));
        if (registration.Status is not (NCPartyDirectoryMutationStatus.Applied or
            NCPartyDirectoryMutationStatus.Duplicate))
        {
            var existing = await _directory.GetAsync(owner, true);
            if (existing == null || existing.Snapshot.Lifecycle != NCPartyLifecycle.Active)
                return new(owner, default, null, registration.Error ?? "The organization could not be registered.");
        }

        return await EnsurePrimaryAccountAsync(owner, auditSource);
    }

    public async Task<NCPartyAccountResolution> EnsureLegacyClearingAccountAsync(string auditSource)
    {
        var owner = NCLegacyBankJobsIdentity.ClearingParty;
        var registration = await _directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            NCLegacyBankJobsIdentity.Command("register-clearing-party", "legacy-bank", "v1"),
            owner,
            "Legacy Bank Migration Clearing",
            NCLegacyBankJobsIdentity.Command("correlation-clearing-party", "legacy-bank", "v1"),
            AuditSource: auditSource));
        if (registration.Status is not (NCPartyDirectoryMutationStatus.Applied or
            NCPartyDirectoryMutationStatus.Duplicate))
        {
            var existing = await _directory.GetAsync(owner, true);
            if (existing == null || existing.Snapshot.Lifecycle != NCPartyLifecycle.Active)
                return new(owner, NCLegacyBankJobsIdentity.ClearingAccount, null,
                    registration.Error ?? "The migration clearing party could not be registered.");
        }

        var result = await _ledger.CreateAccountAsync(new NCCreateAccountRequest(
            NCLegacyBankJobsIdentity.Command("ensure-clearing-account", "legacy-bank", "v1"),
            NCLegacyBankJobsIdentity.ClearingAccount,
            owner,
            NCAccountKind.SystemClearing,
            Configuration.CurrencyId,
            true,
            NCLegacyBankJobsIdentity.Command("correlation-clearing-account", "legacy-bank", "v1"),
            AuditSource: auditSource));
        if (result.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate)
            return new(owner, NCLegacyBankJobsIdentity.ClearingAccount, result.Accounts.Single(), null);

        var account = await _ledger.GetAccountAsync(NCLegacyBankJobsIdentity.ClearingAccount, true);
        return account != null && account.Snapshot.Owner == owner && account.Snapshot.Kind == NCAccountKind.SystemClearing &&
               account.Snapshot.AllowsNegativeBalance
            ? new(owner, account.Snapshot.AccountId, account, null)
            : new(owner, NCLegacyBankJobsIdentity.ClearingAccount, null,
                result.Error ?? "The migration clearing account could not be created.");
    }

    public async Task<NCEconomyMutationResult> TransferAsync(NCPartyTransferRequest request)
    {
        if (request.Amount <= 0 || request.Payer == request.Payee)
            return new(NCEconomyMutationStatus.Rejected, [], null, "The party transfer is invalid.");

        var payer = await EnsurePrimaryAccountAsync(request.Payer, request.AuditSource, request.CausationId);
        var payee = await EnsurePrimaryAccountAsync(request.Payee, request.AuditSource, request.CausationId);
        if (!payer.IsResolved || !payee.IsResolved)
        {
            return new(NCEconomyMutationStatus.Rejected, [], null,
                payer.Error ?? payee.Error ?? "A party account is unavailable.");
        }

        return await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
            request.CommandId,
            request.TransactionId,
            request.Kind,
            request.Reference,
            [
                new NCLedgerLegRequest(payer.AccountId, payer.Account!.AggregateVersion, -request.Amount),
                new NCLedgerLegRequest(payee.AccountId, payee.Account!.AggregateVersion, request.Amount),
            ],
            request.Initiator,
            request.CorrelationId,
            request.CausationId,
            request.AuditSource));
    }
}
