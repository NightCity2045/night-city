// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Bytecode;
using Content.Meta.Commerce;
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Contracts;

public sealed partial class NCMetaDevelopmentContractSystem
{
    private async Task<NCMetaDevelopmentOutcome> QuotePrompt(NativeContext context, MetaDevelopmentContract contract, bool funding)
    {
        if (context.Owner != contract.Terms.Customer || funding && (contract.Phase != MetaDevelopmentPhase.Proposed ||
            !MetaDevelopmentPolicy.BothConsented(contract) || context.Time.Ticks >= contract.Terms.DeliveryDueTicks)) return Fail("action");
        if (!funding)
        {
            var receipt = await Receipt(contract);
            if (receipt == null || !MetaDevelopmentPolicy.CanSettle(contract, receipt.Receipt, true)) return Fail("technical-required");
        }
        var customer = await PartyLabel(contract.Terms.Customer); var developer = await PartyLabel(contract.Terms.Developer);
        return await Main(() =>
        {
            if (!Current(context) || _prompts.Count >= 128 || !_persistence.TryCaptureTime(out _, out var now)) return Fail("busy");
            foreach (var id in _prompts.Where(pair => pair.Value.Context.ActorEntity == context.ActorEntity &&
                         pair.Value.Prompt.ContractId == contract.Id).Select(pair => pair.Key).ToArray()) _prompts.Remove(id);
            var prompt = new NCMetaDevelopmentPrompt(Guid.NewGuid(), contract.Id, contract.Revision, contract.TermsHash,
                contract.Delivery?.Id ?? Guid.Empty, contract.AcceptanceReceiptId, funding, contract.Terms.Price, customer, developer,
                contract.Terms.Source == MetaDevelopmentSource.SourceCopyAndBuild,
                contract.Terms.Acceptance == MetaDevelopmentAcceptance.FixturesAutomatic, 30);
            _prompts.Add(prompt.Nonce, new(context, prompt, checked(now.Ticks + 30 * TimeSpan.TicksPerSecond)));
            return new NCMetaDevelopmentOutcome(true, "nc-meta-development-confirm-required", contract.Id, prompt);
        });
    }
    private async Task<NCMetaDevelopmentOutcome> Confirm(NativeContext context, Guid requestId,
        NCMetaDevelopmentRequest request, MetaDevelopmentContract contract, string fingerprint)
    {
        var valid = await Main(() =>
        {
            if (!_prompts.TryGetValue(request.Nonce, out var pending) || !Current(context) ||
                pending.Context.ActorEntity != context.ActorEntity || pending.Context.Actor != context.Actor ||
                pending.Context.Terminal != context.Terminal || pending.Context.Owner != context.Owner ||
                !ReferenceEquals(pending.Context.Connection, context.Connection) || pending.Context.ConnectedTime != context.ConnectedTime ||
                pending.Context.Epoch != context.Epoch || pending.Context.DeviceEpoch != context.DeviceEpoch ||
                pending.Context.AuthorityEpoch != context.AuthorityEpoch || !_persistence.TryCaptureTime(out _, out var now) || now.Ticks >= pending.ExpiresAt ||
                pending.Prompt.ContractId != contract.Id || pending.Prompt.Revision != contract.Revision ||
                pending.Prompt.TermsHash != contract.TermsHash || pending.Prompt.Amount != contract.Terms.Price ||
                pending.Prompt.DeliveryId != (contract.Delivery?.Id ?? Guid.Empty) || pending.Prompt.ReceiptId != contract.AcceptanceReceiptId ||
                request.DeliveryId != pending.Prompt.DeliveryId || request.ReceiptId != pending.Prompt.ReceiptId ||
                pending.Prompt.Funding != (request.Action == NCMetaDevelopmentAction.ConfirmFunding)) return -1L;
            _prompts.Remove(request.Nonce);
            return request.Confirm ? pending.ExpiresAt : -1L;
        });
        if (valid < 0 || context.Owner != contract.Terms.Customer) return Fail("confirmation");
        context = context with { ApprovalDeadline = valid };
        return request.Action == NCMetaDevelopmentAction.ConfirmFunding
            ? await Fund(context, requestId, fingerprint, contract)
            : await Pay(context, requestId, fingerprint, contract, 0, contract.Terms.Price, true, true);
    }
    private Task<NCMetaDevelopmentReceiptRecord?> Receipt(MetaDevelopmentContract contract) =>
        contract.AcceptanceReceiptId == Guid.Empty ? Task.FromResult<NCMetaDevelopmentReceiptRecord?>(null)
            : Read<NCMetaDevelopmentReceiptRecord>(ReceiptType, contract.AcceptanceReceiptId.ToString("D"), value => value.Revision);
    private async Task<NCAccountRecord?> EnsureEscrow(MetaDevelopmentContract contract)
    {
        var registered = await _directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            NCPersistentIdentity.Command("meta", "development-service", "default"), EscrowParty, Loc.GetString("nc-meta-development-escrow-name"),
            NCPersistentIdentity.Correlation("meta", "development-service", "default"), AuditSource: "meta.development"));
        if (registered.Status is not (NCPartyDirectoryMutationStatus.Applied or NCPartyDirectoryMutationStatus.Duplicate) &&
            await _directory.GetAsync(EscrowParty, true) == null) return null;
        var id = EscrowAccount(contract.Id);
        var escrow = await _ledger.GetAccountAsync(id, true);
        if (escrow == null)
        {
            await _ledger.CreateAccountAsync(new(NCPersistentIdentity.Command("meta", "development-escrow", contract.Id.ToString("D")), id,
                EscrowParty, NCAccountKind.Escrow, _finance.Configuration.CurrencyId, false,
                NCPersistentIdentity.Correlation("meta", "development-escrow", contract.Id.ToString("D")), AuditSource: "meta.development"));
            escrow = await _ledger.GetAccountAsync(id, true);
        }
        return escrow?.Snapshot.Owner == EscrowParty && escrow.Snapshot.Kind == NCAccountKind.Escrow &&
            escrow.Snapshot.Status == NCAccountStatus.Active && !escrow.Snapshot.AllowsNegativeBalance ? escrow : null;
    }
    private static NCTransactionId PaymentTransaction(string actor, Guid requestId) =>
        NCPersistentIdentity.Transaction("meta", "development", actor + ":" + requestId);
    private async Task<NCMetaDevelopmentOutcome> Fund(NativeContext context, Guid requestId, string fingerprint, MetaDevelopmentContract current, int attempt = 0)
    {
        var transaction = PaymentTransaction(context.Actor, requestId);
        if (!await Main(() => OperationalAllowed())) return Fail("test-unavailable");
        if (!MetaDevelopmentPolicy.TryFund(current, transaction.Value, context.Time.Ticks, out var funded) || context.Owner != current.Terms.Customer) return Fail("action");
        var escrow = await EnsureEscrow(current);
        if (escrow == null || escrow.Snapshot.Balance != 0) return Fail("account");
        return await PostMoney(context, requestId, fingerprint, current, funded, escrow, true, current.Terms.Price, 0, [],
            retry: nextAttempt => Fund(context, requestId, fingerprint, current, nextAttempt), attempt: attempt);
    }
    private async Task<NCMetaDevelopmentOutcome> Pay(NativeContext? context, Guid requestId, string fingerprint,
        MetaDevelopmentContract current, long customerAmount, long developerAmount, bool transfer, bool customerConfirmed,
        MetaDevelopmentContract? expected = null, NCSessionEpoch? automaticEpoch = null, int attempt = 0)
    {
        expected ??= current;
        if (customerAmount < 0 || developerAmount < 0 || (decimal) customerAmount + developerAmount != current.Terms.Price) return Fail("input");
        var writes = new List<Content.Server.Database.NCPersistentAggregateWrite>();
        if (current.Phase == MetaDevelopmentPhase.Delivered)
        {
            var receipt = await Receipt(current);
            if (!transfer || developerAmount != current.Terms.Price || receipt == null ||
                !MetaDevelopmentPolicy.CanSettle(current, receipt.Receipt, customerConfirmed && context?.Owner == current.Terms.Customer)) return Fail("technical-required");
            writes.Add(Write(ReceiptType, receipt.Receipt.Id.ToString("D"), receipt.Revision,
                receipt with { Receipt = receipt.Receipt with { Consumed = true }, Revision = receipt.Revision + 1 }));
        }
        else if (current.Phase == MetaDevelopmentPhase.Disputed)
        {
            if (context == null || current.Resolution is not { } resolution || resolution.CustomerAmount != customerAmount ||
                resolution.DeveloperAmount != developerAmount || resolution.TransferDelivery != transfer ||
                !MetaDevelopmentPolicy.ValidDistribution(current, resolution, context.Owner == current.Terms.Arbitrator)) return Fail("action");
        }
        else if (context == null || context.Owner != current.Terms.Customer || customerAmount != current.Terms.Price || transfer ||
                 !MetaDevelopmentPolicy.CanRefundUndelivered(current, context.Time.Ticks)) return Fail("action");
        var escrow = await _ledger.GetAccountAsync(EscrowAccount(current.Id), true);
        if (escrow == null || escrow.Snapshot.Owner != EscrowParty || escrow.Snapshot.Kind != NCAccountKind.Escrow ||
            escrow.Snapshot.Balance != current.Terms.Price) return Fail("account");
        var transaction = PaymentTransaction(context?.Actor ?? EscrowParty.ToString(), requestId);
        if (transfer)
        {
            if (current.Delivery == null) return Fail("action");
            if (!await Main(() => OperationalAllowed(current.Delivery.ContainerDigest, current.Delivery.ContentHash))) return Fail("test-unavailable");
            var delivery = await Read<NCMetaDevelopmentDeliveryRecord>(DeliveryType, current.Delivery.Id.ToString("D"), value => value.Revision);
            if (delivery == null || await LoadInput(current, delivery) == null) return Fail("source-rights");
            var bytes = await _database.ReadNCMetaBlobAsync(delivery.Release.Digest);
            if (bytes == null) return Fail("storage");
            var artifact = await Task.Run(() => MetaVerifier.Verify(bytes, MetaApiCatalog.Default.CreateVerificationCatalog(MetaBytecodeLoader.Load(bytes))));
            var now = await Main(() => _persistence.TryCaptureTime(out _, out var time) ? time.Ticks : -1L);
            if (now < 0) return Fail("access");
            var entitlements = await _marketplace.PrepareDevelopmentEntitlementsAsync(current, delivery.Release, artifact, transaction.Value, now);
            if (entitlements == null) return Fail("source-rights");
            var claim = await _persistence.PrepareContractDeliveryWritesAsync(current, entitlements.Release, entitlements.LicenseIds, transaction.Value);
            if (claim == null) return Fail("quota");
            writes.AddRange(entitlements.Writes.Select(value => new Content.Server.Database.NCPersistentAggregateWrite(value.EventId,
                value.EventType, value.AggregateType, value.AggregateId, value.ExpectedAggregateVersion, value.SnapshotFormatVersion, value.SnapshotPayload, value.EventPayload)));
            writes.AddRange(claim.Writes);
        }
        var next = current with { Phase = developerAmount == 0 && !transfer ? MetaDevelopmentPhase.Refunded : MetaDevelopmentPhase.Settled,
            OutcomeTransactionId = transaction.Value, Revision = expected.Revision + 1 };
        return await PostMoney(context, requestId, fingerprint, expected, next, escrow, false, customerAmount, developerAmount, writes, automaticEpoch,
            nextAttempt => Pay(context, requestId, fingerprint, current, customerAmount, developerAmount, transfer, customerConfirmed,
                expected, automaticEpoch, nextAttempt), attempt);
    }
    private async Task<NCMetaDevelopmentOutcome> PostMoney(NativeContext? context, Guid requestId, string fingerprint,
        MetaDevelopmentContract expected, MetaDevelopmentContract next, NCAccountRecord escrow, bool funding,
        long customerAmount, long developerAmount, IReadOnlyList<Content.Server.Database.NCPersistentAggregateWrite> extra,
        NCSessionEpoch? automaticEpoch = null, Func<int, Task<NCMetaDevelopmentOutcome>>? retry = null, int attempt = 0)
    {
        var reference = "meta-contract:" + fingerprint;
        var actorKey = context?.Actor ?? EscrowParty.ToString();
        var transaction = PaymentTransaction(actorKey, requestId);
        var prior = await _ledger.GetTransactionAsync(transaction, true);
        if (prior != null) return new(prior.Snapshot.Reference == reference,
            prior.Snapshot.Reference == reference ? "nc-meta-development-paid" : "nc-meta-development-request", expected.Id);
        if (!NCPartyRef.TryParse(expected.Terms.Customer, out var buyer) || !NCPartyRef.TryParse(expected.Terms.Developer, out var seller) ||
            !(await _parties.ResolveAsync(buyer, true)).IsResolved || !(await _parties.ResolveAsync(seller, true)).IsResolved) return Fail("access");
        var customer = await _finance.EnsurePrimaryAccountAsync(buyer, "meta.development");
        var developer = await _finance.EnsurePrimaryAccountAsync(seller, "meta.development");
        var proof = context != null ? await Constraints(context) : await Main(() => AutomaticConstraints(expected, automaticEpoch));
        if (!customer.IsResolved || !developer.IsResolved || proof == null || customer.Account!.Snapshot.Owner != buyer || developer.Account!.Snapshot.Owner != seller) return Fail("account");
        if (!await Admit(actorKey, requestId, fingerprint, context, expected, automaticEpoch)) return Fail("request");
        var legs = new List<NCLedgerLegRequest>();
        if (funding)
        {
            legs.Add(new(customer.AccountId, customer.Account.AggregateVersion, -expected.Terms.Price));
            legs.Add(new(escrow.Snapshot.AccountId, escrow.AggregateVersion, expected.Terms.Price));
        }
        else
        {
            legs.Add(new(escrow.Snapshot.AccountId, escrow.AggregateVersion, -expected.Terms.Price));
            if (customerAmount > 0) legs.Add(new(customer.AccountId, customer.Account.AggregateVersion, customerAmount));
            if (developerAmount > 0) legs.Add(new(developer.AccountId, developer.Account.AggregateVersion, developerAmount));
        }
        var outcome = new NCMetaDevelopmentOutcome(true, funding ? "nc-meta-development-funded" : "nc-meta-development-paid", expected.Id);
        var command = CommandId(actorKey, requestId);
        var prepared = extra.ToList();
        if (next.Phase is MetaDevelopmentPhase.Settled or MetaDevelopmentPhase.Refunded)
        {
            var removal = await PrepareCatalogRemoval(next.Id);
            if (removal == null) return Fail("storage");
            prepared.Add(removal);
        }
        var writes = prepared.Append(Write(ContractType, expected.Id.ToString("D"), expected.Revision, next))
            .Append(Write(CommandType, command.ToString("D"), 1, new NCMetaDevelopmentCommand(actorKey, fingerprint, outcome, 2))).ToArray();
        if (writes.Length > 24 || writes.Length + legs.Count + 1 > 32 ||
            writes.Select(value => (value.AggregateType, value.AggregateId)).Distinct().Count() != writes.Length) return Fail("quota");
        // Main-thread admission and ledger's initial clock capture are one synchronous prefix.
        // Its single SQL transaction then enforces the captured epoch, employment/device proof and all CAS writes.
        var transfersDelivery = next.Phase == MetaDevelopmentPhase.Settled && extra.Any(value => value.AggregateType == "meta.development.delivery-claim");
        var payment = await Main(() => (context != null ? Current(context) : AutomaticConstraints(expected, automaticEpoch) != null) &&
            (!funding || OperationalAllowed()) && (!transfersDelivery || next.Delivery is { } image && OperationalAllowed(image.ContainerDigest, image.ContentHash))
            ? _ledger.PostTransactionAsync(new(NCMetaPersistenceSystem.StableGuid("development-ledger-attempt:" + command + ":" + attempt), transaction,
                funding ? NCLedgerTransactionKind.Transfer : NCLedgerTransactionKind.Purchase,
                reference, legs.ToArray(), context?.Management.Actor ?? EscrowParty, requestId, AuditSource: "meta.development",
                ParticipantWrites: writes.Select(value => new NCAtomicLedgerParticipantWrite(value.EventId, value.EventType, value.AggregateType,
                    value.AggregateId, value.ExpectedAggregateVersion, value.SnapshotFormatVersion, value.SnapshotPayload, value.EventPayload)).ToArray(), Authority: proof))
            : Task.FromResult(new NCEconomyMutationResult(NCEconomyMutationStatus.Rejected, [], null, null)));
        var result = await payment;
        if (result.Status == NCEconomyMutationStatus.VersionConflict && retry != null && attempt < 2 &&
            await ReadContractAsync(expected.Id) is { } still && Digest(still) == Digest(expected) &&
            await Main(() => context != null ? Current(context) : AutomaticConstraints(expected, automaticEpoch) != null))
            return await retry(attempt + 1);
        return result.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate && result.Transaction?.Snapshot.Reference == reference
            ? outcome : Fail(result.Status == NCEconomyMutationStatus.InsufficientFunds ? "funds" : "revision");
    }
    private async Task<string> PartyLabel(string party) => NCPartyRef.TryParse(party, out var parsed) &&
        await _parties.ResolveAsync(parsed) is { IsResolved: true } resolved ? resolved.DisplayName : Loc.GetString("nc-meta-software-unknown-party");
}
