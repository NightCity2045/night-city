// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.Identity;
using Content.Shared._NC.Economy;
using Content.Shared._NC.Economy.Components;
using Content.Shared._NC.Persistence.Components;
using Content.Server._NC.Persistence;
using Content.Shared.Mind;
using Content.Shared.Verbs;
using Robust.Server.Player;

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
    [Dependency] private IPlayerManager _players = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CharacterPersistentStateLoadedEvent>(OnCharacterLoaded);
        SubscribeLocalEvent<NCBankTransferTargetComponent, GetVerbsEvent<AlternativeVerb>>(OnGetAlternativeVerbs);
        SubscribeNetworkEvent<NCBankTransferRequest>(OnTransfer);
    }

    private void OnCharacterLoaded(ref CharacterPersistentStateLoadedEvent args)
    {
        EnsureComp<NCBankTransferTargetComponent>(args.Character);
    }

    private void OnGetAlternativeVerbs(
        EntityUid uid,
        NCBankTransferTargetComponent component,
        GetVerbsEvent<AlternativeVerb> args)
    {
        if (uid == args.User ||
            !args.CanAccess ||
            !_players.TryGetSessionByEntity(args.User, out var session) ||
            !_identity.TryGetIdentity(args.User, out _, out var accountId) ||
            accountId != session.UserId ||
            !_identity.TryGetIdentity(uid, out _, out _) ||
            !TryGetState(args.User, out var sourceState) ||
            !TryGetState(uid, out var targetState) ||
            sourceState.PersonalBankAccountId == null ||
            targetState.PersonalBankAccountId == null)
        {
            return;
        }

        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("nc-bank-transfer-verb"),
            Priority = 0,
            Act = () => RaiseNetworkEvent(
                new NCBankTransferPanelState(
                    GetNetEntity(uid),
                    MetaData(uid).EntityName,
                    sourceState.PersonalBalance),
                session.Channel),
        });
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

    private bool TryGetState(
        EntityUid character,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out CharacterPersistentStateComponent? state)
    {
        state = null;
        return _mind.TryGetMind(character, out var mind, out _) &&
               TryComp(mind, out state);
    }
}
