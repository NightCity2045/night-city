// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace Content.Server._NC.Bank;

/// <summary>Raised (broadcast) after a successful transfer between two accounts.</summary>
[ByRefEvent]
public readonly record struct NCBankTransferredEvent(NCBankAccountRecord Payer, NCBankAccountRecord Target, long Amount);

/// <summary>Raised (broadcast) on every payday, after salaries are paid. Subscriptions are billed here.</summary>
[ByRefEvent]
public readonly record struct NCBankPaydayEvent;
