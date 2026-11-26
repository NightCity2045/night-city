// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Lifecycle;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Identity;

namespace Content.Server._NC.LivingCity.Lifecycle;

/// <summary>Creates exactly one estate after a character's permanent death and moves electronic funds into probate.</summary>
public sealed partial class NCDeathSuccessionSystem : EntitySystem
{
    [Dependency] private NCEstateRegistrySystem _estates = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;

    /// <summary>
    /// Opens a player estate and moves the character's electronic balance into probate before profile deletion.
    /// This path is independent from physical A-Life actor lifecycle state.
    /// </summary>
    public Task<NCEstateRecord?> OpenCharacterEstateAsync(NCCharacterId characterId, Guid deathEventId,
        NCPartyRef beneficiary = default) => OpenEstateAndCollectCashAsync(
        NCPartyRef.ForCharacter(characterId),
        beneficiary,
        deathEventId,
        $"Estate of character {characterId}");

    private async Task<NCEstateRecord?> OpenEstateAndCollectCashAsync(
        NCPartyRef decedent,
        NCPartyRef beneficiary,
        Guid deathEventId,
        string displayName)
    {
        var estate = await _estates.OpenAsync(decedent, beneficiary, deathEventId, displayName);
        if (estate == null)
            return null;

        var account = await _finance.GetPrimaryAccountAsync(decedent, true);
        if (account == null)
            return estate;
        if (account.Snapshot.Balance < 0)
            return null;

        var accountId = account.Snapshot.AccountId.ToString();
        var existingAsset = estate.Snapshot.Assets.FirstOrDefault(value =>
            value.Kind == NCEstateAssetKind.Money && value.AssetId == accountId);
        if (existingAsset != default)
        {
            if (existingAsset.Transferred)
                return estate;
        }
        else
        {
            var asset = new NCEstateAssetRef(
                NCEstateAssetKind.Money,
                accountId,
                account.Snapshot.Balance,
                false);
            estate = await _estates.AddAssetAsync(estate.Snapshot.EstateId, estate.AggregateVersion, asset);
            if (estate == null)
                return null;
        }

        if (account.Snapshot.Balance > 0)
        {
            var stable = estate.Snapshot.EstateId.ToString();
            var transfer = await _finance.TransferAsync(new NCPartyTransferRequest(
                NCPersistentIdentity.Command("estate", "collect-cash", stable),
                NCPersistentIdentity.Transaction("estate", "collect-cash", stable),
                decedent,
                estate.Snapshot.EstateParty,
                account.Snapshot.Balance,
                NCLedgerTransactionKind.Transfer,
                $"estate:{stable}",
                estate.Snapshot.Administrator,
                NCPersistentIdentity.Correlation("estate", "collect-cash", stable),
                estate.Snapshot.DeathEventId,
                "estate-death-workflow"));
            if (transfer.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
                return null;
        }

        estate = await _estates.GetAsync(estate.Snapshot.EstateId, true);
        if (estate == null)
            return null;

        return await _estates.SetAssetTransferredAsync(
            estate.Snapshot.EstateId,
            estate.AggregateVersion,
            NCEstateAssetKind.Money,
            accountId);
    }

    public async Task<bool> DistributeAsync(NCEstateId estateId)
    {
        var estate = await _estates.GetAsync(estateId, true);
        if (estate == null || !estate.Snapshot.Beneficiary.IsValid || estate.Snapshot.Assets.Any(value =>
                !value.Transferred) || estate.Snapshot.Claims.Any(value => value.Status == NCEstateClaimStatus.Filed))
            return false;
        foreach (var claim in estate.Snapshot.Claims.Where(value => value.Status == NCEstateClaimStatus.Accepted))
        {
            var stable = $"{estateId}.{claim.ClaimId}";
            var paid = await _finance.TransferAsync(new NCPartyTransferRequest(
                NCPersistentIdentity.Command("estate", "pay-claim", stable),
                NCPersistentIdentity.Transaction("estate", "pay-claim", stable), estate.Snapshot.EstateParty,
                claim.Claimant, claim.Amount, NCLedgerTransactionKind.Transfer, $"estate-claim:{claim.ClaimId}",
                estate.Snapshot.Administrator, NCPersistentIdentity.Correlation("estate", "pay-claim", stable),
                estate.Snapshot.LastAppliedEventId, "estate-distribution"));
            if (paid.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate)) return false;
            estate = await _estates.GetAsync(estateId, true);
            if (estate == null) return false;
            estate = await _estates.SetClaimStatusAsync(estateId, estate.AggregateVersion, claim.ClaimId,
                NCEstateClaimStatus.Paid);
            if (estate == null) return false;
        }
        var account = await _finance.GetPrimaryAccountAsync(estate.Snapshot.EstateParty, true);
        if (account?.Snapshot.Balance > 0)
        {
            var stable = estateId.ToString();
            var result = await _finance.TransferAsync(new NCPartyTransferRequest(
                NCPersistentIdentity.Command("estate", "distribute", stable),
                NCPersistentIdentity.Transaction("estate", "distribute", stable), estate.Snapshot.EstateParty,
                estate.Snapshot.Beneficiary, account.Snapshot.Balance, NCLedgerTransactionKind.Transfer,
                $"estate-distribution:{stable}", estate.Snapshot.Administrator,
                NCPersistentIdentity.Correlation("estate", "distribute", stable),
                estate.Snapshot.LastAppliedEventId, "estate-distribution"));
            if (result.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate)) return false;
        }
        estate = await _estates.GetAsync(estateId, true);
        return estate != null && await _estates.CloseAsync(estateId, estate.AggregateVersion) != null;
    }
}
