// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Server._NC.Meta.Domains;
using Content.Shared._NC.Medical.Surgery;

namespace Content.IntegrationTests.Tests._NC.Meta;

[TestFixture]
[TestOf(typeof(NCMetaClinicalSystem))]
public sealed class NCMetaClinicalReceiptContractTest
{
    [TestCase(false, true, MetaOperationStatus.Applied, "")]
    [TestCase(false, false, MetaOperationStatus.Rejected, "ActionRejected")]
    [TestCase(false, null, MetaOperationStatus.OutcomeUnknown, "OutcomeUnknown")]
    [TestCase(true, false, MetaOperationStatus.Rejected, "Cancelled")]
    [TestCase(true, null, MetaOperationStatus.Rejected, "Cancelled")]
    public void NativeStepOutcomeSettlesTheDurableReceiptWithoutInventingSuccess(bool cancelled, bool? applied,
        MetaOperationStatus status, string error)
    {
        var native = new NCSurgeryStepExecutionChangedEvent(default, false, cancelled, false, applied);
        var result = NCMetaClinicalSystem.ResolveProcedureOutcome(native);
        var running = new MetaOperationReceipt("operation", "owner", "device", "procedure", new string('a', 64),
            MetaOperationStatus.Prepared, "", 1).Transition(MetaOperationStatus.Committing).Transition(MetaOperationStatus.Running);
        var settled = running.Transition(result.Status, result.Error);
        Assert.Multiple(() =>
        {
            Assert.That(settled.Status, Is.EqualTo(status));
            Assert.That(settled.ErrorCode, Is.EqualTo(error));
            Assert.That(settled.Terminal, Is.True);
            Assert.That(settled.ReconcileInterrupted(), Is.SameAs(settled));
        });
        Assert.Throws<InvalidOperationException>(() => settled.Transition(MetaOperationStatus.Committing));
    }
}
