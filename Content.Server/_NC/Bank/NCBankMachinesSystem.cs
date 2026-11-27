// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Shared._NC.Bank;
using Content.Shared.CartridgeLoader;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.PDA;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Bank;

/// <summary>
/// ATMs, department finance terminals and the PDA bank program. All money logic lives in <see cref="NCBankSystem"/>.
/// </summary>
public sealed class NCBankMachinesSystem : EntitySystem
{
    [Dependency] private NCBankSystem _bank = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCAtmComponent, BoundUIOpenedEvent>((ent, ref _) => UpdateAtm(ent));
        SubscribeLocalEvent<NCAtmComponent, EntInsertedIntoContainerMessage>((ent, ref _) => UpdateAtm(ent));
        SubscribeLocalEvent<NCAtmComponent, EntRemovedFromContainerMessage>((ent, ref _) => UpdateAtm(ent));
        SubscribeLocalEvent<NCAtmComponent, NCBankDepositMessage>(OnAtmDeposit);
        SubscribeLocalEvent<NCAtmComponent, NCBankWithdrawMessage>(OnAtmWithdraw);

        SubscribeLocalEvent<NCBankTerminalComponent, BoundUIOpenedEvent>((ent, ref _) => UpdateTerminal(ent));
        SubscribeLocalEvent<NCBankTerminalComponent, EntInsertedIntoContainerMessage>((ent, ref _) => UpdateTerminal(ent));
        SubscribeLocalEvent<NCBankTerminalComponent, EntRemovedFromContainerMessage>((ent, ref _) => UpdateTerminal(ent));
        SubscribeLocalEvent<NCBankTerminalComponent, NCBankDepositMessage>(OnTerminalDeposit);
        SubscribeLocalEvent<NCBankTerminalComponent, NCBankWithdrawMessage>(OnTerminalWithdraw);
        SubscribeLocalEvent<NCBankTerminalComponent, NCBankSetSalaryMessage>(OnTerminalSetSalary);

        SubscribeLocalEvent<NCBankCartridgeComponent, CartridgeUiReadyEvent>(OnCartridgeReady);
        SubscribeLocalEvent<NCBankCartridgeComponent, CartridgeMessageEvent>(OnCartridgeMessage);
    }

    #region Cash slot

    private int GetCashInSlot(EntityUid machine)
    {
        return _itemSlots.GetItemOrNull(machine, NCAtmComponent.CashSlotId) is { } cash &&
               TryComp<StackComponent>(cash, out var stack) &&
               stack.StackTypeId == _bank.Configuration.CurrencyStack
            ? stack.Count
            : 0;
    }

    /// <summary>Deposits the cash in the machine slot into the account and destroys it.</summary>
    private bool TryDepositSlot(EntityUid machine, NCBankAccountRecord account, EntityUid user)
    {
        var amount = GetCashInSlot(machine);
        if (amount <= 0 || _itemSlots.GetItemOrNull(machine, NCAtmComponent.CashSlotId) is not { } cash)
        {
            _popup.PopupEntity(Loc.GetString("nc-bank-no-cash"), machine, user);
            return false;
        }

        if (!_bank.TryDeposit(account, amount, user, out var credited))
        {
            _popup.PopupEntity(Loc.GetString("nc-bank-invalid-amount"), machine, user);
            return false;
        }

        QueueDel(cash);
        _popup.PopupEntity(Loc.GetString("nc-bank-deposited", ("amount", credited), ("fee", amount - credited)), machine, user);
        return true;
    }

    private void Withdraw(EntityUid machine, NCBankAccountRecord account, long amount, EntityUid user)
    {
        if (!_bank.TryWithdrawCash(account, amount, user))
        {
            _popup.PopupEntity(Loc.GetString("nc-bank-insufficient-funds"), machine, user);
            return;
        }

        _popup.PopupEntity(Loc.GetString("nc-bank-withdrawn", ("amount", amount)), machine, user);
    }

    #endregion

    #region ATM

    private void OnAtmDeposit(Entity<NCAtmComponent> ent, ref NCBankDepositMessage args)
    {
        if (_bank.TryGetCharacterAccount(args.Actor, out var account))
            TryDepositSlot(ent, account, args.Actor);

        UpdateAtm(ent);
    }

    private void OnAtmWithdraw(Entity<NCAtmComponent> ent, ref NCBankWithdrawMessage args)
    {
        if (_bank.TryGetCharacterAccount(args.Actor, out var account))
            Withdraw(ent, account, args.Amount, args.Actor);

        UpdateAtm(ent);
    }

    private void UpdateAtm(Entity<NCAtmComponent> ent)
    {
        var cash = GetCashInSlot(ent);
        foreach (var actor in _ui.GetActors(ent.Owner, NCBankUiKey.Atm).ToList())
        {
            var state = _bank.TryGetCharacterAccount(actor, out var account)
                ? new NCAtmUiState(true, account.Number, account.Balance, _bank.GetFeeBasisPoints(account), cash)
                : new NCAtmUiState(false, string.Empty, 0, 0, cash);
            _ui.SetUiState(ent.Owner, NCBankUiKey.Atm, state);
        }
    }

    #endregion

    #region Department terminal

    private bool TryGetTerminalAccount(Entity<NCBankTerminalComponent> ent, EntityUid user, out NCBankAccountRecord account)
    {
        account = default!;
        return _bank.IsManager(user, ent.Comp.Department) &&
               _bank.TryGetDepartmentAccount(ent.Comp.Department, out account);
    }

    private void OnTerminalDeposit(Entity<NCBankTerminalComponent> ent, ref NCBankDepositMessage args)
    {
        if (TryGetTerminalAccount(ent, args.Actor, out var account))
            TryDepositSlot(ent, account, args.Actor);

        UpdateTerminal(ent);
    }

    private void OnTerminalWithdraw(Entity<NCBankTerminalComponent> ent, ref NCBankWithdrawMessage args)
    {
        if (TryGetTerminalAccount(ent, args.Actor, out var account))
            Withdraw(ent, account, args.Amount, args.Actor);

        UpdateTerminal(ent);
    }

    private void OnTerminalSetSalary(Entity<NCBankTerminalComponent> ent, ref NCBankSetSalaryMessage args)
    {
        if (TryGetTerminalAccount(ent, args.Actor, out _) &&
            !_bank.TrySetSalary(ent.Comp.Department, args.Job, args.Amount))
        {
            _popup.PopupEntity(Loc.GetString("nc-bank-invalid-amount"), ent, args.Actor);
        }

        UpdateTerminal(ent);
    }

    private void UpdateTerminal(Entity<NCBankTerminalComponent> ent)
    {
        var actor = _ui.GetActors(ent.Owner, NCBankUiKey.Terminal).Select(uid => (EntityUid?) uid).FirstOrDefault();
        if (actor == null)
            return;

        var cash = GetCashInSlot(ent);
        if (!_bank.TryGetDepartmentAccount(ent.Comp.Department, out var account))
        {
            _ui.SetUiState(ent.Owner, NCBankUiKey.Terminal,
                new NCBankTerminalUiState(false, false, null, string.Empty, 0, 0, cash, [], []));
            return;
        }

        if (!_bank.IsManager(actor.Value, ent.Comp.Department))
        {
            _ui.SetUiState(ent.Owner, NCBankUiKey.Terminal,
                new NCBankTerminalUiState(true, false, null, string.Empty, 0, 0, cash, [], []));
            return;
        }

        var salaries = _bank.GetDepartmentJobs(ent.Comp.Department)
            .Select(job => new NCBankSalaryLine(job, _bank.GetSalary(ent.Comp.Department, job)))
            .ToList();
        var department = ent.Comp.Department;
        var history = _bank.GetHistory(account);
        _ui.SetUiState(ent.Owner, NCBankUiKey.Terminal, new NCBankTerminalUiState(
            true,
            true,
            PrototypeDepartment(department),
            account.Number,
            account.Balance,
            _bank.GetFeeBasisPoints(account),
            cash,
            salaries,
            history));
    }

    private string? PrototypeDepartment(string bankDepartment)
    {
        return _prototypes.TryIndex<NCBankDepartmentPrototype>(bankDepartment, out var prototype)
            ? prototype.Department?.Id
            : null;
    }

    #endregion

    #region PDA program

    private void OnCartridgeReady(EntityUid uid, NCBankCartridgeComponent component, CartridgeUiReadyEvent args)
    {
        var actor = _ui.GetActors(args.Loader.Owner, PdaUiKey.Key).Select(uid => (EntityUid?) uid).FirstOrDefault();
        UpdateCartridge(args.Loader, actor);
    }

    private async void OnCartridgeMessage(EntityUid uid, NCBankCartridgeComponent component, CartridgeMessageEvent args)
    {
        if (args is not NCBankCartridgeTransferMessage transfer)
            return;

        var loader = GetEntity(args.LoaderUid);
        if (_bank.TryGetCharacterAccount(args.Actor, out var account))
        {
            var result = await _bank.TryTransferAsync(account, transfer.Target, transfer.Amount, args.Actor);
            if (Deleted(loader))
                return;

            _popup.PopupEntity(Loc.GetString(result switch
            {
                NCBankTransferResult.Success => "nc-bank-transfer-success",
                NCBankTransferResult.AccountNotFound => "nc-bank-account-not-found",
                NCBankTransferResult.SameAccount => "nc-bank-same-account",
                NCBankTransferResult.InsufficientFunds => "nc-bank-insufficient-funds",
                _ => "nc-bank-invalid-amount",
            }), args.Actor, args.Actor);
        }

        UpdateCartridge(loader, args.Actor);
    }

    private void UpdateCartridge(EntityUid loader, EntityUid? actor)
    {
        if (actor == null || !_bank.TryGetCharacterAccount(actor.Value, out var account))
        {
            _cartridgeLoader.UpdateCartridgeUiState(loader, new NCBankCartridgeUiState(false, string.Empty, 0, 0, []));
            return;
        }

        var history = _bank.GetHistory(account, 15);
        _cartridgeLoader.UpdateCartridgeUiState(loader, new NCBankCartridgeUiState(
            true,
            account.Number,
            account.Balance,
            _bank.GetFeeBasisPoints(account),
            history));
    }

    #endregion
}
