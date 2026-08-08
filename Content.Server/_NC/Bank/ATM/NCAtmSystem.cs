// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Popups;
using System.Threading.Tasks;
using Content.Server.Preferences.Managers;
using Content.Server.Stack;
using Content.Shared._NC.Bank.ATM;
using Content.Shared._NC.Bank.Components;
using Content.Shared._NC.Identity;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Player;

namespace Content.Server._NC.Bank.ATM;

/// <summary>
/// Exchanges physical credit stacks with persistent character accounts.
/// Authentication and balances are resolved through the database, including for offline account owners.
/// </summary>
public sealed partial class NCAtmSystem : EntitySystem
{
    [Dependency] private NCBankSystem _bank = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private StackSystem _stacks = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private readonly Dictionary<EntityUid, EntityUid> _activeUsers = new();
    private readonly Dictionary<(EntityUid Atm, EntityUid User), NCCharacterId> _authenticated = new();
    private readonly HashSet<(EntityUid Atm, EntityUid User)> _processing = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAtmComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<NCAtmComponent, EntInsertedIntoContainerMessage>(OnContainerModified);
        SubscribeLocalEvent<NCAtmComponent, EntRemovedFromContainerMessage>(OnContainerModified);
        SubscribeLocalEvent<NCAtmComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCAtmComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NCAtmComponent, NCAtmLoginMessage>(OnLogin);
        SubscribeLocalEvent<NCAtmComponent, NCAtmLogoutMessage>(OnLogout);
        SubscribeLocalEvent<NCAtmComponent, NCAtmWithdrawMessage>(OnWithdraw);
        SubscribeLocalEvent<NCAtmComponent, NCAtmDepositMessage>(OnDeposit);
    }

    private void OnInteractUsing(EntityUid uid, NCAtmComponent component, InteractUsingEvent args)
    {
        if (!TryComp<StackComponent>(args.Used, out var stack) ||
            stack.StackTypeId != _bank.Configuration.CurrencyStack ||
            !_containers.TryGetContainer(uid, NCAtmComponent.CashSlotId, out var cashContainer))
        {
            return;
        }

        if (_containers.Insert(args.Used, cashContainer))
            args.Handled = true;
    }

    private async void OnContainerModified(EntityUid uid, NCAtmComponent component, ContainerModifiedMessage args)
    {
        if (_activeUsers.TryGetValue(uid, out var user))
            await UpdateUiAsync(uid, component, user);
    }

    private async void OnUiOpened(EntityUid uid, NCAtmComponent component, BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _activeUsers[uid] = user;
        await UpdateUiAsync(uid, component, user);
    }

    private void OnUiClosed(EntityUid uid, NCAtmComponent component, BoundUIClosedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _activeUsers.Remove(uid);
        _authenticated.Remove((uid, user));
        _processing.Remove((uid, user));
    }

    private async void OnLogin(EntityUid uid, NCAtmComponent component, NCAtmLoginMessage args)
    {
        if (args.Actor is not { Valid: true } user || _activeUsers.GetValueOrDefault(uid) != user)
            return;

        var account = await _bank.AuthenticateAsync(args.AccountNumber, args.Pin);
        if (account == null)
        {
            _popup.PopupEntity(Loc.GetString("nc-atm-invalid-credentials"), uid, user);
            return;
        }

        _authenticated[(uid, user)] = account.CharacterId;
        await UpdateUiAsync(uid, component, user);
    }

    private async void OnLogout(EntityUid uid, NCAtmComponent component, NCAtmLogoutMessage args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _authenticated.Remove((uid, user));
        await UpdateUiAsync(uid, component, user);
    }

    private async void OnWithdraw(EntityUid uid, NCAtmComponent component, NCAtmWithdrawMessage args)
    {
        if (args.Actor is not { Valid: true } user ||
            !_authenticated.TryGetValue((uid, user), out var characterId))
        {
            return;
        }

        if (args.Amount <= 0 || args.Amount > component.MaximumTransaction)
        {
            _popup.PopupEntity(Loc.GetString("nc-atm-invalid-amount"), uid, user);
            return;
        }

        var operation = (uid, user);
        if (!_processing.Add(operation))
            return;

        try
        {
            var account = await _bank.TryWithdrawAsync(characterId, args.Amount);
            if (account == null)
            {
                _popup.PopupEntity(Loc.GetString("nc-atm-insufficient-funds"), uid, user);
                return;
            }

            try
            {
                _stacks.SpawnMultipleNextToOrDrop(_bank.Configuration.CurrencyStack, args.Amount, uid);
            }
            catch (Exception exception)
            {
                // A failed cash spawn is compensated immediately so the persistent account does not lose money.
                await _bank.TryDepositAsync(characterId, args.Amount);
                Log.Error($"ATM {uid} failed to spawn {args.Amount} credits: {exception}");
                _popup.PopupEntity(Loc.GetString("nc-atm-transaction-failed"), uid, user);
                return;
            }

            _popup.PopupEntity(Loc.GetString("nc-atm-withdraw-success", ("amount", args.Amount)), uid, user);
        }
        catch (Exception exception)
        {
            Log.Error($"ATM withdrawal failed for character {characterId.Value}: {exception}");
            _popup.PopupEntity(Loc.GetString("nc-atm-transaction-failed"), uid, user);
        }
        finally
        {
            _processing.Remove(operation);
            await UpdateUiAsync(uid, component, user);
        }
    }

    private async void OnDeposit(EntityUid uid, NCAtmComponent component, NCAtmDepositMessage args)
    {
        if (args.Actor is not { Valid: true } user ||
            !_authenticated.TryGetValue((uid, user), out var characterId) ||
            !_containers.TryGetContainer(uid, NCAtmComponent.CashSlotId, out var cashContainer) ||
            cashContainer.ContainedEntities.Count == 0)
        {
            return;
        }

        var operation = (uid, user);
        if (!_processing.Add(operation))
            return;

        var consumedCash = 0;
        try
        {
            var cash = cashContainer.ContainedEntities[0];
            if (!TryComp<StackComponent>(cash, out var stack) ||
                stack.StackTypeId != _bank.Configuration.CurrencyStack)
            {
                return;
            }

            var total = _stacks.GetCount((cash, stack));
            if (total <= 0 || total > component.MaximumTransaction)
            {
                _popup.PopupEntity(Loc.GetString("nc-atm-invalid-amount"), uid, user);
                return;
            }

            var tax = Math.Clamp((int) (total * component.TaxRate), 0, total);
            var deposit = total - tax;
            if (deposit <= 0)
            {
                _popup.PopupEntity(Loc.GetString("nc-atm-invalid-amount"), uid, user);
                return;
            }

            // Consume the physical stack before awaiting persistence, preventing a second deposit request from reusing it.
            _stacks.SetCount((cash, stack), 0);
            consumedCash = total;
            var account = await _bank.TryDepositAsync(characterId, deposit);
            if (account == null)
            {
                _stacks.SpawnMultipleNextToOrDrop(_bank.Configuration.CurrencyStack, total, uid);
                consumedCash = 0;
                _popup.PopupEntity(Loc.GetString("nc-atm-transaction-failed"), uid, user);
                return;
            }

            consumedCash = 0;
            _popup.PopupEntity(
                Loc.GetString("nc-atm-deposit-success", ("amount", deposit), ("tax", tax)),
                uid,
                user);
        }
        catch (Exception exception)
        {
            if (consumedCash > 0 && !Deleted(uid))
                _stacks.SpawnMultipleNextToOrDrop(_bank.Configuration.CurrencyStack, consumedCash, uid);

            Log.Error($"ATM deposit failed for character {characterId.Value}: {exception}");
            _popup.PopupEntity(Loc.GetString("nc-atm-transaction-failed"), uid, user);
        }
        finally
        {
            _processing.Remove(operation);
            await UpdateUiAsync(uid, component, user);
        }
    }

    private async Task UpdateUiAsync(EntityUid uid, NCAtmComponent component, EntityUid user)
    {
        if (Deleted(uid) || Deleted(user))
            return;

        var ownAccountNumber = string.Empty;
        if (_players.TryGetSessionByEntity(user, out var playerSession) &&
            _preferences.TryGetSelectedNCCharacterId(playerSession.UserId, out var ownCharacterId))
        {
            ownAccountNumber = (await _bank.GetAccountAsync(ownCharacterId))?.AccountNumber ?? string.Empty;
        }

        var accountNumber = string.Empty;
        var balance = 0;
        var loggedIn = false;
        if (_authenticated.TryGetValue((uid, user), out var authenticatedCharacter))
        {
            var account = await _bank.GetAccountAsync(authenticatedCharacter);
            if (account != null)
            {
                loggedIn = true;
                accountNumber = account.AccountNumber;
                balance = account.Balance;
            }
            else
            {
                _authenticated.Remove((uid, user));
            }
        }

        var depositAmount = 0;
        if (_containers.TryGetContainer(uid, NCAtmComponent.CashSlotId, out var cashContainer) &&
            cashContainer.ContainedEntities.Count > 0 &&
            TryComp<StackComponent>(cashContainer.ContainedEntities[0], out var stack) &&
            stack.StackTypeId == _bank.Configuration.CurrencyStack)
        {
            depositAmount = _stacks.GetCount((cashContainer.ContainedEntities[0], stack));
        }

        _ui.SetUiState(uid, NCAtmUiKey.Key, new NCAtmBoundUserInterfaceState(
            balance,
            accountNumber,
            loggedIn,
            component.TaxRate,
            depositAmount,
            ownAccountNumber));
    }
}
