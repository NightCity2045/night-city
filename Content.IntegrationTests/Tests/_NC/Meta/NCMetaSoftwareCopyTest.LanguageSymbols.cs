// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Meta.Commerce;
using Content.Server._NC.Meta.Projects;
using Content.Shared._NC.Meta.UI;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaSoftwareCopyTest
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task SemanticPublicAbiTransferRejectsOldProjectRevisionAndActualLibrarySeatRevocation(bool revoke)
    {
        Task<T> Native<T>(Func<Task<T>> action) => NCMetaNativeTestTasks.Run(Server, action);
        var scene = await CreatePublishedPhysicalImage("""
            module test.piracy; pub struct Reading {value:int;}
            pub fn read()->Reading{return new Reading(value:7);}
            fn privateImplementation()->int{return 9;}
            """, new() { Source = MetaSourcePolicy.Closed, AllowLibraryBuild = true }, nativeOwner: true);
        var license = await SeedPurchasedSeat(scene);
        var dependency = await Native(() => _marketplace.ResolveBuildDependencyAsync(scene.Owner, scene.Deck.Id, scene.Product.Id, scene.Release.Id));
        Assert.That(dependency, Is.Not.Null);
        var device = (await Native(() => _persistence.ReadDeviceAsync(scene.Deck.Id)))!;
        var project = (await Native(() => _persistence.ReadProjectAsync(device.ActiveProject, scene.Owner)))!;
        var source = project.Files[0];
        var saved = await Native(() => _persistence.SaveSourceAsync(Guid.NewGuid(), project.Id, source.Id, scene.Owner, source.Revision,
            source.Path, "module consumer; import test.piracy as lib; fn main()->void{let value=lib.read();}", _epoch, _time));
        Assert.That(saved.Success, Is.True, saved.MessageKey);
        var reference = dependency!.Value.Reference;
        var locked = JsonSerializer.Serialize(new { version = "1.0", dependencies = new[] { new {
            productId = reference.ProductId, releaseId = reference.ReleaseId, blobDigest = reference.BlobDigest,
            artifactDigest = reference.ArtifactDigest, packageIdentity = reference.PackageIdentity, version = reference.Version } } });
        Assert.That((await Native(() => _persistence.SaveSourceAsync(Guid.NewGuid(), project.Id, Guid.NewGuid(), scene.Owner, 0,
            "meta.lock.json", locked, _epoch, _time))).Success, Is.True);
        project = (await Native(() => _persistence.ReadProjectAsync(project.Id, scene.Owner)))!;
        await Server.WaitAssertion(() => Assert.That(Server.System<SharedUserInterfaceSystem>()
            .TryOpenUi(scene.Deck.Entity, NCMetaUiKey.Workspace, scene.Actor), Is.True));
        var workspace = Server.System<NCMetaWorkspaceSystem>();
        var whole = await Native(() => workspace.ReadLanguageSymbolsAsync(scene.Deck.Entity, scene.Actor, project.Id, project.Revision));
        Assert.That(whole, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(whole!.Revoked, Is.False);
            Assert.That(whole.Complete, Is.True);
            Assert.That(whole.Json, Does.Contain("test.piracy.read"));
            Assert.That(whole.Json, Does.Not.Contain("privateImplementation"));
            Assert.That(whole.Json, Does.Not.Contain("Instructions"));
        });
        var first = await Native(() => workspace.ReadLanguageSymbolsAsync(scene.Deck.Entity, scene.Actor, project.Id, project.Revision, chunkBytes: 8));
        Assert.That(first, Is.Not.Null);
        Assert.That(first!.Complete, Is.False);
        if (revoke)
        {
            var released = await Native(() => _marketplace.TryReleaseSeatAsync(scene.Deck.Entity, scene.Actor, Guid.NewGuid(), license.Id, scene.Deck.Id));
            Assert.That(released.Success, Is.True, released.MessageKey);
            Assert.That(await Native(() => _marketplace.ResolveBuildDependencyAsync(scene.Owner, scene.Deck.Id, scene.Product.Id, scene.Release.Id)), Is.Null);
        }
        else
        {
            source = project.Files.Single(value => value.Path.EndsWith(".meta", StringComparison.Ordinal));
            Assert.That((await Native(() => _persistence.SaveSourceAsync(Guid.NewGuid(), project.Id, source.Id, scene.Owner, source.Revision,
                source.Path, "module consumer;import test.piracy as lib;fn main()->void{}", _epoch, _time))).Success, Is.True);
        }
        var denied = await Native(() => workspace.ReadLanguageSymbolsAsync(scene.Deck.Entity, scene.Actor, project.Id, project.Revision,
            first.TransferId, 8, 8));
        Assert.That(denied, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(denied!.Revoked, Is.True);
            Assert.That(denied.Json, Is.Empty, "A stale locator cannot retrieve another page of a private dependency ABI.");
        });
        var current = (await Native(() => _persistence.ReadProjectAsync(project.Id, scene.Owner)))!;
        var fresh = await Native(() => workspace.ReadLanguageSymbolsAsync(scene.Deck.Entity, scene.Actor, current.Id, current.Revision));
        Assert.That(fresh?.Revoked, Is.EqualTo(revoke));
        if (!revoke)
        {
            // A late page from the earlier request must not evict the current successful transfer.
            Assert.That(fresh!.TransferId, Is.Not.EqualTo(first.TransferId));
            await Native(() => workspace.ReadLanguageSymbolsAsync(scene.Deck.Entity, scene.Actor, project.Id, project.Revision, first.TransferId, 8, 8));
            var stillCurrent = await Native(() => workspace.ReadLanguageSymbolsAsync(scene.Deck.Entity, scene.Actor, current.Id, current.Revision,
                fresh.TransferId, 0));
            Assert.That(stillCurrent?.Revoked, Is.False);
        }
    }
}
