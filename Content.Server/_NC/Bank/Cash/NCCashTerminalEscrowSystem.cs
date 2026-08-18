// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Stack;
using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Bank.Cash;

/// <summary>
/// Keeps cash entities inside a server-owned terminal container while the durable handoff is unresolved. Callers run
/// these bounded ECS operations on the main thread; database work never receives an EntityUid.
/// </summary>
public sealed partial class NCCashTerminalEscrowSystem : EntitySystem
{
    private const string EscrowContainerId = "nc_cash_handoff_escrow";

    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private StackSystem _stacks = default!;

    public NCCashEscrowCapture? CaptureDeposit(
        EntityUid terminal,
        string inputContainerId,
        ProtoId<StackPrototype> currency,
        int maximumAmount)
    {
        if (Deleted(terminal) || maximumAmount <= 0 ||
            !_containers.TryGetContainer(terminal, inputContainerId, out var input) ||
            input.ContainedEntities.Count == 0)
        {
            return null;
        }

        var cash = input.ContainedEntities[0];
        if (!TryComp<StackComponent>(cash, out var stack) || stack.StackTypeId != currency)
            return null;

        var amount = _stacks.GetCount((cash, stack));
        if (amount <= 0 || amount > maximumAmount)
            return null;

        // Moving the original stack, instead of deleting and recreating it, preserves one exact physical identity
        // until the database outcome is known and makes a second deposit of that stack impossible.
        var escrow = _containers.EnsureContainer<Container>(terminal, EscrowContainerId);
        if (!_containers.Insert(cash, escrow, force: true))
            return null;

        return new NCCashEscrowCapture(amount, [cash]);
    }

    public NCCashEscrowCapture? StageWithdrawal(
        EntityUid terminal,
        ProtoId<StackPrototype> currency,
        int amount)
    {
        if (Deleted(terminal) || amount <= 0)
            return null;

        var escrow = _containers.EnsureContainer<Container>(terminal, EscrowContainerId);
        var spawned = _stacks.SpawnMultipleNextToOrDrop(currency, amount, terminal);
        if (spawned.Count == 0)
            return null;

        foreach (var cash in spawned)
        {
            // This whole method runs in one main-thread callback, so no gameplay interaction can occur between spawn
            // and insertion into the private escrow container.
            if (_containers.Insert(cash, escrow, force: true))
                continue;

            Delete(spawned);
            return null;
        }

        return new NCCashEscrowCapture(amount, spawned);
    }

    public bool RestoreDeposit(EntityUid terminal, string inputContainerId, NCCashEscrowCapture capture)
    {
        if (Deleted(terminal) ||
            !_containers.TryGetContainer(terminal, inputContainerId, out var input))
        {
            return false;
        }

        foreach (var cash in capture.Entities)
        {
            if (Deleted(cash) || !_containers.Insert(cash, input, force: true))
                return false;
        }

        return true;
    }

    public bool ReleaseWithdrawal(EntityUid terminal, NCCashEscrowCapture capture)
    {
        if (Deleted(terminal) ||
            !_containers.TryGetContainer(terminal, EscrowContainerId, out var escrow))
        {
            return false;
        }

        foreach (var cash in capture.Entities)
        {
            if (Deleted(cash) || !escrow.Contains(cash))
                return false;
        }

        var destination = Transform(terminal).Coordinates;
        foreach (var cash in capture.Entities)
        {
            if (!_containers.Remove(cash, escrow, force: true, destination: destination))
                return false;
        }

        return true;
    }

    public void Consume(NCCashEscrowCapture capture)
    {
        Delete(capture.Entities);
    }

    private void Delete(IReadOnlyList<EntityUid> entities)
    {
        foreach (var entity in entities)
        {
            if (!Deleted(entity))
                Del(entity);
        }
    }
}

public sealed record NCCashEscrowCapture(int Amount, IReadOnlyList<EntityUid> Entities);
