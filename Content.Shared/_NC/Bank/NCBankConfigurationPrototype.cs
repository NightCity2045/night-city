// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Stacks;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Bank;

/// <summary>
/// Data-driven defaults shared by character accounts and cash terminals.
/// </summary>
[Prototype("ncBankConfiguration")]
public sealed partial class NCBankConfigurationPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public int StartingBalance;

    [DataField(required: true)]
    public string AccountPrefix = string.Empty;

    [DataField]
    public int PinDigits = 4;

    [DataField]
    public ProtoId<StackPrototype> CurrencyStack = "Credit";
}
