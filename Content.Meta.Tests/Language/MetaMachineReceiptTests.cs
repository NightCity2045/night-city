// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class MetaMachineReceiptTests
{
    private static MetaMachineReceipt Prepared() => new(new("operation", "owner", "device", "fabricator", new string('a', 64), MetaOperationStatus.Prepared, "", 1), 3, 0);
    [Test]
    public void InterruptedAdmissionNeverBecomesPermissionToRepeatMaterialDebit()
    {
        Assert.That(Prepared().ReconcileInterrupted().Operation.Status, Is.EqualTo(MetaOperationStatus.Rejected));
        var committing = Prepared().Transition(MetaOperationStatus.Committing, 0);
        Assert.That(committing.ReconcileInterrupted().Operation.Status, Is.EqualTo(MetaOperationStatus.OutcomeUnknown));
        Assert.That(committing.Transition(MetaOperationStatus.Running, 1).ReconcileInterrupted().Completed, Is.EqualTo(1));
    }
    [Test]
    public void AppliedReceiptRequiresAllRequestedPhysicalOutputsAndCountsNeverDecrease()
    {
        var running = Prepared().Transition(MetaOperationStatus.Committing, 0).Transition(MetaOperationStatus.Running, 1);
        Assert.Throws<InvalidOperationException>(() => running.Transition(MetaOperationStatus.Applied, 2));
        Assert.Throws<InvalidOperationException>(() => running.Transition(MetaOperationStatus.Rejected, 0));
        Assert.Throws<InvalidOperationException>(() => running.Transition(MetaOperationStatus.Applied, 4));
        var applied = running.Transition(MetaOperationStatus.Applied, 3);
        Assert.That(applied.IsValid, Is.True);
        Assert.That(applied.ReconcileInterrupted(), Is.EqualTo(applied));
    }
    [Test]
    public void PartialPhysicalCompletionRemainsExplicitAfterAConservativeFailure()
    {
        var partial = Prepared().Transition(MetaOperationStatus.Committing, 0).Transition(MetaOperationStatus.Running, 1)
            .Transition(MetaOperationStatus.OutcomeUnknown, 2, "OutcomeUnknown");
        Assert.That(partial.IsValid, Is.True);
        Assert.That(partial.Completed, Is.EqualTo(2));
        Assert.Throws<InvalidOperationException>(() => partial.Transition(MetaOperationStatus.Committing, 0));
    }
}
