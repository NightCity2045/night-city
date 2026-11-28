// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Bank;
using Content.Shared._NC.Corporations;
using Content.Shared.Roles;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Bank;

/// <summary>Shared text helpers of the bank windows.</summary>
public static class NCBankFormat
{
    public static string Money(long amount)
    {
        return Loc.GetString("nc-bank-money", ("amount", amount));
    }

    public static string Percent(int basisPoints)
    {
        return Loc.GetString("nc-bank-percent", ("percent", basisPoints / 100f));
    }

    public static string KindName(NCBankTransactionKind kind)
    {
        return Loc.GetString(kind switch
        {
            NCBankTransactionKind.OpeningBalance => "nc-bank-kind-opening",
            NCBankTransactionKind.CashDeposit => "nc-bank-kind-deposit",
            NCBankTransactionKind.CashWithdrawal => "nc-bank-kind-withdrawal",
            NCBankTransactionKind.TransferOut => "nc-bank-kind-transfer-out",
            NCBankTransactionKind.TransferIn => "nc-bank-kind-transfer-in",
            NCBankTransactionKind.Salary => "nc-bank-kind-salary",
            NCBankTransactionKind.AdminGrant => "nc-bank-kind-admin-grant",
            NCBankTransactionKind.AdminWithdrawal => "nc-bank-kind-admin-withdrawal",
            NCBankTransactionKind.SalaryMissed => "nc-bank-kind-salary-missed",
            NCBankTransactionKind.ContractReward => "nc-bank-kind-contract",
            NCBankTransactionKind.FixerPayment => "nc-bank-kind-fixer",
            NCBankTransactionKind.Rent => "nc-bank-kind-rent",
            NCBankTransactionKind.Purchase => "nc-bank-kind-purchase",
            NCBankTransactionKind.ServicePayment => "nc-bank-kind-service-payment",
            NCBankTransactionKind.ServiceRefund => "nc-bank-kind-service-refund",
            _ => "nc-bank-kind-unknown",
        });
    }

    /// <summary>Time since the round started.</summary>
    public static string Time(NCBankHistoryLine line)
    {
        var time = line.RoundTime;
        return $"{(int) time.TotalHours:00}:{time.Minutes:00}";
    }

    public static string Line(NCBankHistoryLine line, IPrototypeManager prototypes)
    {
        var note = line.Note;
        if (line.Kind is NCBankTransactionKind.Salary or NCBankTransactionKind.SalaryMissed &&
            prototypes.TryIndex<JobPrototype>(note, out var job))
        {
            note = job.LocalizedName;
        }
        else if (line.Kind == NCBankTransactionKind.ContractReward &&
                 prototypes.TryIndex<NCCorpContractPrototype>(note, out var contract))
        {
            note = Loc.GetString(contract.Name);
        }

        var text = Loc.GetString("nc-bank-history-line",
            ("time", Time(line)),
            ("kind", KindName(line.Kind)),
            ("amount", line.Amount));
        if (line.Fee > 0)
            text += " " + Loc.GetString("nc-bank-history-fee", ("fee", line.Fee));
        if (!string.IsNullOrEmpty(line.Counterparty))
            text += " " + Loc.GetString("nc-bank-history-counterparty", ("counterparty", line.Counterparty));
        if (note.Length > 0)
            text += " " + Loc.GetString("nc-bank-history-note", ("note", note));
        return text;
    }

    public static void FillHistory(BoxContainer container, List<NCBankHistoryLine> history, IPrototypeManager prototypes)
    {
        container.RemoveAllChildren();
        if (history.Count == 0)
        {
            container.AddChild(new Label { Text = Loc.GetString("nc-bank-history-empty"), FontColorOverride = Color.DarkGray });
            return;
        }

        foreach (var line in history)
        {
            container.AddChild(new Label
            {
                Text = Line(line, prototypes),
                FontColorOverride = line.Amount >= 0 ? Color.FromHex("#9FE3A5") : Color.FromHex("#E3A59F"),
            });
        }
    }

    public static bool TryParseAmount(string text, out long amount)
    {
        return long.TryParse(text.Trim(), out amount) && amount > 0;
    }
}
