// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Database;
using Content.Shared._NC.Identity;

namespace Content.Server._NC.Bank;

public sealed record NCBankAccountData(
    NCCharacterId CharacterId,
    string AccountNumber,
    string Pin,
    long Balance);

public sealed record NCOrganizationBankAccountData(string OrganizationPrototypeId, long Balance);

public sealed record NCOrganizationBankTransactionData(
    long Id,
    NCOrganizationBankTransactionType Type,
    long Amount,
    long BalanceAfter,
    int? ActorProfileId,
    string ActorName,
    string Reason,
    DateTime CreatedAt);

public enum NCSalaryPaymentResult : byte
{
    Success,
    InvalidRequest,
    CharacterAccountNotFound,
    InsufficientOrganizationFunds,
    CharacterBalanceOverflow,
}

public sealed record NCSalaryPaymentData(
    NCSalaryPaymentResult Result,
    long OrganizationBalance,
    long? CharacterBalance);
