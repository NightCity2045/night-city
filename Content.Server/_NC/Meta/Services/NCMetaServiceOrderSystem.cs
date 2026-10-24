// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using System.Text.Json;
using System.IO;
using Content.Meta.Runtime;
using Content.Meta.Services;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Applications;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.Meta.Services;

[RegisterComponent]
public sealed partial class NCMetaServiceDeskComponent : Component;

/// <summary>Generic service orders. Scripts never supply payer identity or directly authorize a bank transfer.</summary>
public sealed partial class NCMetaServiceOrderSystem : EntitySystem, INCMetaDeviceAdapter
{
    private const string OrderType = "meta.service-order";
    private const string BoardType = "meta.service-board";
    private sealed record Board(string DeskId, string Provider, Guid[] Orders, long Revision);
    private sealed record Request(NCMetaDeviceInvocation Invocation, EntityUid Actor, string Party, string Provider,
        string DeskId, MetaValue User, MetaValue Session, Guid ActionId, string Method, string[] Text, long Number, long Revision);
    private sealed record Response(MetaServiceOrder? Order = null, MetaServiceOrder[]? Orders = null, Guid Confirmation = default, string? Error = null);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16 };
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaSnapshotSystem _snapshots = default!;
    [Dependency] private NCMetaApplicationSystem _apps = default!;
    [Dependency] private NCEntityPartyResolverSystem _parties = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private ITaskManager _tasks = default!;
    private int _pending;
    public IEnumerable<string> Members => ["meta.services.ServiceOrders.create", "meta.services.ServiceOrders.listOrders", "meta.services.ServiceOrders.cachedList",
        "meta.services.ServiceOrders.get", "meta.services.ServiceOrders.assign", "meta.services.ServiceOrders.transition",
        "meta.services.ServiceOrders.invoice", "meta.services.ServiceOrders.requestPayment"];
    public override void Initialize() { base.Initialize(); _runtime.RegisterAdapter(this); _runtime.RuntimeStopped += OnRuntimeStopped; }
    public override void Shutdown() { _runtime.RuntimeStopped -= OnRuntimeStopped; _cachedOrders.Clear(); base.Shutdown(); }
    public bool CanBind(EntityUid device, string capability) => capability == "meta.services.ServiceOrders" && HasComp<NCMetaServiceDeskComponent>(device);
    public MetaValue Read(NCMetaDeviceInvocation invocation) => ReadCachedOrders(invocation);

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        MetaHostResult Error(string code) => MetaHostResult.Completed(process.CreateErrorResult(process.GetImport(invocation.Request.ImportId).ReturnType!.Value, code));
        var args = invocation.Request.Arguments;
        if (_pending >= 32 || args.Count < 2 || !CanBind(invocation.Device, invocation.Member.Owner) ||
            !_apps.TryResolveAppActor(invocation.Runtime, args[0], args[1], out var actor) || !_parties.TryResolve(actor, out var party))
            return Error("AccessDenied");
        var method = invocation.Member.Name;
        var actionId = Guid.Empty;
        if (method is not ("get" or "listOrders") &&
            !_apps.TryGetAppActionId(invocation.Runtime, args[0], args[1], out actionId)) return Error("AccessDenied");
        var strings = new List<string>();
        long number = 0, revision = 0;
        for (var index = 2; index < args.Count; index++)
        {
            if (args[index].Kind == MetaValueKind.String)
            {
                var value = process.GetString(args[index]);
                if (Encoding.UTF8.GetByteCount(value) > 1024) return Error("InvalidArgument");
                strings.Add(value);
            }
        }
        if (method is "assign") revision = args[4].AsInt();
        if (method is "transition") { number = (long) args[3].Payload; revision = args[4].AsInt(); }
        if (method is "invoice") { number = args[3].AsInt(); revision = args[5].AsInt(); }
        var request = new Request(invocation, actor, party.ToString(), invocation.Runtime.Owner, invocation.DeviceId,
            args[0], args[1], actionId, method, strings.ToArray(), number, revision);
        _pending++;
        _ = ExecuteAndReplyAsync(request);
        return MetaHostResult.Awaiting(invocation.OperationId);
    }
    private bool Current(Request request) => _runtime.CanContinue(request.Invocation) &&
        _apps.TryResolveAppActor(request.Invocation.Runtime, request.User, request.Session, out var actor) && actor == request.Actor &&
        _parties.TryResolve(actor, out var party) && party.ToString() == request.Party;
    private async Task ExecuteAndReplyAsync(Request request)
    {
        Response response;
        using var lease = _persistence.TryBeginNativeMutation();
        try { response = lease == null ? new(Error: "Busy") : await ExecuteAsync(request); }
        catch (Exception exception) { Log.Error($"META service order failed: {exception}"); response = new(Error: "IoFailure"); }
        await Main(() =>
        {
            _pending--;
            if (Current(request))
            {
                if (response.Orders != null) CacheOrders(request, response.Orders);
                _runtime.TryComplete(request.Invocation.Runtime, request.Invocation.OperationId, process => Encode(process, request, response));
            }
            return true;
        });
    }
    private async Task<Response> ExecuteAsync(Request request)
    {
        if (!await Main(() => Current(request))) return new(Error: "PermissionRevoked");
        if (request.Method == "listOrders")
        {
            var board = await ReadBoard(request.DeskId, request.Provider);
            var orders = new List<MetaServiceOrder>();
            foreach (var id in board.Orders.Reverse().Take(256))
            {
                var order = await ReadOrder(id);
                if (order != null && order.DeskId == request.DeskId && order.Provider == request.Provider && MetaServicePolicy.CanRead(order, request.Party)) orders.Add(order);
                if (orders.Count == 32) break;
            }
            return new(Orders: orders.ToArray());
        }
        if (request.Method == "create") return await CreateAsync(request);
        var orderId = await ResolveOrderReference(request, request.Text[0]);
        if (orderId == null) return new(Error: "NotFound");
        var current = await ReadOrder(orderId.Value);
        if (current == null || current.DeskId != request.DeskId || current.Provider != request.Provider || !MetaServicePolicy.CanRead(current, request.Party))
            return new(Error: "NotFound");
        if (request.Method == "get") return new(current);
        if (request.Method == "requestPayment")
        {
            if (current.Customer != request.Party || current.Invoice is not { Paid: false } invoice || current.Status != MetaServiceStatus.AwaitingPayment)
                return new(Error: "Interlock");
            return await Main(() => Current(request) && _apps.TryRequestInvoiceConfirmation(request.Invocation.Runtime, request.User, request.Session,
                    invoice.Id, invoice.Hash, invoice.Amount, invoice.Payee,
                    (actor, id, hash, amount, payee, confirmation, canCommit) => PayAsync(current.Id, request.Party, actor, id, hash, amount, payee, confirmation, canCommit), out var confirmation)
                ? new Response(Confirmation: confirmation) : new Response(Error: "Busy"));
        }
        var key = request.Text[^1];
        if (key.Length is < 1 or > 64) return new(Error: "InvalidArgument");
        var command = CommandId(request, key);
        var hashInput = NCMetaPersistenceSystem.Fingerprint(request.Party, request.DeskId, request.Method, JsonSerializer.Serialize(request.Text), request.Number.ToString(), request.Revision.ToString());
        var replay = await Replay(command, request.Party, hashInput);
        if (replay != null) return replay;
        if (current.Revision != request.Revision) return new(Error: "StaleState");
        MetaServiceOrder next = current;
        bool valid;
        if (request.Method == "assign")
        {
            var executor = request.Text[1] == "self" ? request.Party : request.Text[1];
            valid = NCPartyRef.TryParse(executor, out _) && MetaServicePolicy.TryAssign(current, request.Party, executor, out next!);
        }
        else if (request.Method == "transition")
            valid = request.Number is >= 0 and <= 3 && MetaServicePolicy.TryTransition(current, request.Party, (MetaServiceAction) request.Number, out next!);
        else if (request.Method == "invoice")
        {
            var invoiceId = NCMetaPersistenceSystem.StableGuid("service-invoice:" + command);
            var hash = NCMetaPersistenceSystem.Fingerprint(invoiceId.ToString("D"), current.Id.ToString("D"), current.Provider, request.Number.ToString(), request.Text[1]);
            valid = MetaServicePolicy.TryInvoice(current, request.Party, new(invoiceId, hash, request.Number, current.Provider, request.Text[1]), out next!);
        }
        else return new(Error: "Unsupported");
        if (!valid) return new(Error: "Interlock");
        return await PersistAsync(request, command, hashInput, current, next!);
    }
    private async Task<Response> CreateAsync(Request request)
    {
        if (request.Text.Length != 2 || request.Text[0].Length is < 1 or > 256 || request.Text[1].Length is < 1 or > 64)
            return new(Error: "InvalidArgument");
        var command = CommandId(request, request.Text[1]);
        var fingerprint = NCMetaPersistenceSystem.Fingerprint(request.DeskId, request.Party, "create", request.Text[0]);
        var replay = await Replay(command, request.Party, fingerprint);
        if (replay != null) return replay;
        var order = new MetaServiceOrder(command, request.DeskId, request.Provider, request.Party, request.Text[0], "", MetaServiceStatus.Submitted, 1);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var board = await ReadBoard(request.DeskId, request.Provider);
            if (board.Orders.Length >= 256) return new(Error: "QuotaExceeded");
            if (!await Main(() => Current(request))) return new(Error: "PermissionRevoked");
            var clock = await Main(() => _persistence.TryCaptureTime(out var epoch, out var now) ? (epoch, now) : default);
            var result = await _database.ApplyNCPersistentBatchMutationAsync(new(command, "meta.service.create", "meta.owner", request.Party,
                [new(Guid.NewGuid(), "meta.service.created", OrderType, order.Id.ToString("D"), 0, 1, JsonSerializer.Serialize(order, Json), "{}"),
                 new(Guid.NewGuid(), "meta.service.indexed", BoardType, request.DeskId, board.Revision, 1,
                     JsonSerializer.Serialize(board with { Orders = board.Orders.Append(order.Id).ToArray(), Revision = board.Revision + 1 }, Json), "{}")],
                JsonSerializer.Serialize(order, Json), command, null, clock.epoch, clock.now, [], RequestFingerprint: fingerprint));
            if (result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted)
            { await Notify(request.Invocation.Device, order); return new(JsonSerializer.Deserialize<MetaServiceOrder>(result.ResultPayload!, Json)); }
            if (result.Status != NCPersistentMutationStatus.VersionConflict) return new(Error: "IoFailure");
        }
        return new(Error: "Conflict");
    }
    private async Task<Response> PersistAsync(Request request, Guid command, string fingerprint, MetaServiceOrder old, MetaServiceOrder next)
    {
        var clock = await Main(() => Current(request) && _persistence.TryCaptureTime(out var epoch, out var now) ? (Valid: true, epoch, now) : default);
        if (!clock.Valid) return new(Error: "PermissionRevoked");
        var result = await _database.ApplyNCPersistentMutationAsync(new(command, "meta.service.change", "meta.owner", request.Party,
            Guid.NewGuid(), "meta.service.changed", OrderType, old.Id.ToString("D"), old.Revision, 1,
            JsonSerializer.Serialize(next, Json), "{}", JsonSerializer.Serialize(next, Json), command, null, clock.epoch, clock.now, [], RequestFingerprint: fingerprint));
        if (result.Status is not (NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted)) return new(Error: "Conflict");
        var committed = JsonSerializer.Deserialize<MetaServiceOrder>(result.ResultPayload!, Json);
        if (committed != null) await Notify(request.Invocation.Device, committed);
        return new(committed);
    }
    private async Task<Response?> Replay(Guid command, string party, string fingerprint)
    {
        var receipt = await _database.GetNCPersistentCommandResultAsync(command);
        if (receipt == null) return null;
        if (!(receipt.CommandType is "meta.service.create" or "meta.service.change" &&
            await _persistence.CanReplayCommandAsync(receipt, command, receipt.CommandType, party, fingerprint) &&
            receipt.ResultPayload is { Length: <= 16384 } payload)) return new(Error: "Conflict");
        var order = JsonSerializer.Deserialize<MetaServiceOrder>(payload, Json);
        return order != null ? new(order) : new(Error: "Conflict");
    }
    private async Task<NCMetaInvoicePaymentResult> PayAsync(Guid orderId, string customer, EntityUid actor, Guid invoiceId,
        string invoiceHash, long amount, string payee, Guid confirmation, Func<bool> canCommit)
    {
        var transactionId = NCPersistentIdentity.Transaction("meta", "service-payment", confirmation.ToString("D"));
        var fingerprint = NCMetaPersistenceSystem.Fingerprint(orderId.ToString("D"), customer, invoiceId.ToString("D"), invoiceHash, amount.ToString(), payee);
        var previous = await _ledger.GetTransactionAsync(transactionId, true);
        if (previous != null)
        {
            var matched = previous.Snapshot.Reference == "meta-service:" + fingerprint;
            return new(matched, matched ? "nc-meta-service-payment-confirmed" : "nc-meta-service-payment-rejected");
        }
        var order = await ReadOrder(orderId);
        if (order == null || order.Customer != customer || order.Provider != payee || order.Invoice is not { Paid: false } invoice ||
            invoice.Amount != amount || invoice.Id != invoiceId || invoice.Hash != invoiceHash || customer == payee ||
            !NCPartyRef.TryParse(customer, out var buyer) || !NCPartyRef.TryParse(payee, out var provider) ||
            !MetaServicePolicy.TryRecordPaid(order, invoiceId, invoiceHash, transactionId.Value, out var paid))
            return new(false, "nc-meta-service-payment-rejected");
        var payer = await _finance.GetPrimaryAccountAsync(buyer, true);
        var recipient = await _finance.GetPrimaryAccountAsync(provider, true);
        if (payer == null || recipient == null || payer.Snapshot.Owner != buyer || recipient.Snapshot.Owner != provider ||
            !await Main(() => canCommit() && _parties.TryResolve(actor, out var fresh) && fresh == buyer))
            return new(false, "nc-meta-service-payment-rejected");
        var result = await _ledger.PostTransactionAsync(new(confirmation, transactionId, NCLedgerTransactionKind.Purchase,
            "meta-service:" + fingerprint,
            [new(payer.Snapshot.AccountId, payer.AggregateVersion, -amount), new(recipient.Snapshot.AccountId, recipient.AggregateVersion, amount)],
            buyer, confirmation, AuditSource: "meta.service",
            ParticipantWrites: [new(Guid.NewGuid(), "meta.service.paid", OrderType, order.Id.ToString("D"), order.Revision, 1, JsonSerializer.Serialize(paid, Json), "{}")]));
        if (result.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate)
            await Main(() =>
            {
                if (_devices.TryResolve(order.DeskId, out var desk))
                {
                    InvalidateOrders(order.DeskId);
                    _runtime.TryPublishEvent(desk.Entity, "meta.services.ServiceOrders.orderChanged", process => process.CreateRecord(
                        process.GetTypeId("meta.services.OrderChange"), [process.CreateString(paid.Id.ToString("D")), MetaValue.Int(paid.Revision)]));
                }
                return true;
            });
        return new(result.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate,
            result.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate ? "nc-meta-service-payment-confirmed" : "nc-meta-service-payment-rejected");
    }
    private async Task<MetaServiceOrder?> ReadOrder(Guid id)
    {
        var row = await _snapshots.ReadCurrentAsync(OrderType, id.ToString("D"));
        if (row == null || row.Payload.Length > 16384) return null;
        try
        {
            var value = JsonSerializer.Deserialize<MetaServiceOrder>(row.Payload, Json);
            return value != null && value.Id == id && value.Revision == row.AggregateVersion && Enum.IsDefined(value.Status) &&
                NCPartyRef.TryParse(value.Customer, out _) && NCPartyRef.TryParse(value.Provider, out _) && value.Subject.Length <= 256 ? value : null;
        }
        catch (JsonException) { return null; }
    }
    private async Task<Board> ReadBoard(string id, string provider)
    {
        var row = await _snapshots.ReadCurrentAsync(BoardType, id);
        if (row == null) return new(id, provider, [], 0);
        if (row.Payload.Length > 32768) throw new InvalidDataException("Service board size limit.");
        var value = JsonSerializer.Deserialize<Board>(row.Payload, Json);
        if (value is not { Orders.Length: <= 256 } || value.DeskId != id || value.Provider != provider || value.Revision != row.AggregateVersion)
            throw new InvalidDataException("Invalid service board.");
        return value;
    }
    private Task Notify(EntityUid device, MetaServiceOrder order) => Main(() =>
    {
        InvalidateOrders(order.DeskId);
        _runtime.TryPublishEvent(device, "meta.services.ServiceOrders.orderChanged", process => process.CreateRecord(
            process.GetTypeId("meta.services.OrderChange"), [process.CreateString(order.Id.ToString("D")), MetaValue.Int(order.Revision)]));
        return true;
    });
    private static MetaValue Encode(MetaProcess process, Request request, Response response)
    {
        var resultId = process.GetImport(request.Invocation.Request.ImportId).ReturnType!.Value;
        if (response.Error != null) return process.CreateErrorResult(resultId, response.Error);
        MetaValue value;
        if (response.Orders != null)
        {
            var arrayType = request.Invocation.Runtime.Artifact.GetType(resultId).Args[0];
            value = process.CreateArray(arrayType, response.Orders.Select(order => EncodeOrder(process, order)).ToArray());
        }
        else if (response.Order != null) value = EncodeOrder(process, response.Order);
        else value = process.CreateRecord(process.GetTypeId("meta.services.PaymentRequest"), [process.CreateString(response.Confirmation.ToString("D")), MetaValue.Bool(true)]);
        return process.CreateResult(resultId, value);
    }
    private static MetaValue EncodeOrder(MetaProcess process, MetaServiceOrder order) => process.CreateRecord(process.GetTypeId("meta.services.Order"),
        [process.CreateString(order.Id.ToString("D")), process.CreateString(order.Subject), process.CreateString(order.Customer), process.CreateString(order.Executor),
         process.CreateEnum(process.GetTypeId("meta.services.OrderStatus"), (int) order.Status), MetaValue.Int(order.Revision),
         process.CreateString(order.Invoice?.Id.ToString("D") ?? ""), process.CreateString(order.Invoice?.Hash ?? ""),
         MetaValue.Int(order.Invoice?.Amount ?? 0), MetaValue.Bool(order.Invoice?.Paid == true)]);
    private Task<T> Main<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() => { try { completion.SetResult(action()); } catch (Exception exception) { completion.SetException(exception); } });
        return completion.Task;
    }
}
