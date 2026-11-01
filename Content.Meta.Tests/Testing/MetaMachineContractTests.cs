// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaMachineContractTests
{
    [TestCase("fabricator", "industrial.Fabricator", "start(\"Wirecutter\",3,\"batch-1\")", "start(Wirecutter,3,batch-1)", 3)]
    [TestCase("grinder", "lab.Grinder", "start(lab.GrinderProgram.Grind,\"batch-1\")", "start(Grind,batch-1)", 1)]
    [TestCase("packager", "lab.ChemMaster", "pills(3,5,\"saline\",\"batch-1\")", "pills(3,5,saline,batch-1)", 3)]
    public void TypedMachineRequestsAndDurableCompletionEventsRunThroughTheVerifiedVm(string slot, string type, string call, string trace, int requested)
    {
        var module = "test.machine";
        var source = $$$"""
            module test.machine; import meta.system; import meta.industrial; import meta.lab;
            link {{{slot}}}:{{{type}}}="{{{slot}}}";
            watch {{{slot}}}.operationChanged(receipt) {
                if(receipt.status==system.OperationStatus.Applied&&receipt.completed==receipt.requested){system.log("applied");}
            }
            async fn main()->void {
                match(await {{{slot}}}.{{{call}}}) {
                    ok(receipt)=>{if(receipt.status==system.OperationStatus.Running){system.log("running");}}
                    err(error)=>{system.log("rejected");}
                }
            }
            """;
        var result = new MetaCompiler().Compile(new([new("machine.meta", source)], module));
        Assert.That(result.Success, Is.True, string.Join(',', result.Diagnostics.Select(d => d.Code + ":" + string.Join(',', d.Arguments))));
        var artifact = MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module!));
        JsonElement Receipt(string state, int count) => JsonSerializer.SerializeToElement(new
        {
            operationId = "operation", status = state, requested, completed = count, errorCode = new { tag = "none" },
            stamp = new { epoch = 1, sequence = 1, tick = 60 }
        });
        var run = MetaTestHost.Run(artifact, new(module, [new("meta." + type + "." + (type.EndsWith("ChemMaster") ? "pills" : "start"), Receipt("Running", 0), Pending: true)],
            [new(slot, "operationChanged", [Receipt("Applied", requested)], 2)], ["running", "applied"], [trace]));
        Assert.That(run.Passed, Is.True, run.FailureCode + ":" + run.Fault);
    }
}
