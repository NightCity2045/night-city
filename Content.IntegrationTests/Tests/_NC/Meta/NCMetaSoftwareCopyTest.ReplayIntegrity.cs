// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
#if TOOLS && NC_DIAGNOSTICS
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Meta.Commerce;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaSoftwareCopyTest
{
    [TestCase(NCCommandReceiptState.Started)]
    [TestCase(NCCommandReceiptState.Failed)]
    public async Task ActualCopiedLicenseRequiresItsCompletedNativeReceiptEvenAfterAPreviousSuccessfulResolution(NCCommandReceiptState state)
    {
        var scene = await CreatePublishedPhysicalImage();
        var admission = Admission(scene);
        var copied = await NCMetaNativeTestTasks.Run(Server, () => _marketplace.TryCopySoftwareAsync(admission, () => true));
        Assert.That(copied.Success, Is.True, copied.MessageKey);
        Assert.That(copied.License?.Provenance, Is.EqualTo(MetaLicenseProvenance.NativePiracy));
        var receipt = await NCMetaNativeTestTasks.Run(Server, () => _database.GetNCPersistentCommandResultAsync(admission.ActionId));
        Assert.That(receipt?.Status, Is.EqualTo(NCPersistentMutationStatus.DuplicateCompleted));
        Assert.That(await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(scene.Owner,
            scene.Deck.Id, scene.Product.Id, scene.Release.Id, true)), Is.Not.Null, "Positive native evidence must authorize the actual protected image first.");
        var before = await NCMetaNativeTestTasks.Run(Server, async () => JsonSerializer.Serialize(new
        {
            License = await _database.GetNCAggregateSnapshotAsync("meta.software.license", copied.License!.Id.ToString("D")),
            Reserved = await _persistence.ReadReservedSoftwareCopyBytesAsync(scene.Deck.Id, scene.Owner),
        }));
        var manager = (ServerDbManager) _database;
        var changed = false;
        try
        {
            await NCMetaNativeTestTasks.Run(Server, async () =>
            {
                Assert.That(await manager.GetNCPersistentCommandResultAsync(admission.ActionId), Is.Not.Null);
                await manager.ApplyMetaIsolatedSqliteFaultAsync(context =>
                    Assert.That(context.NCCommandReceipts.Where(value => value.CommandId == admission.ActionId)
                        .ExecuteUpdate(update => update.SetProperty(value => value.State, state)), Is.EqualTo(1)));
                return true;
            });
            changed = true;
            Assert.That((await NCMetaNativeTestTasks.Run(Server, () => _database.GetNCPersistentCommandResultAsync(admission.ActionId)))!.Status,
                Is.EqualTo(state == NCCommandReceiptState.Started ? NCPersistentMutationStatus.InProgress : NCPersistentMutationStatus.DuplicateFailed));
            Assert.That(await NCMetaNativeTestTasks.Run(Server, () => _marketplace.ResolveLicensedReleaseAsync(scene.Owner,
                scene.Deck.Id, scene.Product.Id, scene.Release.Id)), Is.Null);
            Assert.That(await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(scene.Owner,
                scene.Deck.Id, scene.Product.Id, scene.Release.Id, true)), Is.Null,
                "A real copied license and reservation must not replace a Completed command receipt.");
            Assert.That(await NCMetaNativeTestTasks.Run(Server, async () => JsonSerializer.Serialize(new
            {
                License = await _database.GetNCAggregateSnapshotAsync("meta.software.license", copied.License!.Id.ToString("D")),
                Reserved = await _persistence.ReadReservedSoftwareCopyBytesAsync(scene.Deck.Id, scene.Owner),
            })), Is.EqualTo(before), "Failed corroboration must neither activate nor duplicate a seat/storage reservation.");
        }
        finally
        {
            if (changed) await NCMetaNativeTestTasks.Run(Server, async () =>
            {
                await manager.ApplyMetaIsolatedSqliteFaultAsync(context =>
                    Assert.That(context.NCCommandReceipts.Where(value => value.CommandId == admission.ActionId)
                        .ExecuteUpdate(update => update.SetProperty(value => value.State, NCCommandReceiptState.Completed)), Is.EqualTo(1)));
                return true;
            });
        }
        Assert.That(await NCMetaNativeTestTasks.Run(Server, () => _marketplace.PrepareActivationAsync(scene.Owner,
            scene.Deck.Id, scene.Product.Id, scene.Release.Id, true)), Is.Not.Null);
        Assert.That((await NCMetaNativeTestTasks.Run(Server, () => _database.GetNCPersistentCommandResultAsync(admission.ActionId)))!.ResultPayload,
            Is.EqualTo(receipt!.ResultPayload));
    }
}
#endif
