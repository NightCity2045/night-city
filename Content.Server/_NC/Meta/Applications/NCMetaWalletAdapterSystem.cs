// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Hosting;
using Content.Meta.Runtime;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.Meta.Applications;

/// <summary>
/// The only way a META program touches money, and only through the ledger. A payment is made by the person who pressed
/// the button (the action's actor), from their own account, to the owner of a device; the program never sees a balance.
/// The owner of the receiving device can check a receipt, so a service can be paid for without trusting anyone.
/// </summary>
public sealed class NCMetaWalletAdapterSystem : EntitySystem, INCMetaDeviceAdapter
{
    private const string ReferencePrefix = "meta-wallet:";
    private const int MaximumPayment = 1000;
    private const int MaximumMemoLength = 48;

    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    private readonly Dictionary<EntityUid, MetaRateLimit> _payRates = [];

    public IEnumerable<string> Members => ["meta.economy.Wallet.pay", "meta.economy.Wallet.received", "meta.economy.Wallet.selfDevice"];

    public bool CanBind(EntityUid device, string capability) => capability == "meta.economy.Wallet" && HasComp<NCMetaHostComponent>(device);

    public override void Initialize()
    {
        base.Initialize();
        _runtime.RegisterAdapter(this);
    }

    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var type = process.GetImport(invocation.Request.ImportId).ReturnType!.Value;
        return invocation.Member.Name == "selfDevice" ? process.CreateResult(type, process.CreateString(invocation.DeviceId))
            : process.CreateErrorResult(type, "Unsupported");
    }

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var type = process.GetImport(invocation.Request.ImportId).ReturnType!.Value;
        MetaHostResult Fail(string code) => MetaHostResult.Completed(process.CreateErrorResult(type, code));
        if (invocation.Member.Name == "received")
        {
            if (!_devices.TryGetEntry(invocation.Device, out var self) || !NCPartyRef.TryParse(self.Owner, out var owner)) return Fail("NotFound");
            var receiptId = process.GetString(invocation.Request.Arguments[0]);
            _ = ReceivedAsync(invocation, type, owner, receiptId);
            return MetaHostResult.Awaiting(invocation.OperationId);
        }

        // Paying: a person, pressing a button, moves their own money to the owner of a device.
        if (invocation.Actor is not { } actor || !_devices.CanManage(invocation.Device, actor) || !_parties.TryResolve(actor, out var payer))
            return Fail("AccessDenied");
        var payeeDevice = process.GetString(invocation.Request.Arguments[0]);
        var amount = invocation.Request.Arguments[1].AsInt();
        var memo = process.GetString(invocation.Request.Arguments[2]);
        if (amount < 1 || amount > MaximumPayment || memo.Length > MaximumMemoLength || memo.Any(character => character is < ' ' or > '~') ||
            !_devices.TryResolve(payeeDevice, out var target) || !NCPartyRef.TryParse(target.Owner, out var payee) || payee == payer)
            return Fail("InvalidArgument");
        if (!_payRates.TryGetValue(actor, out var rate)) _payRates[actor] = rate = new MetaRateLimit(4, 1);
        if (!rate.TryConsume(Environment.TickCount64)) return Fail("RateLimited");
        _ = PayAsync(invocation, type, payer, payee, (int) amount, memo);
        return MetaHostResult.Awaiting(invocation.OperationId);
    }

    private async Task PayAsync(NCMetaDeviceInvocation invocation, int type, NCPartyRef payer, NCPartyRef payee, int amount, string memo)
    {
        string? receipt = null;
        var code = "IoFailure";
        try
        {
            var from = await _finance.GetPrimaryAccountAsync(payer, true);
            var to = (await _finance.EnsurePrimaryAccountAsync(payee, "meta.wallet")).Account;
            if (from is null || to is null) code = "NotFound";
            else
            {
                var commandId = Guid.NewGuid();
                var transaction = new NCTransactionId(commandId);
                var result = await _ledger.PostTransactionAsync(new(commandId, transaction, NCLedgerTransactionKind.Purchase,
                    ReferencePrefix + memo, [new(from.Snapshot.AccountId, from.AggregateVersion, -amount), new(to.Snapshot.AccountId, to.AggregateVersion, amount)],
                    payer, commandId, AuditSource: "meta.wallet"));
                if (result.Status is NCEconomyMutationStatus.Applied) receipt = transaction.Value.ToString("D");
                else code = result.Status == NCEconomyMutationStatus.InsufficientFunds ? "QuotaExceeded" : "Conflict";
            }
        }
        catch (Exception exception) { Log.Error($"META wallet payment failed: {exception}"); }
        _runtime.TryComplete(invocation.Runtime, invocation.OperationId, process => receipt is null
            ? process.CreateErrorResult(type, code) : process.CreateResult(type, Receipt(process, receipt, amount, memo)));
    }

    private async Task ReceivedAsync(NCMetaDeviceInvocation invocation, int type, NCPartyRef owner, string receiptId)
    {
        (string Id, int Amount, string Memo)? found = null;
        try
        {
            if (Guid.TryParseExact(receiptId, "D", out var id))
            {
                var record = await _ledger.GetTransactionAsync(new NCTransactionId(id), true);
                var mine = await _finance.GetPrimaryAccountAsync(owner, true);
                if (record is not null && mine is not null && record.Snapshot.Reference.StartsWith(ReferencePrefix, StringComparison.Ordinal))
                {
                    var credit = record.Snapshot.Legs.FirstOrDefault(leg => leg.Amount > 0 && leg.AccountId == mine.Snapshot.AccountId);
                    if (credit is not null && credit.Amount <= int.MaxValue)
                        found = (receiptId, (int) credit.Amount, record.Snapshot.Reference[ReferencePrefix.Length..]);
                }
            }
        }
        catch (Exception exception) { Log.Error($"META wallet receipt check failed: {exception}"); }
        _runtime.TryComplete(invocation.Runtime, invocation.OperationId, process => found is { } value
            ? process.CreateResult(type, Receipt(process, value.Id, value.Amount, value.Memo)) : process.CreateErrorResult(type, "NotFound"));
    }

    private static MetaValue Receipt(MetaProcess process, string id, int amount, string memo) =>
        process.CreateRecord(process.GetTypeId("meta.economy.PaymentReceipt"), [process.CreateString(id), MetaValue.Int(amount), process.CreateString(memo)]);
}
