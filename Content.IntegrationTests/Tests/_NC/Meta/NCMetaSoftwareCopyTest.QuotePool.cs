// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Meta.Commerce;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Authority;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaSoftwareCopyTest
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ReusedNativeSoftwareQuoteSlotsPreserveOldPriceExpiryAndPurchaseAckWithoutChargingTwice(bool paid)
    {
        Task<T> Native<T>(Func<Task<T>> action) => NCMetaNativeTestTasks.Run(Server, action);
        var scene = await CreatePublishedPhysicalImage(nativeOwner: true, nativeSourceOwner: true);
        if (!paid)
        {
            await Server.WaitAssertion(() => Assert.That(_persistence.TryCaptureTime(out _epoch, out _time), Is.True));
            var changed = await Native(() => _database.ApplyNCPersistentBatchMutationAsync(new(Guid.NewGuid(), "meta.test.free-product", "meta.owner", scene.SourceOwner,
                [Write("meta.software.product", scene.Product.Id.ToString("D") + ":1", scene.Product.Revision,
                    scene.Product with { Price = 0, Revision = scene.Product.Revision + 1 })], "{}", Guid.NewGuid(), null, _epoch, _time, [])));
            Assert.That(changed.Status, Is.EqualTo(NCPersistentMutationStatus.Applied));
        }
        await Server.WaitAssertion(() => Assert.That(Server.System<SharedUserInterfaceSystem>()
            .TryOpenUi(scene.Deck.Entity, NCMetaUiKey.Workspace, scene.Actor), Is.True));
        Assert.That(await Native(() => Server.System<NCMetaAuthoritySystem>().CaptureManagementAsync(scene.Actor, scene.Owner)), Is.Not.Null);
        Assert.That(NCPartyRef.TryParse(scene.Owner, out var buyer), Is.True);
        Assert.That(NCPartyRef.TryParse(scene.SourceOwner, out var seller), Is.True);
        var finance = Server.System<NCPartyFinanceAdapterSystem>();
        var ledger = Server.System<NCEconomyLedgerSystem>();
        var credit = Guid.NewGuid();
        Assert.That((await Native(() => finance.AdjustWithIssuanceAsync(new(credit,
            NCPersistentIdentity.Transaction("meta", "quote-test-credit", credit.ToString("D")), buyer, 1000,
            NCLedgerTransactionKind.Adjustment, "native quote lifecycle", buyer, credit)))).Status,
            Is.AnyOf(NCEconomyMutationStatus.Applied, NCEconomyMutationStatus.Duplicate));
        var before = (await Native(() => finance.GetPrimaryAccountAsync(buyer, true)))!.Snapshot.Balance;
        var quoteRequest = Guid.NewGuid();
        var quoted = await Native(() => _marketplace.TryQuoteAsync(scene.Deck.Entity, scene.Actor, quoteRequest,
            scene.Product.Id, 1, scene.Release.Id, scene.Owner, MetaLicenseScope.Device, 1, [scene.Deck.Id]));
        Assert.That(quoted.Success, Is.True, quoted.MessageKey);
        var quote = quoted.Quote!;
        var slotRows = (await Native(() => _database.GetNCAggregateSnapshotPageAsync("meta.software.quote", null, 64))).Count;
        var legacyCommands = (await Native(() => _database.GetNCAggregateSnapshotPageAsync("meta.software.command", null, 64))).Count;
        Assert.That(quote.Price, Is.EqualTo(paid ? 100 : 0));
        var purchaseRequest = Guid.NewGuid();
        var purchased = await Native(() => _marketplace.TryPurchaseAsync(scene.Deck.Entity, scene.Actor, purchaseRequest, quote.Id));
        Assert.That(purchased.Success, Is.True, purchased.MessageKey);
        var after = (await Native(() => finance.GetPrimaryAccountAsync(buyer, true)))!.Snapshot.Balance;
        Assert.That(after, Is.EqualTo(before - quote.Price));
        var replacement = await Native(() => _marketplace.TryQuoteAsync(scene.Deck.Entity, scene.Actor, Guid.NewGuid(),
            scene.Product.Id, 1, scene.Release.Id, scene.Owner, MetaLicenseScope.Device, 1, [scene.Deck.Id]));
        Assert.That(replacement.Success, Is.True, replacement.MessageKey);
        Assert.That(replacement.Quote!.PoolSlot, Is.EqualTo(quote.PoolSlot));
        Assert.That(replacement.Quote.Id, Is.Not.EqualTo(quote.Id));
        var oldQuote = await Native(() => _marketplace.TryQuoteAsync(scene.Deck.Entity, scene.Actor, quoteRequest,
            scene.Product.Id, 1, scene.Release.Id, scene.Owner, MetaLicenseScope.Device, 1, [scene.Deck.Id]));
        Assert.Multiple(() =>
        {
            Assert.That(oldQuote.Success, Is.True, oldQuote.MessageKey);
            Assert.That(oldQuote.Quote!.Id, Is.EqualTo(quote.Id));
            Assert.That(oldQuote.Quote.Price, Is.EqualTo(quote.Price));
            Assert.That(oldQuote.Quote.ExpiresAtTicks, Is.EqualTo(quote.ExpiresAtTicks));
        });
        var replay = await Native(() => _marketplace.TryPurchaseAsync(scene.Deck.Entity, scene.Actor, purchaseRequest, quote.Id));
        Assert.That(replay.Success, Is.True, replay.MessageKey);
        Assert.That(replay.License!.Id, Is.EqualTo(purchased.License!.Id));
        Assert.That((await Native(() => _marketplace.TryPurchaseAsync(scene.Deck.Entity, scene.Actor, Guid.NewGuid(), quote.Id))).Success, Is.False);
        Assert.That((await Native(() => finance.GetPrimaryAccountAsync(buyer, true)))!.Snapshot.Balance, Is.EqualTo(after));
        Assert.That((await Native(() => _database.GetNCAggregateSnapshotPageAsync("meta.software.command", null, 64))).Count, Is.EqualTo(legacyCommands));
        Assert.That((await Native(() => _database.GetNCAggregateSnapshotPageAsync("meta.software.quote", null, 64))).Count, Is.EqualTo(slotRows));
        if (paid)
        {
            var command = NCPersistentIdentity.Command("meta", "software-purchase", scene.Owner + ":" + purchaseRequest);
            var transaction = NCPersistentIdentity.Transaction("meta", "software-purchase", scene.Owner + ":" + purchaseRequest);
            var receipt = (await Native(() => _database.GetNCPersistentCommandResultAsync(command)))!;
            using var document = JsonDocument.Parse(receipt.ResultPayload!);
            var acknowledged = document.RootElement.GetProperty("participantResultPayload").GetString()!;
            var payer = (await Native(() => finance.GetPrimaryAccountAsync(buyer, true)))!;
            var payee = (await Native(() => finance.GetPrimaryAccountAsync(seller, true)))!;
            var aliased = await Native(() => ledger.PostTransactionAsync(new(command, transaction, NCLedgerTransactionKind.Purchase,
                "same transaction, different acknowledgement", [new(payer.Snapshot.AccountId, payer.AggregateVersion, -1),
                    new(payee.Snapshot.AccountId, payee.AggregateVersion, 1)], buyer, purchaseRequest, ParticipantResultPayload: acknowledged + " ")));
            Assert.That(aliased.Status, Is.EqualTo(NCEconomyMutationStatus.StorageFailure));
            Assert.That((await Native(() => finance.GetPrimaryAccountAsync(buyer, true)))!.Snapshot.Balance, Is.EqualTo(after));
        }
        var legacyShape = JsonSerializer.Serialize(new NCEconomyMutationResult(NCEconomyMutationStatus.Applied, [], null, null));
        Assert.That(legacyShape, Does.Not.Contain("ParticipantResultPayload"));
        Assert.That(NCEconomyLedgerSystem.ParticipantResultFingerprint(Guid.NewGuid(), null), Is.Null);
    }
}
