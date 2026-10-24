// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

[TestFixture]
public sealed class MetaDroneContractTests
{
    [Test]
    public void StorageContactsAndOperationReceiptsCrossTheVerifiedAbiWithoutEntityIdentifiers()
    {
        var result = new MetaCompiler().Compile(new([new("cargo.meta", """
            module test.cargo;import meta.drone;import meta.system;
            link eye:Perception="eye";link cargo:Manipulator="cargo";
            watch cargo.operationChanged(receipt){if(receipt.status==system.OperationStatus.Applied){system.log("event applied");}}
            async fn main()->void{
                match(cargo.holding()){ok(held)=>{if(!held){return;}}err(error)=>{return;}}
                match(eye.observe()){
                    ok(contacts)=>{for(let contact in contacts){if(contact.kind==drone.ContactKind.Storage){
                        match(await cargo.storeIn(contact.reference,"store-1")){ok(receipt)=>{if(receipt.completed==1){system.log("stored");}}err(error)=>{}}
                        match(await cargo.operationStatus("store-1")){ok(receipt)=>{system.log(receipt.operationId);}err(error)=>{}}
                        match(await cargo.cancel("store-1")){ok(receipt)=>{}err(error)=>{}}
                        return;
                    }}}
                    err(error)=>{}
                }
            }
            """)], "test.cargo"));
        Assert.That(result.Success, Is.True, string.Join(',', result.Diagnostics.Select(d => d.Code)));
        Assert.That(result.Module!.Manifest.RequiredScopes, Is.EquivalentTo(new[] { "drone.cargo.read", "drone.observe", "drone.cargo.store", "drone.operation.read", "drone.operation.cancel", "drone.operation.observe" }));
        var verified = MetaVerifier.Verify(result.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(result.Module));
        var contact = JsonSerializer.SerializeToElement(new[] { new { reference = "storage-contact", kind = "Storage", bearingDegrees = 10.0, rangeMeters = 1.0,
            confidence = 0.8, stamp = new { epoch = 1, sequence = 1, tick = 60 } } });
        var receipt = JsonSerializer.SerializeToElement(new { operationId = "durable-operation", status = "Applied", requested = 1, completed = 1,
            errorCode = new { tag = "none" }, stamp = new { epoch = 1, sequence = 2, tick = 60 } });
        var run = MetaTestHost.Run(verified, new("test.cargo", [new("meta.drone.Manipulator.holding", JsonSerializer.SerializeToElement(true)),
            new("meta.drone.Perception.observe", contact), new("meta.drone.Manipulator.storeIn", receipt, Pending: true),
            new("meta.drone.Manipulator.operationStatus", receipt, Pending: true), new("meta.drone.Manipulator.cancel", receipt, Pending: true)],
            [new("cargo", "operationChanged", [receipt], 1)], ["stored", "durable-operation", "event applied"],
            ["holding()", "observe()", "storeIn(storage-contact,store-1)", "operationStatus(store-1)", "cancel(store-1)"]));
        Assert.That(run.Passed, Is.True, run.FailureCode + ":" + run.Fault);
    }
    [TestCase("let contact:drone.ContactRef = new drone.ContactRef();")]
    [TestCase("let contact:drone.ContactRef = 1;")]
    [TestCase("match(camera.observe()){ok(contacts)=>{if(contacts.length>0){let p=contacts[0].reference;if(p==p){system.log(\"same\");}}}err(error)=>{}}")]
    public void ContactReferencesCannotBeManufacturedOrComparedBySource(string body)
    {
        var result = new MetaCompiler().Compile(new([new("bad.meta", "module test.bad;import meta.drone;import meta.system;link camera:Perception=\"camera\";fn main()->void{" + body + "}")], "test.bad"));
        Assert.That(result.Success, Is.False);
    }
    [Test]
    public void PublishedFunctionalArtifactsRetainTheExactTrustedCatalogAfterDroneCapabilitiesAreAdded()
    {
        var historical = MetaApiCatalog.ResolveTrustedCatalog(MetaApiCatalog.FunctionsCatalogHash);
        Assert.That(historical.Hash, Is.EqualTo("a55828612ad25b07d5cc1716d4155f457d531f53430c81dcc6702f6592c25405"));
        Assert.That(historical.Types.Any(t => t.Name == "meta.drone.ContactRef"), Is.False);
        var compiled = new MetaCompiler(historical).Compile(new([new("old.meta", "module test.old;import meta.implants;import meta.system;link function:FunctionalController=\"function\";fn main()->void{match(function.snapshot()){ok(state)=>{system.log(\"native\");}err(error)=>{}}}")], "test.old"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        Assert.That(() => MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!)), Throws.Nothing);
    }
}
