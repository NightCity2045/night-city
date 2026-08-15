// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Administration;
using Content.Server._NC.LivingCity.Employment;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._NC.LivingCity.Economy.Commands;

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCPartyInfoCommand : LocalizedEntityCommands
{
    [Dependency] private NCPartyResolverSystem _parties = default!;

    public override string Command => "alife_party_info";
    public override string Description => "Resolve one typed persistent party without materializing an entity.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !NCPartyRef.TryParse(args[0], out var party))
        {
            shell.WriteError($"Usage: {Command} <kind:id>");
            return;
        }

        var resolution = await _parties.ResolveAsync(party);
        if (!resolution.IsResolved)
        {
            shell.WriteError($"{resolution.Status}: {resolution.Error ?? "resolution failed"}");
            return;
        }

        shell.WriteLine(
            $"Party={resolution.Party}, name={resolution.DisplayName}, lifecycle={resolution.Lifecycle}, " +
            $"sourceVersion={resolution.SourceVersion}.");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCAccountInfoCommand : LocalizedEntityCommands
{
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;

    public override string Command => "alife_account_info";
    public override string Description => "Inspect one common party-owned account balance projection.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !Guid.TryParse(args[0], out var rawId) || rawId == Guid.Empty)
        {
            shell.WriteError($"Usage: {Command} <accountId>");
            return;
        }

        var account = await _ledger.GetAccountAsync(new NCAccountId(rawId), true);
        if (account == null)
        {
            shell.WriteError($"Account {rawId:D} was not found or has an unreadable snapshot.");
            return;
        }

        var snapshot = account.Snapshot;
        shell.WriteLine(
            $"Account={snapshot.AccountId}, owner={snapshot.Owner}, kind={snapshot.Kind}, " +
            $"currency={snapshot.CurrencyId}, balance={snapshot.Balance}, status={snapshot.Status}.");
        shell.WriteLine(
            $"Version={account.AggregateVersion}/{snapshot.DataVersion}, allowsNegative={snapshot.AllowsNegativeBalance}, " +
            $"created={snapshot.CreatedAt.Elapsed}, updated={snapshot.UpdatedAt.Elapsed}.");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCLedgerTransactionInfoCommand : LocalizedEntityCommands
{
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;

    public override string Command => "alife_transaction_info";
    public override string Description => "Inspect one immutable balanced ledger transaction.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !Guid.TryParse(args[0], out var rawId) || rawId == Guid.Empty)
        {
            shell.WriteError($"Usage: {Command} <transactionId>");
            return;
        }

        var transaction = await _ledger.GetTransactionAsync(new NCTransactionId(rawId), true);
        if (transaction == null)
        {
            shell.WriteError($"Transaction {rawId:D} was not found or has an unreadable snapshot.");
            return;
        }

        var snapshot = transaction.Snapshot;
        shell.WriteLine(
            $"Transaction={snapshot.TransactionId}, kind={snapshot.Kind}, currency={snapshot.CurrencyId}, " +
            $"initiator={snapshot.Initiator}, posted={snapshot.PostedAt.Elapsed}, version={transaction.AggregateVersion}.");
        shell.WriteLine($"Reference={snapshot.Reference}, legs={snapshot.Legs.Length}.");
        foreach (var leg in snapshot.Legs)
            shell.WriteLine($"  account={leg.AccountId}, amount={leg.Amount}, balanceAfter={leg.BalanceAfter}.");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCEmploymentInfoCommand : LocalizedEntityCommands
{
    [Dependency] private NCEmploymentRegistrySystem _employment = default!;

    public override string Command => "alife_employment_info";
    public override string Description => "Inspect one typed employment relation.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !Guid.TryParse(args[0], out var rawId) || rawId == Guid.Empty)
        {
            shell.WriteError($"Usage: {Command} <employmentId>");
            return;
        }

        var employment = await _employment.GetAsync(new NCEmploymentId(rawId), true);
        if (employment == null)
        {
            shell.WriteError($"Employment {rawId:D} was not found or has an unreadable snapshot.");
            return;
        }

        var snapshot = employment.Snapshot;
        shell.WriteLine(
            $"Employment={snapshot.EmploymentId}, employee={snapshot.Employee}, employer={snapshot.Employer}, " +
            $"position={snapshot.PositionId}, status={snapshot.Status}.");
        shell.WriteLine(
            $"Version={employment.AggregateVersion}/{snapshot.DataVersion}, salary={snapshot.SalaryAmount}, " +
            $"intervalTicks={snapshot.PayrollIntervalTicks}, " +
            $"nextPayroll={snapshot.NextPayrollAt?.Elapsed.ToString() ?? "none"}.");
        shell.WriteLine(
            $"EmployerAccount={snapshot.EmployerAccountId}, payeeAccount={snapshot.PayeeAccountId}, " +
            $"started={snapshot.StartedAt.Elapsed}, changed={snapshot.ChangedAt.Elapsed}.");
    }
}

