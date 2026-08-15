// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Economy;

public sealed record NCPartyAccountResolution(
    NCPartyRef Owner,
    NCAccountId AccountId,
    NCAccountRecord? Account,
    string? Error)
{
    public bool IsResolved => Account != null && Error == null;
}

public sealed record NCPartyTransferRequest(
    Guid CommandId,
    NCTransactionId TransactionId,
    NCPartyRef Payer,
    NCPartyRef Payee,
    long Amount,
    NCLedgerTransactionKind Kind,
    string Reference,
    NCPartyRef Initiator,
    Guid CorrelationId,
    Guid? CausationId = null,
    string AuditSource = "system");

