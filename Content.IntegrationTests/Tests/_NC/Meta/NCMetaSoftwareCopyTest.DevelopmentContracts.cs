// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Meta.Commerce;
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Contracts;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._NC.Meta;

public sealed partial class NCMetaSoftwareCopyTest
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task NativeDevelopmentEscrowUsesServerReceiptAndTransfersRightsWithMoney(bool automatic, bool fakePass)
    {
        var scene = await CreatePublishedPhysicalImage(nativeOwner: true, nativeSourceOwner: true);
        var developerActor = scene.SourceActor!.Value;
        await Server.WaitAssertion(() =>
        {
            var cable = SEntMan.SpawnEntity("NCMetaNetworkCable", SEntMan.GetComponent<TransformComponent>(scene.Source.Entity).Coordinates);
            var network = Server.System<NCMetaNetworkSystem>();
            Assert.That(network.TryConnectCable(cable, developerActor, scene.Source.Entity), Is.True);
            Assert.That(network.TryConnectCable(cable, developerActor, scene.Service), Is.True);
            var ui = Server.System<SharedUserInterfaceSystem>();
            Assert.That(ui.TryOpenUi(scene.Deck.Entity, NCMetaUiKey.Workspace, scene.Actor), Is.True);
            Assert.That(ui.TryOpenUi(scene.Source.Entity, NCMetaUiKey.Workspace, developerActor), Is.True);
        });
        await Server.WaitRunTicks(3);
        var finance = Server.System<NCPartyFinanceAdapterSystem>();
        Assert.That(NCPartyRef.TryParse(scene.Owner, out var buyer), Is.True);
        Assert.That(NCPartyRef.TryParse(scene.SourceOwner, out var seller), Is.True);
        var fundingCommand = Guid.NewGuid();
        var credited = await finance.AdjustWithIssuanceAsync(new(fundingCommand,
            NCPersistentIdentity.Transaction("meta", "contract-test-credit", fundingCommand.ToString("D")), buyer, 1000,
            NCLedgerTransactionKind.Adjustment, "integration development escrow", buyer, fundingCommand));
        Assert.That(credited.Status, Is.AnyOf(NCEconomyMutationStatus.Applied, NCEconomyMutationStatus.Duplicate));
        var customerDevice = (await _persistence.ReadDeviceAsync(scene.Deck.Id))!;
        var customerProject = (await _persistence.ReadProjectAsync(customerDevice.ActiveProject, scene.Owner))!;
        var fixtureId = Guid.NewGuid();
        var fixture = new MetaTestFixture("test.contract", [], [], ["native-result"], []);
        var fixtureJson = JsonSerializer.Serialize(fixture, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.That((await _persistence.SaveSourceAsync(Guid.NewGuid(), customerProject.Id, fixtureId, scene.Owner, 0,
            "tests/alarm.test.json", fixtureJson, _epoch, _time)).Success, Is.True);
        var developerProject = (await _persistence.ReadProjectAsync(scene.Project.Id, scene.SourceOwner))!;
        var code = developerProject.Files.First(file => file.Path.EndsWith(".meta", StringComparison.Ordinal));
        var source = "module test.contract; import meta.system; fn main()->void{system.log(\"" + (fakePass ? "PASS" : "native-result") + "\");}";
        Assert.That((await _persistence.SaveSourceAsync(Guid.NewGuid(), developerProject.Id, code.Id, scene.SourceOwner,
            code.Revision, code.Path, source, _epoch, _time)).Success, Is.True);
        developerProject = (await _persistence.ReadProjectAsync(scene.Project.Id, scene.SourceOwner))!;
        var compiled = new MetaCompiler().Compile(new([new(code.Id.ToString("D"), source)], "test.contract", "test.contract", "1.0.0"));
        Assert.That(compiled.Success, Is.True, string.Join("\n", compiled.Diagnostics));
        var releaseId = Guid.NewGuid();
        Assert.That((await _persistence.PublishReleaseAsync(releaseId, developerProject, compiled.Artifact!, MetaCompiler.BuildId,
            _epoch, _time, compiled.Exports)).Success, Is.True);
        var contracts = Server.System<NCMetaDevelopmentContractSystem>();
        var offer = new NCMetaDevelopmentOffer(scene.Owner, scene.SourceOwner, "Native contract", "Produce the exact agreed output",
            "A printed PASS is not acceptance", 100, 10, 0, automatic, true, [scene.Deck.Id], [fixtureId],
            compiled.Module!.Manifest.RequiredScopes.ToList(), 1024 * 1024, 1024 * 1024, 1024 * 1024);
        var offered = await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, Guid.NewGuid(),
            new(NCMetaDevelopmentAction.Offer, scene.Owner, ProjectId: customerProject.Id, Offer: offer));
        Assert.That(offered.Success, Is.True, offered.MessageKey);
        var current = (await contracts.ReadContractAsync(offered.ContractId))!;
        NCMetaDevelopmentRequest Request(NCMetaDevelopmentAction action, string owner) => new(action, owner, current.Id,
            current.Revision, current.TermsHash, current.Delivery?.Id ?? Guid.Empty, current.AcceptanceReceiptId,
            Confirm: true, AutomaticConsent: automatic);
        var consent = await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, Guid.NewGuid(), Request(NCMetaDevelopmentAction.Consent, scene.Owner));
        Assert.That(consent.Success, Is.True, consent.MessageKey);
        current = (await contracts.ReadContractAsync(current.Id))!;
        consent = await contracts.TryExecuteAsync(scene.Source.Entity, developerActor, Guid.NewGuid(), Request(NCMetaDevelopmentAction.Consent, scene.SourceOwner));
        Assert.That(consent.Success, Is.True, consent.MessageKey);
        current = (await contracts.ReadContractAsync(current.Id))!;
        var quote = await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, Guid.NewGuid(), Request(NCMetaDevelopmentAction.QuoteFunding, scene.Owner));
        Assert.That(quote.Prompt, Is.Not.Null);
        var fundRequest = Request(NCMetaDevelopmentAction.ConfirmFunding, scene.Owner) with { Nonce = quote.Prompt!.Nonce };
        var fundId = Guid.NewGuid();
        var funded = await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, fundId, fundRequest);
        Assert.That(funded.Success, Is.True, funded.MessageKey);
        Assert.That((await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, fundId, fundRequest)).Success, Is.True);
        Assert.That((await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, fundId, fundRequest with { Confirm = false })).Success, Is.False);
        Assert.That((await finance.GetPrimaryAccountAsync(buyer, true))!.Snapshot.Balance, Is.EqualTo(900));
        current = (await contracts.ReadContractAsync(current.Id))!;
        Assert.That(current.Phase, Is.EqualTo(MetaDevelopmentPhase.Funded));
        var delivered = await contracts.TryExecuteAsync(scene.Source.Entity, developerActor, Guid.NewGuid(),
            Request(NCMetaDevelopmentAction.Deliver, scene.SourceOwner) with { ProjectId = developerProject.Id, ReleaseId = releaseId });
        Assert.That(delivered.Success, Is.True, delivered.MessageKey);
        current = (await contracts.ReadContractAsync(current.Id))!;
        var claimId = NCMetaPersistenceSystem.StableGuid("development-claim:" + current.Id);
        Assert.That(await _persistence.ReadDevelopmentClaimAsync(claimId, scene.Owner), Is.Null);
        var tested = await contracts.TryExecuteAsync(scene.Source.Entity, developerActor, Guid.NewGuid(), Request(NCMetaDevelopmentAction.Test, scene.SourceOwner));
        Assert.That(tested.Success, Is.True, tested.MessageKey);
        current = (await contracts.ReadContractAsync(current.Id))!;
        var receiptRow = await _persistence.ReadSnapshotAsync("meta.development.acceptance", current.AcceptanceReceiptId.ToString("D"));
        var receipt = JsonSerializer.Deserialize<NCMetaDevelopmentReceiptRecord>(receiptRow!.Payload)!;
        Assert.That(receipt.Receipt.Status, Is.EqualTo(fakePass ? MetaDevelopmentTestStatus.Failed : MetaDevelopmentTestStatus.Passed));
        if (fakePass)
        {
            Assert.That(current.Phase, Is.EqualTo(MetaDevelopmentPhase.Delivered));
            Assert.That((await finance.GetPrimaryAccountAsync(seller, true))!.Snapshot.Balance, Is.Zero);
            Assert.That((await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, Guid.NewGuid(),
                Request(NCMetaDevelopmentAction.QuoteAcceptance, scene.Owner))).Success, Is.False);
            var disputed = await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, Guid.NewGuid(),
                Request(NCMetaDevelopmentAction.Dispute, scene.Owner) with { Text = "Agreed native fixture failed" });
            Assert.That(disputed.Success, Is.True, disputed.MessageKey);
            current = (await contracts.ReadContractAsync(current.Id))!;
            var proposed = await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, Guid.NewGuid(),
                Request(NCMetaDevelopmentAction.ProposeResolution, scene.Owner) with { CustomerAmount = 20, DeveloperAmount = 80, TransferDelivery = false });
            Assert.That(proposed.Success, Is.True, proposed.MessageKey);
            current = (await contracts.ReadContractAsync(current.Id))!;
            var accepted = await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, Guid.NewGuid(),
                Request(NCMetaDevelopmentAction.ConsentResolution, scene.Owner) with { ResolutionHash = current.Resolution!.Hash });
            Assert.That(accepted.Success, Is.True, accepted.MessageKey);
            current = (await contracts.ReadContractAsync(current.Id))!;
            accepted = await contracts.TryExecuteAsync(scene.Source.Entity, developerActor, Guid.NewGuid(),
                Request(NCMetaDevelopmentAction.ConsentResolution, scene.SourceOwner) with { ResolutionHash = current.Resolution!.Hash });
            Assert.That(accepted.Success, Is.True, accepted.MessageKey);
            Assert.That((await finance.GetPrimaryAccountAsync(buyer, true))!.Snapshot.Balance, Is.EqualTo(920));
            Assert.That((await finance.GetPrimaryAccountAsync(seller, true))!.Snapshot.Balance, Is.EqualTo(80));
            Assert.That(await _persistence.ReadDevelopmentClaimAsync(claimId, scene.Owner), Is.Null);
        }
        else
        {
            if (!automatic)
            {
                Assert.That(current.Phase, Is.EqualTo(MetaDevelopmentPhase.Delivered));
                Assert.That((await finance.GetPrimaryAccountAsync(seller, true))!.Snapshot.Balance, Is.Zero);
                var acceptQuote = await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, Guid.NewGuid(), Request(NCMetaDevelopmentAction.QuoteAcceptance, scene.Owner));
                Assert.That(acceptQuote.Prompt, Is.Not.Null);
                var acceptance = Request(NCMetaDevelopmentAction.ConfirmAcceptance, scene.Owner) with { Nonce = acceptQuote.Prompt!.Nonce };
                var acceptId = Guid.NewGuid();
                var paid = await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, acceptId, acceptance);
                Assert.That(paid.Success, Is.True, paid.MessageKey);
                Assert.That((await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, acceptId, acceptance)).Success, Is.True);
            }
            current = (await contracts.ReadContractAsync(current.Id))!;
            Assert.That(current.Phase, Is.EqualTo(MetaDevelopmentPhase.Settled));
            Assert.That((await finance.GetPrimaryAccountAsync(seller, true))!.Snapshot.Balance, Is.EqualTo(100));
            var claim = await _persistence.ReadDevelopmentClaimAsync(claimId, scene.Owner);
            Assert.That(claim, Is.Not.Null);
            Assert.That(claim!.SettlementTransactionId, Is.EqualTo(current.OutcomeTransactionId));
            Assert.That(claim.Release.CommercialOrigin!.ProductId, Is.EqualTo(current.Delivery!.ProductId));
            Assert.That(await _persistence.ReadDevelopmentClaimAsync(claimId, scene.SourceOwner), Is.Null);
            var oldDevice = (await _persistence.ReadDeviceAsync(scene.Deck.Id))!;
            var importId = Guid.NewGuid();
            var import = new NCMetaDevelopmentRequest(NCMetaDevelopmentAction.ImportSource, scene.Owner, ContractId: claimId, Text: scene.Deck.Id);
            Assert.That((await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, importId, import)).Success, Is.True);
            Assert.That((await contracts.TryExecuteAsync(scene.Deck.Entity, scene.Actor, importId, import)).Success, Is.True);
            var importedDevice = (await _persistence.ReadDeviceAsync(scene.Deck.Id))!;
            Assert.That(importedDevice.Projects.Length, Is.EqualTo(oldDevice.Projects.Length + 1));
            var project = (await _persistence.ReadProjectAsync(importedDevice.ActiveProject, scene.Owner))!;
            Assert.That(await _persistence.ReadSourceAsync(project, project.Files.Single(file => file.Path.EndsWith(".meta", StringComparison.Ordinal)).Id), Is.EqualTo(source));
        }
        var escrow = await Server.System<NCEconomyLedgerSystem>().GetAccountAsync(NCPersistentIdentity.SystemAccount("meta-development." + current.Id.ToString("D")), true);
        Assert.That(escrow!.Snapshot.Balance, Is.Zero);
    }
}
