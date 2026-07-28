using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.Identity;
using Content.Shared._NC.Economy;
using Content.Shared._NC.Persistence.Components;
using Content.Shared.Mind;

namespace Content.Server._NC.Economy;

/// <summary>
/// Server-authoritative personal transfers. Clients identify only an online body;
/// durable profile and account identifiers never cross the network boundary.
/// </summary>
public sealed partial class BankSystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private CharacterIdentitySystem _identity = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private GameTicker _ticker = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCBankTransferRequest>(OnTransfer);
    }

    private async void OnTransfer(NCBankTransferRequest request, EntitySessionEventArgs args)
    {
        var sourceEntity = args.SenderSession.AttachedEntity;
        var targetEntity = GetEntity(request.Target);
        if (sourceEntity == null ||
            !Exists(targetEntity) ||
            !_identity.TryGetIdentity(sourceEntity.Value, out var sourceProfile, out var sourceAccount) ||
            sourceAccount != args.SenderSession.UserId ||
            !_identity.TryGetIdentity(targetEntity, out _, out _) ||
            !_mind.TryGetMind(sourceEntity.Value, out var sourceMind, out _) ||
            !_mind.TryGetMind(targetEntity, out var targetMind, out _) ||
            !TryComp<CharacterPersistentStateComponent>(sourceMind, out var sourceState) ||
            !TryComp<CharacterPersistentStateComponent>(targetMind, out var targetState) ||
            sourceState.PersonalBankAccountId == null ||
            targetState.PersonalBankAccountId == null)
        {
            RaiseNetworkEvent(new NCBankStateEvent(0, "nc-bank-error-unavailable-account"),
                args.SenderSession.Channel);
            return;
        }

        var result = await _database.TransferNCFundsAsync(
            sourceState.PersonalBankAccountId.Value,
            targetState.PersonalBankAccountId.Value,
            request.Amount,
            NCBankTransactionType.Transfer,
            request.Reason.Trim(),
            sourceAccount.UserId,
            sourceProfile.Value,
            _ticker.RoundId,
            request.RequestId);

        if (result.Success)
        {
            sourceState.PersonalBalance = result.DebitBalance;
            targetState.PersonalBalance = result.CreditBalance;
        }

        RaiseNetworkEvent(new NCBankStateEvent(sourceState.PersonalBalance, result.Error),
            args.SenderSession.Channel);
    }
}
