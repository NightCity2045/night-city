// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Services;

public enum MetaServiceStatus { Submitted, Assigned, InProgress, AwaitingPayment, Paid, Ready, Completed, Cancelled }
public enum MetaServiceAction { Begin, Ready, Complete, Cancel }
public sealed record MetaServiceInvoice(Guid Id, string Hash, long Amount, string Payee, string Description,
    bool Paid = false, Guid TransactionId = default);
public sealed record MetaServiceOrder(Guid Id, string DeskId, string Provider, string Customer, string Subject,
    string Executor, MetaServiceStatus Status, long Revision, MetaServiceInvoice? Invoice = null, string NativeReceipt = "");

/// <summary>Actor-scoped state transitions. Money and physical work require separate authoritative receipts.</summary>
public static class MetaServicePolicy
{
    public static bool CanRead(MetaServiceOrder order, string actor) =>
        actor == order.Customer || actor == order.Provider || actor == order.Executor;
    public static bool CanOperate(MetaServiceOrder order, string actor) => actor == order.Provider || actor == order.Executor;
    public static bool TryAssign(MetaServiceOrder order, string actor, string executor, out MetaServiceOrder next)
    {
        next = order;
        if (actor != order.Provider || executor.Length is < 1 or > 128 ||
            order.Status is not (MetaServiceStatus.Submitted or MetaServiceStatus.Assigned)) return false;
        next = order with { Executor = executor, Status = MetaServiceStatus.Assigned, Revision = checked(order.Revision + 1) };
        return true;
    }
    public static bool TryTransition(MetaServiceOrder order, string actor, MetaServiceAction action, out MetaServiceOrder next)
    {
        next = order;
        var allowed = action switch
        {
            MetaServiceAction.Begin => CanOperate(order, actor) && order.Status == MetaServiceStatus.Assigned,
            MetaServiceAction.Ready => CanOperate(order, actor) && order.Status is MetaServiceStatus.InProgress or MetaServiceStatus.Paid,
            MetaServiceAction.Complete => actor == order.Customer && order.Status == MetaServiceStatus.Ready &&
                (order.Invoice == null || order.Invoice.Paid),
            MetaServiceAction.Cancel => (actor == order.Customer || actor == order.Provider) && order.Invoice?.Paid != true &&
                order.Status is MetaServiceStatus.Submitted or MetaServiceStatus.Assigned or MetaServiceStatus.InProgress or MetaServiceStatus.AwaitingPayment,
            _ => false,
        };
        if (!allowed) return false;
        next = order with { Status = action switch { MetaServiceAction.Begin => MetaServiceStatus.InProgress,
            MetaServiceAction.Ready => MetaServiceStatus.Ready, MetaServiceAction.Complete => MetaServiceStatus.Completed,
            _ => MetaServiceStatus.Cancelled }, Revision = checked(order.Revision + 1) };
        return true;
    }
    public static bool TryInvoice(MetaServiceOrder order, string actor, MetaServiceInvoice invoice, out MetaServiceOrder next)
    {
        next = order;
        if (!CanOperate(order, actor) || invoice.Id == Guid.Empty || invoice.Amount is < 1 or > 1000000 ||
            invoice.Payee != order.Provider || invoice.Description.Length is < 1 or > 512 || invoice.Paid || invoice.Hash.Length != 64 ||
            order.Invoice?.Paid == true || order.Status is MetaServiceStatus.Cancelled or MetaServiceStatus.Completed) return false;
        next = order with { Invoice = invoice, Status = MetaServiceStatus.AwaitingPayment, Revision = checked(order.Revision + 1) };
        return true;
    }
    public static bool TryRecordPaid(MetaServiceOrder order, Guid invoiceId, string hash, Guid transactionId, out MetaServiceOrder next)
    {
        next = order;
        if (order.Status != MetaServiceStatus.AwaitingPayment || order.Invoice == null || order.Invoice.Paid ||
            order.Invoice.Id != invoiceId || order.Invoice.Hash != hash || transactionId == Guid.Empty) return false;
        next = order with { Status = MetaServiceStatus.Paid, Invoice = order.Invoice with { Paid = true, TransactionId = transactionId },
            Revision = checked(order.Revision + 1) };
        return true;
    }
}
