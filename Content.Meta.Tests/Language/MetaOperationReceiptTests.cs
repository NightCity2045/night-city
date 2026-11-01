// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class MetaOperationReceiptTests
{
    private static MetaOperationReceipt Prepared() => new("operation", "owner", "device", "dose", new string('a', 64), MetaOperationStatus.Prepared, "", 1);

    [TestCase(MetaOperationStatus.Committing)]
    [TestCase(MetaOperationStatus.Running)]
    public void IncompletePhysicalOperationNeverBecomesRetryPermission(MetaOperationStatus state)
    {
        var receipt = Prepared().Transition(MetaOperationStatus.Committing);
        if (state == MetaOperationStatus.Running) receipt = receipt.Transition(state);
        var recovered = receipt.ReconcileInterrupted();
        Assert.Multiple(() =>
        {
            Assert.That(recovered.Status, Is.EqualTo(MetaOperationStatus.OutcomeUnknown));
            Assert.That(recovered.ErrorCode, Is.EqualTo("OutcomeUnknown"));
            Assert.That(recovered.Revision, Is.EqualTo(receipt.Revision + 1));
            Assert.That(recovered.Terminal, Is.True);
        });
        Assert.That(recovered.ReconcileInterrupted(), Is.SameAs(recovered));
        Assert.Throws<InvalidOperationException>(() => recovered.Transition(MetaOperationStatus.Committing));
    }
    [Test]
    public void InterruptedPreparationIsCancelledBeforeAnyPhysicalAction()
    {
        var receipt = Prepared().ReconcileInterrupted();
        Assert.That(receipt.Status, Is.EqualTo(MetaOperationStatus.Rejected));
        Assert.That(receipt.ErrorCode, Is.EqualTo("Cancelled"));
        Assert.Throws<InvalidOperationException>(() => Prepared().Transition(MetaOperationStatus.Applied));
    }
    [TestCase(MetaOperationStatus.Applied, "")]
    [TestCase(MetaOperationStatus.Rejected, "ActionRejected")]
    public void NativeOutcomeIsTerminalAndCannotBeReplayed(MetaOperationStatus status, string error)
    {
        var receipt = Prepared().Transition(MetaOperationStatus.Committing).Transition(MetaOperationStatus.Running).Transition(status, error);
        Assert.That(receipt.Status, Is.EqualTo(status));
        Assert.That(receipt.ErrorCode, Is.EqualTo(error));
        Assert.That(receipt.ReconcileInterrupted(), Is.SameAs(receipt));
        Assert.Throws<InvalidOperationException>(() => receipt.Transition(MetaOperationStatus.Committing));
    }
    [Test]
    public void FramedFingerprintBindsEveryScopeAndPayloadField()
    {
        var hash = MetaOperationReceipt.HashFields("owner", "device", "dose", "key", "epoch", "1");
        foreach (var index in Enumerable.Range(0, 6))
        {
            var fields = new[] { "owner", "device", "dose", "key", "epoch", "1" }; fields[index] += "changed";
            Assert.That(MetaOperationReceipt.HashFields(fields), Is.Not.EqualTo(hash));
        }
        Assert.That(MetaOperationReceipt.HashFields("a|b", "c"), Is.Not.EqualTo(MetaOperationReceipt.HashFields("a", "b|c")));
        Assert.That(MetaOperationReceipt.IsValidKey("dose_123-safe"), Is.True);
        Assert.That(MetaOperationReceipt.IsValidKey("../foreign"), Is.False);
        Assert.That(MetaOperationReceipt.IsValidKey(new string('a', 65)), Is.False);
    }
    [Test]
    public void ClinicalContractsCompileAndIndependentlyVerifyOpaquePatientFlow()
    {
        const string source = """
            module clinic.program;
            import meta.medical;
            link monitor:medical.MedicalMonitor="bed";
            link dosing:medical.DosingController="pump";
            link procedure:medical.ProcedureController="console";
            async fn main()->void {
                match (monitor.patient()) {
                    ok(patient) => {
                        let report=monitor.snapshot(patient);
                        let dose=await dosing.dose(patient,"dose-1");
                        let step=await procedure.startSelectedStep(patient,"step-1");
                        let status=await procedure.operationStatus("step-1");
                    }
                    err(error) => {}
                }
            }
            """;
        var compiled = new MetaCompiler().Compile(new([new("clinic.meta", source)], "clinic.program"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        Assert.DoesNotThrow(() => MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!)));
        var forged = new MetaCompiler().Compile(new([new("clinic.meta", "module clinic.program; import meta.medical; fn main()->void{let p=new medical.PatientRef{};}")], "clinic.program"));
        Assert.That(forged.Success, Is.False);
    }
}
