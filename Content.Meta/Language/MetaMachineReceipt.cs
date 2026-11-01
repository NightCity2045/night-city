// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Language;

/// <summary>Physical output counts accompany a durable operation; incomplete records never authorize replay.</summary>
public sealed record MetaMachineReceipt(MetaOperationReceipt Operation, int Requested, int Completed)
{
    public bool IsValid => Requested is >= 1 and <= 16 && Completed >= 0 && Completed <= Requested &&
        Enum.IsDefined(Operation.Status) && Operation.Revision > 0 &&
        (Operation.Status != MetaOperationStatus.Applied || Completed == Requested);

    public MetaMachineReceipt Transition(MetaOperationStatus status, int completed, string error = "")
    {
        var result = this with { Operation = Operation.Transition(status, error), Completed = completed };
        if (!result.IsValid || completed < Completed) throw new InvalidOperationException("Invalid native output count.");
        return result;
    }

    public MetaMachineReceipt ReconcileInterrupted() => this with { Operation = Operation.ReconcileInterrupted() };
}
