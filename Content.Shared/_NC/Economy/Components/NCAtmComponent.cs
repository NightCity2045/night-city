// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Content.Shared.UserInterface;

namespace Content.Shared._NC.Economy.Components;

[RegisterComponent, NetworkedComponent]
public sealed partial class NCAtmComponent : Component
{
    [DataField]
    public float DepositFee = 0.1f;

    [DataField]
    public int MaximumWithdrawal = 10000;
}

[Serializable, NetSerializable]
public enum NCAtmUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class NCAtmUiState : BoundUserInterfaceState
{
    public long Balance { get; }
    public float DepositFee { get; }
    public int MaximumWithdrawal { get; }

    public NCAtmUiState(long balance, float depositFee, int maximumWithdrawal)
    {
        Balance = balance;
        DepositFee = depositFee;
        MaximumWithdrawal = maximumWithdrawal;
    }
}

[Serializable, NetSerializable]
public sealed class NCAtmWithdrawMessage : BoundUserInterfaceMessage
{
    public int Amount { get; }
    public Guid RequestId { get; }

    public NCAtmWithdrawMessage(int amount, Guid requestId)
    {
        Amount = amount;
        RequestId = requestId;
    }
}
