// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Content.Meta.Commerce;
using Content.Meta.Hosting;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Commerce;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Player;
using Robust.Shared.Enums;
using Robust.Shared.Configuration;

namespace Content.Server._NC.Meta.Contracts;

public sealed record NCMetaDevelopmentOutcome(bool Success, string MessageKey, Guid ContractId = default,
    NCMetaDevelopmentPrompt? Prompt = null, NCMetaDevelopmentFixtureChunk? Fixture = null);
public sealed record NCMetaDevelopmentDeliveryRecord(MetaDevelopmentDelivery Delivery, NCMetaRelease Release,
    string Developer, string BuildDeviceId, long Revision);
public sealed record NCMetaDevelopmentReceiptRecord(MetaDevelopmentReceipt Receipt, long Revision);
public sealed record NCMetaDevelopmentPartyIndex(string Party, Guid[] Contracts, long Revision);
public sealed record NCMetaDevelopmentCommand(string Actor, string Fingerprint, NCMetaDevelopmentOutcome? Outcome, long Revision);

/// <summary>Native bilateral software contracts. No guest method can assert acceptance or withdraw escrow.</summary>
public sealed partial class NCMetaDevelopmentContractSystem : EntitySystem
{
    private const string ContractType = "meta.development-contract";
    private const string DeliveryType = "meta.development.delivery";
    private const string ReceiptType = "meta.development.acceptance";
    private const string IndexType = "meta.development.party";
    private const string CommandType = "meta.development.command";
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record NativeContext(EntityUid Terminal, EntityUid ActorEntity, NCMetaManagementProof Management,
        string Owner, string Actor, string DeviceId, ulong DeviceEpoch, ulong AuthorityEpoch, NCSessionEpoch Epoch, NCActiveWorldTime Time,
        ICommonSession? Connection, DateTime ConnectedTime, long? ApprovalDeadline = null);
    private sealed record PendingPrompt(NativeContext Context, NCMetaDevelopmentPrompt Prompt, long ExpiresAt);
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCMetaSnapshotSystem _snapshots = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaAuthoritySystem _authority = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaMarketplaceSystem _marketplace = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;
    [Dependency] private NCPartyDirectorySystem _directory = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    private readonly SemaphoreSlim _ingress = new(32, 32);
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly Dictionary<Guid, PendingPrompt> _prompts = [];
    private readonly Dictionary<EntityUid, MetaRateLimit> _rates = [];
    public NCPartyRef EscrowParty => NCPersistentIdentity.CityService("meta-development-escrow");
    private static NCAccountId EscrowAccount(Guid contract) => NCPersistentIdentity.SystemAccount("meta-development." + contract.ToString("D"));

    public void CloseFor(EntityUid terminal, EntityUid actor)
    {
        _rates.Remove(actor);
        foreach (var id in _prompts.Where(pair => pair.Value.Context.Terminal == terminal && pair.Value.Context.ActorEntity == actor)
                     .Select(pair => pair.Key).ToArray()) _prompts.Remove(id);
    }

    private async Task<NativeContext?> Capture(EntityUid terminal, EntityUid actor, string requestedOwner, bool consumeRate = true)
    {
        var proof = await _authority.CaptureWorkspaceManagementAsync(terminal, actor);
        if (proof == null || requestedOwner.Length != 0 && requestedOwner != proof.Owner.ToString()) return null;
        return await Main(() =>
        {
            if (!_marketplace.CanAccessService(terminal, actor) || !_authority.IsCurrent(proof) ||
                !_devices.TryGetEntry(terminal, out var device) || device.Owner != proof.Owner.ToString() ||
                !_persistence.TryCaptureTime(out var epoch, out var time)) return null;
            if (!_rates.TryGetValue(actor, out var rate))
            {
                if (_rates.Count >= 256) return null;
                _rates.Add(actor, rate = new(12, 4));
            }
            if (consumeRate && !rate.TryConsume(time.Ticks / TimeSpan.TicksPerMillisecond)) return null;
            foreach (var id in _prompts.Where(pair => pair.Value.ExpiresAt <= time.Ticks || pair.Value.Context.Epoch != epoch)
                         .Select(pair => pair.Key).ToArray()) _prompts.Remove(id);
            return new NativeContext(terminal, actor, proof, device.Owner, proof.Actor.ToString(), device.Id,
                device.DeviceEpoch, device.AuthorityEpoch, epoch, time,
                TryComp<ActorComponent>(actor, out var player) ? player.PlayerSession : null,
                player?.PlayerSession.ConnectedTime ?? default);
        });
    }
    private bool Current(NativeContext context) => _authority.IsCurrent(context.Management) && ConnectionCurrent(context) &&
        _marketplace.CanAccessService(context.Terminal, context.ActorEntity) && _devices.TryGetEntry(context.Terminal, out var entry) &&
        entry.Id == context.DeviceId && entry.Owner == context.Owner && entry.DeviceEpoch == context.DeviceEpoch &&
        entry.AuthorityEpoch == context.AuthorityEpoch && _persistence.TryCaptureTime(out var epoch, out var time) && epoch == context.Epoch &&
        (context.ApprovalDeadline == null || time.Ticks < context.ApprovalDeadline);
    private bool ConnectionCurrent(NativeContext context) => context.Connection == null ||
        context.Connection.Status == SessionStatus.InGame && context.Connection.ConnectedTime == context.ConnectedTime &&
        TryComp<ActorComponent>(context.ActorEntity, out var actor) && ReferenceEquals(actor.PlayerSession, context.Connection);
    private Task<NCMetaAuthorityConstraints?> Constraints(NativeContext context) => Main(() => Current(context)
        ? _authority.CaptureWriteConstraints(context.Management, context.Terminal) : null);
    private static bool Participant(MetaDevelopmentContract value, string owner) => value.Terms.Customer == owner ||
        value.Terms.Developer == owner || value.Terms.Arbitrator == owner;
    private static Guid CommandId(string actor, Guid request) => NCPersistentIdentity.Command("meta", "development", actor + ":" + request);
    private static NCMetaDevelopmentOutcome Fail(string reason) => new(false, "nc-meta-development-" + reason);
    private bool OperationalAllowed(string container = "", string content = "") =>
        _configuration.GetCVar(NCMetaCVars.Enabled) &&
        MetaOperationalPolicy.TryParseRevokedArtifacts(_configuration.GetCVar(NCMetaCVars.RevokedArtifacts), out var blocked) &&
        !blocked.Contains(container) && !blocked.Contains(content);

    public async Task<NCMetaDevelopmentOutcome> TryExecuteAsync(EntityUid terminal, EntityUid actor, Guid requestId,
        NCMetaDevelopmentRequest request)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation == null || requestId == Guid.Empty || request == null || !Enum.IsDefined(request.Action) ||
            !await _ingress.WaitAsync(0)) return Fail("busy");
        try
        {
            // Take an immutable copy before asynchronous identity and database work.
            var body = JsonSerializer.Serialize(request, Json);
            if (Utf8.GetByteCount(body) > 32768) return Fail("input");
            request = JsonSerializer.Deserialize<NCMetaDevelopmentRequest>(body, Json)!;
            if (request.ActingParty == null || request.TermsHash == null || request.Text == null || request.ResolutionHash == null) return Fail("input");
            var context = await Capture(terminal, actor, request.ActingParty);
            if (context == null) return Fail("access");
            var key = request.ContractId == Guid.Empty ? CommandId(context.Actor, requestId) : request.ContractId;
            var gate = _gates[(uint) key.GetHashCode() % (uint) _gates.Length];
            if (!await gate.WaitAsync(0)) return Fail("busy");
            try
            {
                var fingerprint = NCMetaPersistenceSystem.Fingerprint(context.Actor, context.Owner, body);
                var replay = await Read<NCMetaDevelopmentCommand>(CommandType, CommandId(context.Actor, requestId).ToString("D"), value => value.Revision);
                if (replay != null && (replay.Actor != context.Actor || replay.Fingerprint != fingerprint)) return Fail("request");
                if (replay?.Outcome is { } completed) return completed;
                if (request.Action is not (NCMetaDevelopmentAction.Read or NCMetaDevelopmentAction.SearchContacts or
                    NCMetaDevelopmentAction.QuoteFunding or NCMetaDevelopmentAction.QuoteAcceptance or NCMetaDevelopmentAction.ReadFixture) &&
                    !await Admit(context.Actor, requestId, fingerprint, context)) return Fail("request");
                if (request.Action == NCMetaDevelopmentAction.Offer) return await Offer(context, requestId, request, fingerprint);
                if (request.Action is NCMetaDevelopmentAction.Read or NCMetaDevelopmentAction.SearchContacts) return new(true, "nc-meta-development-refreshed", request.ContractId);
                if (request.Action == NCMetaDevelopmentAction.ImportSource) return await ImportSource(context, requestId, request);
                var current = await ReadContractAsync(request.ContractId);
                if (current == null || !Participant(current, context.Owner)) return Fail("access");
                if (current.Revision != request.ExpectedRevision || current.TermsHash != request.TermsHash) return Fail("revision");
                if (request.Action == NCMetaDevelopmentAction.ReadFixture) return await ReadFixture(context, current, request);
                if (request.Action == NCMetaDevelopmentAction.Deliver) return await Deliver(context, requestId, request, current, fingerprint);
                if (request.Action == NCMetaDevelopmentAction.Test) return await Test(context, requestId, current, fingerprint);
                if (request.Action is NCMetaDevelopmentAction.QuoteFunding or NCMetaDevelopmentAction.QuoteAcceptance)
                    return await QuotePrompt(context, current, request.Action == NCMetaDevelopmentAction.QuoteFunding);
                if (request.Action is NCMetaDevelopmentAction.ConfirmFunding or NCMetaDevelopmentAction.ConfirmAcceptance)
                    return await Confirm(context, requestId, request, current, fingerprint);
                if (request.Action == NCMetaDevelopmentAction.RefundUndelivered)
                    return current.Terms.Customer == context.Owner && MetaDevelopmentPolicy.CanRefundUndelivered(current, context.Time.Ticks)
                        ? await Pay(context, requestId, fingerprint, current, current.Terms.Price, 0, false, false) : Fail("action");
                return await Change(context, requestId, request, current, fingerprint);
            }
            finally { gate.Release(); }
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or OverflowException)
        { Log.Warning($"META development request rejected: {exception.GetType().Name}"); return Fail("input"); }
        finally { _ingress.Release(); }
    }

    public async Task<MetaDevelopmentContract?> ReadContractAsync(Guid id)
    {
        var value = await Read<MetaDevelopmentContract>(ContractType, id.ToString("D"), contract => contract.Revision);
        if (value != null && (value.Id != id || value.Terms == null || value.TermsHash != MetaDevelopmentPolicy.Hash(value.Terms) ||
            value.Consents.Length > 2 || !Enum.IsDefined(value.Phase))) throw new InvalidDataException("Invalid META development contract.");
        return value;
    }
    private async Task<T?> Read<T>(string type, string id, Func<T, long> version) where T : class
    {
        var row = await _snapshots.ReadCurrentAsync(type, id);
        if (row == null) return null;
        if (row.FormatVersion != 1 || Utf8.GetByteCount(row.Payload) > 512 * 1024) throw new InvalidDataException("META development record limit.");
        var value = JsonSerializer.Deserialize<T>(row.Payload, Json);
        if (value == null || version(value) != row.AggregateVersion) throw new InvalidDataException("META development record version mismatch.");
        return value;
    }
    private static NCPersistentAggregateWrite Write<T>(string type, string id, long previous, T value) =>
        new(Guid.NewGuid(), "meta.development.changed", type, id, previous, 1, JsonSerializer.Serialize(value, Json), "{}");
    private async Task<NCMetaDevelopmentOutcome> Commit(NativeContext context, Guid requestId, string fingerprint,
        MetaDevelopmentContract next, IReadOnlyList<NCPersistentAggregateWrite> writes, string key = "changed")
    {
        var proof = await Constraints(context);
        if (proof == null) return Fail("access");
        var outcome = new NCMetaDevelopmentOutcome(true, "nc-meta-development-" + key, next.Id);
        var all = writes.Append(Write(CommandType, CommandId(context.Actor, requestId).ToString("D"), 1,
            new NCMetaDevelopmentCommand(context.Actor, fingerprint, outcome, 2))).ToArray();
        if (all.Length > 24 || !await Main(() => Current(context))) return Fail("quota");
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new(CommandId(context.Actor, requestId), "meta.development.change",
            "meta.owner", context.Owner, all, JsonSerializer.Serialize(outcome, Json), requestId, null,
            context.Epoch, context.Time, [], RequestFingerprint: fingerprint, Authority: proof));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted ? outcome : Fail("revision");
    }
    private Task<T> Main<T>(Func<T> callback)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() => { try { completion.SetResult(callback()); } catch (Exception exception) { completion.SetException(exception); } });
        return completion.Task;
    }

    // Immutable idempotency admission binds even a rejected financial attempt to its payload.
    // It does not consume a test receipt, release source rights, move money or report success.
    private async Task<bool> Admit(string actor, Guid request, string fingerprint, NativeContext? context,
        MetaDevelopmentContract? automaticContract = null, NCSessionEpoch? automaticEpoch = null)
    {
        var id = CommandId(actor, request);
        var previous = await Read<NCMetaDevelopmentCommand>(CommandType, id.ToString("D"), value => value.Revision);
        if (previous != null) return previous.Actor == actor && previous.Fingerprint == fingerprint;
        var proof = context != null ? await Constraints(context) : await Main(() => automaticContract == null ? null : AutomaticConstraints(automaticContract, automaticEpoch));
        if (proof == null) return false;
        var clock = await Main(() => _persistence.TryCaptureTime(out var epoch, out var time) &&
            (context != null ? Current(context) : epoch == automaticEpoch)
            ? ((NCSessionEpoch Epoch, NCActiveWorldTime Time)?)(epoch, time) : null);
        if (clock == null) return false;
        var result = await _database.ApplyNCPersistentBatchMutationAsync(new(NCMetaPersistenceSystem.StableGuid("development-admit:" + id),
            "meta.development.admit", "meta.actor", actor, [Write(CommandType, id.ToString("D"), 0,
                new NCMetaDevelopmentCommand(actor, fingerprint, null, 1))], "{}", request, null, clock.Value.Epoch, clock.Value.Time,
            [], RequestFingerprint: fingerprint, Authority: proof));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
    }
}
