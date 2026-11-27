// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared._NC.Bank;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Bank;

/// <summary>
/// Admin bank window: department balances, manual grants and withdrawals on any account, and fee rates.
/// </summary>
public sealed class NCBankAdminEui : BaseEui
{
    public const AdminFlags RequiredFlags = AdminFlags.Admin;

    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IEntityManager _entities = default!;

    private readonly NCBankSystem _bank;
    private NCBankAdminAccountLine? _lookup;
    private string? _status;

    public NCBankAdminEui()
    {
        IoCManager.InjectDependencies(this);
        _bank = _entities.System<NCBankSystem>();
    }

    public override void Opened()
    {
        StateDirty();
    }

    public override EuiStateBase GetNewState()
    {
        var departments = new List<NCBankAdminAccountLine>();
        var fees = new List<NCBankAdminFeeLine>
        {
            new(NCBankSystem.ResidentFeeCategory, null, _bank.GetFeeBasisPoints(NCBankSystem.ResidentFeeCategory)),
        };

        foreach (var department in _prototypes.EnumeratePrototypes<NCBankDepartmentPrototype>().OrderBy(d => d.ID))
        {
            var name = _bank.GetAccountName(department.ID);
            if (department.Department != null)
                fees.Add(new NCBankAdminFeeLine(department.ID, name, _bank.GetFeeBasisPoints(department.ID)));
            if (_bank.TryGetDepartmentAccount(department.ID, out var account))
                departments.Add(new NCBankAdminAccountLine(account.Number, name, account.Balance));
        }

        return new NCBankAdminEuiState(departments, fees, _lookup, _status);
    }

    public override async void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);
        if (!_admins.HasAdminFlag(Player, RequiredFlags))
        {
            Close();
            return;
        }

        switch (msg)
        {
            case NCBankAdminEuiMsg.Refresh:
                _status = null;
                break;
            case NCBankAdminEuiMsg.Lookup lookup:
            {
                var account = await _bank.GetAccountByNumberAsync(lookup.Number);
                _lookup = account == null ? null : ToLine(account);
                _status = account == null ? Loc.GetString("nc-bank-account-not-found") : null;
                break;
            }
            case NCBankAdminEuiMsg.Adjust adjust:
            {
                var reason = adjust.Reason.Trim();
                if (reason.Length == 0)
                {
                    _status = Loc.GetString("nc-bank-admin-reason-required");
                    break;
                }

                var account = await _bank.TryAdminAdjustAsync(adjust.Number, adjust.Amount, reason, Player.Name);
                if (account == null)
                {
                    _status = Loc.GetString("nc-bank-admin-adjust-failed");
                    break;
                }

                _lookup = ToLine(account);
                _status = Loc.GetString("nc-bank-admin-adjusted", ("number", account.Number), ("amount", adjust.Amount));
                break;
            }
            case NCBankAdminEuiMsg.SetFee fee:
                _status = _bank.TrySetFeeBasisPoints(fee.Category, fee.BasisPoints)
                    ? Loc.GetString("nc-bank-admin-fee-set")
                    : Loc.GetString("nc-bank-invalid-amount");
                break;
        }

        if (!IsShutDown)
            StateDirty();
    }

    private NCBankAdminAccountLine ToLine(NCBankAccountRecord account)
    {
        var name = account.Department is { } department ? _bank.GetAccountName(department) : null;
        return new NCBankAdminAccountLine(account.Number, name, account.Balance);
    }
}

[AdminCommand(NCBankAdminEui.RequiredFlags)]
public sealed class NCBankAdminCommand : LocalizedEntityCommands
{
    [Dependency] private EuiManager _eui = default!;

    public override string Command => "ncbank";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player == null)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        _eui.OpenEui(new NCBankAdminEui(), shell.Player);
    }
}
