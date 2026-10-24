// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Services;
using NUnit.Framework;

namespace Content.Meta.Tests.Services;

[TestFixture]
public sealed class MetaServiceOrderTests
{
    private static MetaServiceOrder Order() => new(Guid.NewGuid(), Guid.NewGuid().ToString("D"), "provider", "buyer",
        "Repair engine", "", MetaServiceStatus.Submitted, 1);
    [Test]
    public void CustomerAndProviderAuthorityCannotBeSubstitutedByStranger()
    {
        var order = Order();
        Assert.That(MetaServicePolicy.CanRead(order, "stranger"), Is.False);
        Assert.That(MetaServicePolicy.TryAssign(order, "buyer", "mechanic", out _), Is.False);
        Assert.That(MetaServicePolicy.TryAssign(order, "provider", "mechanic", out var assigned), Is.True);
        Assert.That(MetaServicePolicy.CanRead(assigned, "mechanic"), Is.True);
        Assert.That(MetaServicePolicy.TryTransition(assigned, "stranger", MetaServiceAction.Begin, out _), Is.False);
        Assert.That(MetaServicePolicy.TryTransition(assigned, "mechanic", MetaServiceAction.Begin, out var working), Is.True);
        Assert.That(working.Status, Is.EqualTo(MetaServiceStatus.InProgress));
        Assert.That(working.Revision, Is.EqualTo(3));
    }
    [Test]
    public void InvoiceIsImmutableAcrossPaymentAndCannotBeReplayedForDifferentTerms()
    {
        var order = Order();
        var invoice = new MetaServiceInvoice(Guid.NewGuid(), new string('a', 64), 100, "provider", "Engine repair");
        Assert.That(MetaServicePolicy.TryInvoice(order, "buyer", invoice, out _), Is.False);
        Assert.That(MetaServicePolicy.TryInvoice(order, "provider", invoice, out var quoted), Is.True);
        Assert.That(MetaServicePolicy.TryRecordPaid(quoted, invoice.Id, new string('b', 64), Guid.NewGuid(), out _), Is.False);
        Assert.That(MetaServicePolicy.TryRecordPaid(quoted, Guid.NewGuid(), invoice.Hash, Guid.NewGuid(), out _), Is.False);
        var payment = Guid.NewGuid();
        Assert.That(MetaServicePolicy.TryRecordPaid(quoted, invoice.Id, invoice.Hash, payment, out var paid), Is.True);
        Assert.That(paid.Invoice!.TransactionId, Is.EqualTo(payment));
        Assert.That(MetaServicePolicy.TryRecordPaid(paid, invoice.Id, invoice.Hash, payment, out _), Is.False);
        Assert.That(MetaServicePolicy.TryInvoice(paid, "provider", invoice with { Amount = 200 }, out _), Is.False);
        Assert.That(MetaServicePolicy.TryTransition(paid, "buyer", MetaServiceAction.Cancel, out _), Is.False);
    }
    [Test]
    public void CompletionRequiresCustomerAcceptanceAfterProviderReadiness()
    {
        Assert.That(MetaServicePolicy.TryAssign(Order(), "provider", "mechanic", out var assigned), Is.True);
        Assert.That(MetaServicePolicy.TryTransition(assigned, "mechanic", MetaServiceAction.Complete, out _), Is.False);
        Assert.That(MetaServicePolicy.TryTransition(assigned, "mechanic", MetaServiceAction.Begin, out var working), Is.True);
        Assert.That(MetaServicePolicy.TryTransition(working, "mechanic", MetaServiceAction.Ready, out var ready), Is.True);
        Assert.That(MetaServicePolicy.TryTransition(ready, "provider", MetaServiceAction.Complete, out _), Is.False);
        Assert.That(MetaServicePolicy.TryTransition(ready, "buyer", MetaServiceAction.Complete, out var completed), Is.True);
        Assert.That(MetaServicePolicy.TryTransition(completed, "buyer", MetaServiceAction.Cancel, out _), Is.False);
        Assert.That(MetaServicePolicy.TryTransition(completed, "mechanic", MetaServiceAction.Begin, out _), Is.False);
    }
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(1000001)]
    public void InvoiceAmountLimitsAreEnforced(long amount)
    {
        Assert.That(MetaServicePolicy.TryInvoice(Order(), "provider",
            new(Guid.NewGuid(), new string('a', 64), amount, "provider", "Repair"), out _), Is.False);
    }
}
