// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Actors;
using Content.Server._NC.LivingCity.Actors.Components;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.Actors;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Lifecycle;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Identity;
using Content.Shared.Mobs;

namespace Content.Server._NC.LivingCity.Lifecycle;

/// <summary>Creates exactly one estate after permanent actor death and moves electronic funds into probate.</summary>
public sealed partial class NCDeathSuccessionSystem : EntitySystem
{
    [Dependency] private NCCityActorRegistrySystem _actors = default!;
    [Dependency] private NCEstateRegistrySystem _estates = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    private readonly HashSet<NCActorId> _processing = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityActorPersistedEvent>(OnActor);
        SubscribeLocalEvent<NCCityActorBindingComponent, MobStateChangedEvent>(OnPhysicalStateChanged);
    }
    private void OnPhysicalStateChanged(Entity<NCCityActorBindingComponent> entity, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead)
            _ = ConfirmPhysicalDeathAsync(entity.Comp.ActorId);
    }
    private async Task ConfirmPhysicalDeathAsync(NCActorId actorId)
    {
        var actor = await _actors.GetAsync(actorId, true);
        if (actor == null || actor.Snapshot.Lifecycle.Status == NCCityActorStatus.Dead) return;
        var stable = $"{actorId}.{actor.AggregateVersion}";
        await _actors.SetStatusAsync(new NCCityActorStatusMutationRequest(
            NCPersistentIdentity.Command("death", "confirm", stable), actorId, actor.AggregateVersion,
            NCCityActorStatus.Dead, NCPersistentIdentity.Correlation("death", "confirm", stable),
            actor.Snapshot.LastAppliedEventId, "authoritative-physical-death"));
    }
    private void OnActor(NCCityActorPersistedEvent args)
    {
        if (args.Status == NCCityActorStatus.Dead && _processing.Add(args.ActorId))
            _ = OpenActorEstateAsync(args.ActorId);
    }

    public Task<NCEstateRecord?> OpenCharacterEstateAsync(NCCharacterId characterId, Guid deathEventId,
        NCPartyRef beneficiary = default) => _estates.OpenAsync(NCPartyRef.ForCharacter(characterId), beneficiary,
        deathEventId, $"Estate of character {characterId}");

    private async Task OpenActorEstateAsync(NCActorId actorId)
    {
        try
        {
            var actor = await _actors.GetAsync(actorId, true);
            if (actor == null || actor.Snapshot.Lifecycle.Status != NCCityActorStatus.Dead) return;
            var decedent = NCPartyRef.ForActor(actorId);
            var estate = await _estates.OpenAsync(decedent, default, actor.Snapshot.LastAppliedEventId,
                $"Estate of {actor.Snapshot.Identity.FullName}");
            if (estate == null) return;
            var account = await _finance.GetPrimaryAccountAsync(decedent, true);
            if (account == null || account.Snapshot.Balance < 0) return;
            var asset = new NCEstateAssetRef(NCEstateAssetKind.Money, account.Snapshot.AccountId.ToString(),
                account.Snapshot.Balance, false);
            estate = await _estates.AddAssetAsync(estate.Snapshot.EstateId, estate.AggregateVersion, asset) ?? estate;
            if (account.Snapshot.Balance > 0)
            {
                var stable = estate.Snapshot.EstateId.ToString();
                var transfer = await _finance.TransferAsync(new NCPartyTransferRequest(
                    NCPersistentIdentity.Command("estate", "collect-cash", stable),
                    NCPersistentIdentity.Transaction("estate", "collect-cash", stable), decedent,
                    estate.Snapshot.EstateParty, account.Snapshot.Balance, NCLedgerTransactionKind.Transfer,
                    $"estate:{stable}", estate.Snapshot.Administrator,
                    NCPersistentIdentity.Correlation("estate", "collect-cash", stable),
                    estate.Snapshot.DeathEventId, "estate-death-workflow"));
                if (transfer.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
                    return;
            }
            estate = await _estates.GetAsync(estate.Snapshot.EstateId, true);
            if (estate != null)
                await _estates.SetAssetTransferredAsync(estate.Snapshot.EstateId, estate.AggregateVersion,
                    NCEstateAssetKind.Money, account.Snapshot.AccountId.ToString());
        }
        finally { _processing.Remove(actorId); }
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
