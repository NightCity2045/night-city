// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Popups;
using Content.Server.Stack;
using Content.Server._NC.Identity;
using Content.Shared._NC.Economy.Components;
using Content.Shared._NC.Persistence.Components;
using Content.Shared.Interaction;
using Content.Shared.Mind;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Economy;

/// <summary>
/// Converts physical cash and the actor's own durable personal account.
/// No account number, PIN, or arbitrary account identifier is accepted from the client.
/// </summary>
public sealed partial class NCAtmSystem : EntitySystem
{
    private const string CashStackType = "Credit";

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private CharacterIdentitySystem _identity = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private StackSystem _stacks = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAtmComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCAtmComponent, NCAtmWithdrawMessage>(OnWithdraw);
        SubscribeLocalEvent<NCAtmComponent, InteractUsingEvent>(OnInteractUsing);
    }

    private void OnUiOpened(EntityUid uid, NCAtmComponent component, BoundUIOpenedEvent args)
    {
        if (args.Actor is { } actor)
            UpdateUi(uid, component, actor);
    }

    private async void OnWithdraw(
        EntityUid uid,
        NCAtmComponent component,
        NCAtmWithdrawMessage message)
    {
        var actor = message.Actor;
        if (message.Amount <= 0 ||
            message.Amount > component.MaximumWithdrawal ||
            !TryGetAccount(actor, out var profileId, out var accountId, out var state))
        {
            return;
        }

        var result = await _database.ChangeNCCashBalanceAsync(
            state.PersonalBankAccountId!.Value,
            message.Amount,
            false,
            accountId,
            profileId,
            _ticker.RoundId,
            message.RequestId);
        if (!result.Success)
        {
            _popup.PopupEntity(Loc.GetString(result.Error ?? "nc-bank-error-invalid-transfer"), uid, actor);
            return;
        }

        state.PersonalBalance = result.DebitBalance;
        SpawnCash(message.Amount, Transform(uid).Coordinates);
        _popup.PopupEntity(
            Loc.GetString("nc-atm-withdraw-success", ("amount", message.Amount)),
            uid,
            actor);
        UpdateUi(uid, component, actor);
    }

    private async void OnInteractUsing(
        EntityUid uid,
        NCAtmComponent component,
        InteractUsingEvent args)
    {
        if (args.Handled ||
            !TryComp<StackComponent>(args.Used, out var stack) ||
            stack.StackTypeId != CashStackType ||
            !TryGetAccount(args.User, out var profileId, out var accountId, out var state))
        {
            return;
        }

        var physicalAmount = _stacks.GetCount((args.Used, stack));
        var fee = Math.Clamp(component.DepositFee, 0f, 0.99f);
        var creditedAmount = (int) Math.Floor(physicalAmount * (1f - fee));
        if (creditedAmount <= 0)
        {
            _popup.PopupEntity(Loc.GetString("nc-atm-deposit-too-small"), uid, args.User);
            return;
        }

        // Reserve the entire stack before crossing the async database boundary.
        // A failed database operation restores the same physical amount at the ATM.
        var cashCoordinates = Transform(uid).Coordinates;
        _stacks.SetCount((args.Used, stack), 0);
        args.Handled = true;

        var result = await _database.ChangeNCCashBalanceAsync(
            state.PersonalBankAccountId!.Value,
            creditedAmount,
            true,
            accountId,
            profileId,
            _ticker.RoundId,
            Guid.NewGuid());
        if (!result.Success)
        {
            SpawnCash(physicalAmount, cashCoordinates);
            _popup.PopupEntity(Loc.GetString(result.Error ?? "nc-bank-error-invalid-transfer"), uid, args.User);
            return;
        }

        state.PersonalBalance = result.CreditBalance;
        _popup.PopupEntity(
            Loc.GetString(
                "nc-atm-deposit-success",
                ("amount", creditedAmount),
                ("fee", physicalAmount - creditedAmount)),
            uid,
            args.User);
        UpdateUi(uid, component, args.User);
    }

    private void UpdateUi(EntityUid uid, NCAtmComponent component, EntityUid actor)
    {
        if (!TryGetAccount(actor, out _, out _, out var state))
            return;

        _ui.SetUiState(
            uid,
            NCAtmUiKey.Key,
            new NCAtmUiState(
                state.PersonalBalance,
                component.DepositFee,
                component.MaximumWithdrawal));
    }

    private bool TryGetAccount(
        EntityUid actor,
        out int profileId,
        out Guid accountId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out CharacterPersistentStateComponent? state)
    {
        profileId = default;
        accountId = default;
        state = null;
        if (!_identity.TryGetIdentity(actor, out var profile, out var account) ||
            !_mind.TryGetMind(actor, out var mind, out _) ||
            !TryComp(mind, out state) ||
            state.PersonalBankAccountId == null)
        {
            return false;
        }

        profileId = profile.Value;
        accountId = account.UserId;
        return true;
    }

    private void SpawnCash(int amount, EntityCoordinates coordinates)
    {
        _stacks.SpawnMultipleAtPosition(
            _prototypes.Index<StackPrototype>(CashStackType),
            amount,
            coordinates);
    }
}
