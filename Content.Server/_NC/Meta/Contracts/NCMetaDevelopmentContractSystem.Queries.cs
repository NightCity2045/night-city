// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Network;
using Content.Shared._NC.Meta.UI;

namespace Content.Server._NC.Meta.Contracts;

public sealed partial class NCMetaDevelopmentContractSystem
{
    [Dependency] private NCMetaNetworkSystem _network = default!;

    public async Task<NCMetaDevelopmentState?> ReadStateAsync(EntityUid terminal, EntityUid actor,
        string owner, Guid selected, int offset = 0, string contactsQuery = "", NCMetaDevelopmentPrompt? prompt = null)
    {
        var context = await Capture(terminal, actor, owner, consumeRate: false);
        if (context == null || offset is < 0 or > 128 || contactsQuery.Length > 64) return null;
        var index = await Read<NCMetaDevelopmentPartyIndex>(IndexType, context.Owner, value => value.Revision) ?? new(context.Owner, [], 0);
        if (index.Party != context.Owner || index.Contracts.Length > 128) return null;
        var state = new NCMetaDevelopmentState { Owner = context.Owner, Offset = offset, HasMore = index.Contracts.Length > offset + 8, Prompt = prompt };
        var nativeDevice = await _persistence.ReadDeviceAsync(context.DeviceId);
        var nativeProject = nativeDevice == null ? null : await _persistence.ReadProjectAsync(nativeDevice.ActiveProject, context.Owner);
        if (nativeProject != null) state.Fixtures.AddRange(nativeProject.Files.Where(file => file.Path.EndsWith(".test.json", StringComparison.Ordinal) && file.Bytes <= 65536)
            .Take(16).Select(file => new NCMetaFileSummary(file.Id, file.Path, file.Revision, file.Bytes, file.Digest, false)));
        foreach (var id in index.Contracts.Reverse().Skip(offset).Take(8))
        {
            var value = await ReadContractAsync(id);
            if (value == null || !Participant(value, context.Owner)) continue;
            state.Rows.Add(new(value.Id, value.Revision, value.Terms.Subject, await PartyLabel(value.Terms.Customer),
                await PartyLabel(value.Terms.Developer), PhaseKey(value.Phase), value.Terms.Price));
        }
        if (selected != Guid.Empty && index.Contracts.Contains(selected) && await ReadContractAsync(selected) is { } contract && Participant(contract, context.Owner))
        {
            var receipt = await Receipt(contract);
            var actions = new List<NCMetaDevelopmentAction>();
            var isParty = context.Owner == contract.Terms.Customer || context.Owner == contract.Terms.Developer;
            if (isParty && contract.Phase == MetaDevelopmentPhase.Proposed)
            { actions.Add(NCMetaDevelopmentAction.Consent); actions.Add(NCMetaDevelopmentAction.Withdraw); }
            if (context.Owner == contract.Terms.Customer && contract.Phase == MetaDevelopmentPhase.Proposed && MetaDevelopmentPolicy.BothConsented(contract))
                actions.Add(NCMetaDevelopmentAction.QuoteFunding);
            if (context.Owner == contract.Terms.Developer && contract.Phase is MetaDevelopmentPhase.Funded or MetaDevelopmentPhase.Delivered && context.Time.Ticks < contract.Terms.DeliveryDueTicks)
                actions.Add(NCMetaDevelopmentAction.Deliver);
            if (isParty && contract.Phase == MetaDevelopmentPhase.Delivered) actions.Add(NCMetaDevelopmentAction.Test);
            if (context.Owner == contract.Terms.Customer && receipt != null && MetaDevelopmentPolicy.CanSettle(contract, receipt.Receipt, true))
                actions.Add(NCMetaDevelopmentAction.QuoteAcceptance);
            if (isParty && contract.Phase is MetaDevelopmentPhase.Funded or MetaDevelopmentPhase.Delivered) actions.Add(NCMetaDevelopmentAction.Dispute);
            if (isParty && contract.Phase == MetaDevelopmentPhase.Disputed)
            {
                actions.Add(NCMetaDevelopmentAction.ProposeResolution);
                if (contract.Resolution != null) actions.Add(NCMetaDevelopmentAction.ConsentResolution);
            }
            if (context.Owner == contract.Terms.Arbitrator && contract.Phase == MetaDevelopmentPhase.Disputed && contract.Resolution != null)
                actions.Add(NCMetaDevelopmentAction.Arbitrate);
            if (context.Owner == contract.Terms.Customer && MetaDevelopmentPolicy.CanRefundUndelivered(contract, context.Time.Ticks)) actions.Add(NCMetaDevelopmentAction.RefundUndelivered);
            var labels = await Main(() => contract.Terms.Targets.Select(target => _devices.TryResolve(target.DeviceId, out var device) && device.Owner == contract.Terms.Customer
                ? Name(device.Entity) : Loc.GetString("nc-meta-software-device-offline")).ToList());
            var consents = new List<string>();
            foreach (var consent in contract.Consents) consents.Add(await PartyLabel(consent.Party));
            state.Selected = new(contract.Id, contract.Revision, contract.TermsHash, contract.Terms.Subject, contract.Terms.Requirements,
                contract.Terms.FailureConditions, await PartyLabel(contract.Terms.Customer), await PartyLabel(contract.Terms.Developer), PhaseKey(contract.Phase),
                contract.Terms.Price, context.Time.Ticks, contract.Terms.DeliveryDueTicks, (int) (contract.Terms.SupportTicks / TimeSpan.TicksPerMinute),
                contract.Terms.Acceptance == MetaDevelopmentAcceptance.FixturesAutomatic, contract.Terms.Source == MetaDevelopmentSource.SourceCopyAndBuild,
                labels, contract.Terms.MaximumScopes.ToList(), contract.Terms.MaximumResidentBytes, contract.Terms.MaximumProcessBytes,
                contract.Terms.MaximumPackageBytes, contract.Terms.Fixtures.Select(fixture => fixture.Path).ToList(), actions,
                contract.Delivery?.Id ?? Guid.Empty, contract.AcceptanceReceiptId, receipt == null ? "nc-meta-development-test-none" : TestKey(receipt.Receipt.Status),
                contract.Resolution?.Hash ?? "", contract.Resolution?.CustomerAmount ?? 0, contract.Resolution?.DeveloperAmount ?? 0,
                contract.Resolution?.TransferDelivery ?? false, consents,
                contract.Terms.Arbitrator == null ? "" : await PartyLabel(contract.Terms.Arbitrator), contract.DisputeReason);
        }
        var inbox = await _persistence.ReadDevelopmentInboxAsync(context.Owner);
        foreach (var id in inbox.Claims.Reverse().Take(8))
            if (await _persistence.ReadDevelopmentClaimAsync(id, context.Owner) is { } claim)
                state.Inbox.Add(new(claim.Id, claim.Subject, claim.SourceCopyAndBuild));
        if (contactsQuery.Length > 0)
            state.Contacts.AddRange((await _marketplace.SearchResaleRecipientsAsync(terminal, actor, contactsQuery, 8))
                .Select(contact => new NCMetaSoftwareContactSummary(contact.Party, contact.DisplayName, "", false)));
        return await Main(() => Current(context)) ? state : null;
    }
    private async Task<NCMetaDevelopmentOutcome> ReadFixture(NativeContext context, MetaDevelopmentContract contract, NCMetaDevelopmentRequest request)
    {
        var fixture = contract.Terms.Fixtures.SingleOrDefault(value => value.Path == request.Text);
        if (fixture == null || request.Utf8Offset < 0 || request.Utf8Offset > fixture.Bytes) return Fail("input");
        var bytes = await _database.ReadNCMetaBlobAsync(fixture.Digest);
        if (bytes == null || bytes.Length != fixture.Bytes || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != fixture.Digest) return Fail("storage");
        if (request.Utf8Offset < bytes.Length && (bytes[request.Utf8Offset] & 0xc0) == 0x80) return Fail("input");
        var end = Math.Min(bytes.Length, request.Utf8Offset + 16384);
        while (end < bytes.Length && (bytes[end] & 0xc0) == 0x80) end--;
        if (!await Main(() => Current(context))) return Fail("access");
        return new(true, "nc-meta-development-refreshed", contract.Id, Fixture:
            new(fixture.Path, Utf8.GetString(bytes.AsSpan(request.Utf8Offset, end - request.Utf8Offset)), request.Utf8Offset,
                bytes.Length, fixture.Digest, end == bytes.Length, contract.Id, contract.Revision));
    }
    private async Task<NCMetaDevelopmentOutcome> ImportSource(NativeContext context, Guid requestId, NCMetaDevelopmentRequest request)
    {
        var claim = await _persistence.ReadDevelopmentClaimAsync(request.ContractId, context.Owner);
        var contract = claim == null ? null : await ReadContractAsync(claim.ContractId);
        if (claim == null || contract == null || !claim.SourceCopyAndBuild || contract.OutcomeTransactionId != claim.SettlementTransactionId ||
            contract.Phase != MetaDevelopmentPhase.Settled || contract.TermsHash != claim.TermsHash) return Fail("access");
        var capture = await Main(() => Current(context) && _devices.TryResolve(request.Text, out var target) && target.Owner == context.Owner &&
            target.Ready && _network.CanRoute(context.Terminal, target.Entity)
                ? _authority.CaptureWriteConstraints(context.Management, context.Terminal, target.Entity) : null);
        var device = capture == null ? null : await _persistence.ReadDeviceAsync(request.Text);
        if (device == null || device.Owner != context.Owner || !await Main(() => Current(context))) return Fail("access");
        var result = await _persistence.ImportDevelopmentSourceAsync(requestId, device, claim, context.Epoch, context.Time, capture!);
        return new(result.Success, result.MessageKey, claim.ContractId);
    }
    private static string PhaseKey(MetaDevelopmentPhase phase) => phase switch
    {
        MetaDevelopmentPhase.Proposed => "nc-meta-development-proposed", MetaDevelopmentPhase.Funded => "nc-meta-development-funded",
        MetaDevelopmentPhase.Delivered => "nc-meta-development-delivered", MetaDevelopmentPhase.Disputed => "nc-meta-development-disputed",
        MetaDevelopmentPhase.Settled => "nc-meta-development-settled", MetaDevelopmentPhase.Refunded => "nc-meta-development-refunded", _ => "nc-meta-development-cancelled",
    };
    private static string TestKey(MetaDevelopmentTestStatus status) => status switch
    {
        MetaDevelopmentTestStatus.Passed => "nc-meta-development-test-passed", MetaDevelopmentTestStatus.Failed => "nc-meta-development-test-failed",
        MetaDevelopmentTestStatus.Faulted => "nc-meta-development-test-faulted", MetaDevelopmentTestStatus.LimitExceeded => "nc-meta-development-test-limited",
        MetaDevelopmentTestStatus.InvalidFixture => "nc-meta-development-test-invalid", _ => "nc-meta-development-test-unavailable",
    };
}
