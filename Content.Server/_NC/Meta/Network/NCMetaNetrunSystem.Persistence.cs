// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Content.Server.Database;
using Content.Server._NC.Meta.Devices;
using Content.Shared.Verbs;
using Content.Shared._NC.Meta.Network;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetrunSystem
{
    private sealed record NodePolicy(string Id, string DeviceId, NCMetaNetNodeKind Kind, string Defense,
        string[] Neighbors, string DataPath, string ControlledDeviceId);
    private sealed record SecurityPolicy(string AccessDeviceId, string Owner, string Architecture, bool PublicEntry,
        string[] Entries, NodePolicy[] Nodes, long Revision);
    private sealed class PolicyState
    {
        public required string Owner;
        public SecurityPolicy? Policy;
        public bool Loading;
        public bool Writing;
        public long RetryAfter;
    }
    private sealed record AuditEntry(Guid ActionId, Guid SessionId, string Principal, string DeckId,
        string Architecture, string Node, string Operation, string Outcome, int Trace, long ActiveTicks, bool ClockCurrent = true);
    private sealed record AuditJournal(string AccessDeviceId, AuditEntry[] Entries, long Revision);
    private sealed record OutcomeReservation(IDisposable Lease, NCSessionEpoch Epoch, NCActiveWorldTime Time,
        AuditEntry Entry, SemaphoreSlim Gate);
    [Dependency] private IServerDbManager _database = default!;
    private static readonly JsonSerializerOptions NetJson = new() { MaxDepth = 12 };
    private readonly Dictionary<string, PolicyState> _policies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _journalLocks = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, OutcomeReservation> _outcomes = [];
    private int _policyOperations;
    private int _journalOperations;
    private int _auditReservations;
    private bool _nativeAccepting = true;
    private NCSessionEpoch? _policyEpoch;
    private Guid? _policyCheckpoint;
    private Guid _policyGeneration = Guid.NewGuid();

    /// <summary>Call before persistence closes ingress, so pending native actions append their final audit records.</summary>
    public void QuiesceSessions()
    {
        _nativeAccepting = false;
        foreach (var session in _sessions.Values.ToArray()) Disconnect(session, "nc-meta-net-disconnected");
        _connecting.Clear(); _connectionTimeouts.Clear();
    }
    public void ResumeSessions()
    {
        _nativeAccepting = true;
        if (_persistence.TryCaptureTime(out var epoch, out _)) EnsurePolicyBoundary(epoch);
    }

    // An ordinary same-epoch maintenance resume preserves the cache. A newly selected world must authenticate it again.
    private void EnsurePolicyBoundary(NCSessionEpoch epoch)
    {
        var checkpoint = _netSnapshots.SelectedSnapshotId;
        if (_policyEpoch == epoch && _policyCheckpoint == checkpoint) return;
        _policyEpoch = epoch; _policyCheckpoint = checkpoint; _policyGeneration = Guid.NewGuid();
        _policies.Clear(); _connecting.Clear(); _connectionTimeouts.Clear();
    }

    private bool IsPolicyStateCurrent(string id, PolicyState state, Guid generation, NCSessionEpoch epoch) =>
        _nativeAccepting && _policyEpoch == epoch && _policyGeneration == generation &&
        _policies.TryGetValue(id, out var current) && ReferenceEquals(current, state);

    private bool TryGetPolicy(NCMetaDeviceEntry access, NCMetaNetAccessPointComponent point, out SecurityPolicy policy)
    {
        policy = default!;
        if (!_nativeAccepting || access.Owner.Length == 0) return false;
        if (_policies.TryGetValue(access.Id, out var existing))
        {
            if (existing.Owner != access.Owner || existing.Loading || existing.Writing || existing.RetryAfter > _now) return false;
            if (existing.Policy is { } ready)
            {
                if (ready.Architecture != point.ArchitectureId || ready.PublicEntry != point.PublicEntry || !ready.Entries.SequenceEqual(point.EntryNodes)) return false;
                policy = ready; return true;
            }
            _policies.Remove(access.Id);
        }
        if (_policies.Count >= 128 || _policyOperations >= 8 || !_persistence.TryCaptureTime(out var epoch, out var time)) return false;
        var snapshot = CapturePolicy(access, point);
        if (snapshot == null) return false;
        var lease = _persistence.TryBeginNativeMutation();
        if (lease == null) return false;
        var state = new PolicyState { Owner = access.Owner, Loading = true };
        _policies.Add(access.Id, state); _policyOperations++;
        _ = LoadPolicyAsync(snapshot, access.Entity, access.DeviceEpoch, access.AuthorityEpoch, state,
            _policyGeneration, epoch, time, lease);
        return false;
    }
    private SecurityPolicy? CapturePolicy(NCMetaDeviceEntry access, NCMetaNetAccessPointComponent point)
    {
        var nodes = _nodes.Where(pair => pair.Key.Architecture == point.ArchitectureId).Take(65).ToArray();
        if (nodes.Length > 64 || point.EntryNodes.Count > 64 || nodes.Any(pair => !_devices.TryGetEntry(pair.Value.Entity, out _))) return null;
        var snapshot = new SecurityPolicy(access.Id, access.Owner, point.ArchitectureId, point.PublicEntry,
            point.EntryNodes.ToArray(), nodes.Select(pair =>
            {
                _devices.TryGetEntry(pair.Value.Entity, out var device);
                var node = pair.Value.Component;
                return new NodePolicy(node.NodeId, device!.Id, node.Kind, node.Defense.Id, node.Neighbors.ToArray(), node.DataPath, node.ControlledDeviceId);
            }).ToArray(), 1);
        return ValidPolicy(snapshot) ? snapshot : null;
    }
    private void OnAccessPointVerbs(Entity<NCMetaNetAccessPointComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !_devices.CanManage(ent.Owner, args.User)) return;
        var actor = args.User;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-net-save-policy"), Act = () => TrySynchronizeSecurityPolicy(ent.Owner, actor) });
    }
    /// <summary>Explicit owner action to adopt the currently mapped native graph after configuration or ownership changes.</summary>
    public bool TrySynchronizeSecurityPolicy(EntityUid accessPoint, EntityUid actor)
    {
        if (!_nativeAccepting || !_devices.CanManage(accessPoint, actor) || !_devices.TryGetEntry(accessPoint, out var access) || !TryComp<NCMetaNetAccessPointComponent>(accessPoint, out var point) || _policyOperations >= 8 || !_persistence.TryCaptureTime(out var epoch, out var time)) return false;
        EnsurePolicyBoundary(epoch);
        if (_policies.TryGetValue(access.Id, out var previous) && (previous.Loading || previous.Writing) || previous == null && _policies.Count >= 128) return false;
        var policy = CapturePolicy(access, point); if (policy == null) return false;
        var nodes = policy.Nodes.Select(binding => _devices.TryResolve(binding.DeviceId, out var node)
            ? (EntityUid?) node.Entity : null).ToArray();
        if (nodes.Any(node => node == null)) return false;
        var configuration = CaptureConfiguration(actor, accessPoint, access.Id,
            nodes.Select(node => node!.Value), requireTargetRoutes: false);
        if (configuration == null) return false;
        var lease = _persistence.TryBeginNativeMutation(); if (lease == null) return false;
        var state = previous ?? new PolicyState { Owner = access.Owner };
        state.Owner = access.Owner; state.Writing = true; _policies[access.Id] = state; _policyOperations++;
        _ = SynchronizePolicyAsync(policy, accessPoint, access.DeviceEpoch, access.AuthorityEpoch, state,
            configuration, _policyGeneration, epoch, time, lease);
        return true;
    }
    private async Task SynchronizePolicyAsync(SecurityPolicy policy, EntityUid entity, ulong deviceEpoch, ulong authorityEpoch,
        PolicyState state, ConfigurationSeed configuration, Guid generation, NCSessionEpoch epoch, NCActiveWorldTime time, IDisposable lease)
    {
        SecurityPolicy? updated = null;
        ConfigurationAuthorization? authorization = null;
        using (lease)
        {
            try
            {
                authorization = await AuthorizeConfigurationAsync(configuration);
                if (authorization != null)
                {
                    var previous = await _netSnapshots.ReadCurrentAsync("meta.net.policy", policy.AccessDeviceId);
                    var expected = previous?.AggregateVersion ?? 0;
                    var next = policy with { Revision = checked(expected + 1) };
                    if (await ConfigurationMain(() => IsPolicyStateCurrent(policy.AccessDeviceId, state, generation, epoch) &&
                            IsConfigurationCurrent(configuration, authorization.Management)) &&
                        await WritePolicyAsync(Guid.NewGuid(), next, expected, epoch, time, authorization.Constraints)) updated = next;
                }
            }
            catch (Exception exception) { Log.Error($"NET policy synchronization failed: {exception.GetType().Name}."); }
        }
        _completed.Enqueue(() =>
        {
            _policyOperations--;
            if (!IsPolicyStateCurrent(policy.AccessDeviceId, state, generation, epoch)) return;
            state.Writing = false;
            if (authorization == null || !IsConfigurationCurrent(configuration, authorization.Management))
            { state.Policy = null; return; }
            if (!_devices.TryGetEntry(entity, out var device) || device.Id != policy.AccessDeviceId || device.Owner != policy.Owner || device.DeviceEpoch != deviceEpoch || device.AuthorityEpoch != authorityEpoch) { state.Policy = null; return; }
            state.Policy = updated;
            if (updated != null) _devices.TryInvalidate(entity);
        });
    }
    private async Task LoadPolicyAsync(SecurityPolicy initial, EntityUid entity, ulong deviceEpoch, ulong authorityEpoch,
        PolicyState state, Guid generation, NCSessionEpoch epoch, NCActiveWorldTime time, IDisposable lease)
    {
        SecurityPolicy? result = null;
        using (lease)
        {
            try
            {
                var stored = await _netSnapshots.ReadCurrentAsync("meta.net.policy", initial.AccessDeviceId);
                if (stored == null)
                {
                    var nativeAuthority = new NCMetaAuthorityConstraints(null,
                        [new(initial.AccessDeviceId, initial.Owner, authorityEpoch)]);
                    if (await ConfigurationMain(() => IsPolicyStateCurrent(initial.AccessDeviceId, state, generation, epoch) &&
                            _persistence.TryCaptureTime(out var currentEpoch, out _) && currentEpoch == epoch &&
                            _devices.TryGetEntry(entity, out var current) && current.Id == initial.AccessDeviceId &&
                            current.Owner == initial.Owner && current.DeviceEpoch == deviceEpoch && current.AuthorityEpoch == authorityEpoch) &&
                        await WritePolicyAsync(Guid.NewGuid(), initial, 0, epoch, time, nativeAuthority)) result = initial;
                    else stored = await _netSnapshots.ReadCurrentAsync("meta.net.policy", initial.AccessDeviceId);
                }
                if (stored is { Payload.Length: <= 131072 })
                {
                    var candidate = JsonSerializer.Deserialize<SecurityPolicy>(stored.Payload, NetJson);
                    if (candidate != null && ValidPolicy(candidate) && candidate.AccessDeviceId == initial.AccessDeviceId && candidate.Owner == initial.Owner && candidate.Revision == stored.AggregateVersion) result = candidate;
                }
            }
            catch (Exception exception) { Log.Error($"NET policy read failed for {initial.AccessDeviceId}: {exception.GetType().Name}."); }
        }
        var captured = result;
        _completed.Enqueue(() =>
        {
            _policyOperations--;
            if (!IsPolicyStateCurrent(initial.AccessDeviceId, state, generation, epoch)) return;
            state.Loading = false;
            state.RetryAfter = captured == null ? _now + 5000 : 0;
            if (!_devices.TryGetEntry(entity, out var device) || device.Id != initial.AccessDeviceId || device.Owner != initial.Owner || device.DeviceEpoch != deviceEpoch || device.AuthorityEpoch != authorityEpoch) { CompleteConnections(initial.AccessDeviceId, false); return; }
            state.Policy = captured;
            if (captured == null) { CompleteConnections(initial.AccessDeviceId, false); return; }
            foreach (var binding in captured.Nodes)
                if (_nodes.TryGetValue((captured.Architecture, binding.Id), out var node) && _devices.TryGetEntry(node.Entity, out var native) && native.Id == binding.DeviceId && MatchesPolicy(binding, node, compareControl: false))
                {
                    var key = (captured.Architecture, binding.Id);
                    RemoveControlledNode(node.Component.ControlledDeviceId, key); node.Component.ControlledDeviceId = binding.ControlledDeviceId; AddControlledNode(binding.ControlledDeviceId, key);
                    node.Component.DataPath = binding.DataPath;
                }
            CompleteConnections(initial.AccessDeviceId, true);
        });
    }
    private static bool ValidPolicy(SecurityPolicy policy) => policy is { Revision: > 0, Owner.Length: > 0 and <= 128, Architecture.Length: > 0 and <= 128, Entries.Length: <= 64, Nodes.Length: <= 64 } &&
        Guid.TryParseExact(policy.AccessDeviceId, "D", out _) && policy.Entries.All(id => id is { Length: > 0 and <= 128 }) &&
        policy.Nodes.All(node => node is { Id.Length: > 0 and <= 128, Defense.Length: > 0 and <= 128, Neighbors.Length: <= 64, DataPath.Length: <= 512, ControlledDeviceId.Length: <= 128 } && Guid.TryParseExact(node.DeviceId, "D", out _) && Enum.IsDefined(node.Kind) && node.Neighbors.All(id => id is { Length: > 0 and <= 128 })) &&
        policy.Nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() == policy.Nodes.Length;
    private static bool MatchesPolicy(NodePolicy policy, NodeEntry node, bool compareControl = true) => policy.Kind == node.Component.Kind &&
        policy.Defense == node.Component.Defense.Id && policy.Neighbors.SequenceEqual(node.Component.Neighbors) &&
        (!compareControl || policy.ControlledDeviceId == node.Component.ControlledDeviceId && policy.DataPath == node.Component.DataPath);
    private bool PolicyAllows(Session session, NodeEntry node)
    {
        var policy = session.Security.Nodes.FirstOrDefault(policy => policy.Id == node.Component.NodeId);
        return policy != null && MatchesPolicy(policy, node) && _devices.TryGetEntry(node.Entity, out var device) &&
            device.Ready && device.Id == policy.DeviceId && device.Owner == session.Security.Owner;
    }
    private async Task<bool> WritePolicyAsync(Guid request, SecurityPolicy policy, long expected, NCSessionEpoch epoch,
        NCActiveWorldTime time, NCMetaAuthorityConstraints authority)
    {
        var payload = JsonSerializer.Serialize(policy, NetJson);
        var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(request,
            "meta.net.policy.update", "meta.owner", policy.Owner, Guid.NewGuid(), "meta.net.policy.updated", "meta.net.policy", policy.AccessDeviceId,
            expected, 1, payload, "{}", payload, request, null, epoch, time, [], RequestFingerprint: NetFingerprint(payload), Authority: authority));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
    }
    private bool TrySaveControlBinding(NodeEntry node, NCMetaDeviceEntry target, EntityUid actor, string? dataPath = null)
    {
        if (!_persistence.TryCaptureTime(out var epoch, out var time)) return false;
        EnsurePolicyBoundary(epoch);
        var pair = _policies.FirstOrDefault(pair => pair.Value.Policy is { } policy && policy.Nodes.Any(binding => binding.Id == node.Component.NodeId && binding.DeviceId == (_devices.TryGetEntry(node.Entity, out var device) ? device.Id : string.Empty)) && policy.Architecture == node.Component.ArchitectureId);
        if (pair.Value?.Policy is not { } old || old.Owner != target.Owner || pair.Value.Writing || _policyOperations >= 8) return false;
        var configuration = CaptureConfiguration(actor, node.Entity, old.AccessDeviceId,
            [node.Entity, target.Entity], requireTargetRoutes: true);
        if (configuration == null) return false;
        var lease = _persistence.TryBeginNativeMutation(); if (lease == null) return false;
        var updated = old with { Revision = checked(old.Revision + 1), Nodes = old.Nodes.Select(binding => binding.Id == node.Component.NodeId ? binding with { ControlledDeviceId = target.Id, DataPath = dataPath ?? binding.DataPath } : binding).ToArray() };
        pair.Value.Writing = true; _policyOperations++;
        _ = SaveControlBindingAsync(pair.Value, old, updated, node.Entity, target.Entity,
            target.DeviceEpoch, target.AuthorityEpoch, configuration, _policyGeneration, epoch, time, lease);
        return true;
    }
    private async Task SaveControlBindingAsync(PolicyState state, SecurityPolicy old, SecurityPolicy updated, EntityUid nodeEntity,
        EntityUid targetEntity, ulong targetEpoch, ulong targetAuthority, ConfigurationSeed configuration,
        Guid generation, NCSessionEpoch epoch, NCActiveWorldTime time, IDisposable lease)
    {
        var success = false;
        ConfigurationAuthorization? authorization = null;
        using (lease)
        {
            try
            {
                authorization = await AuthorizeConfigurationAsync(configuration);
                success = authorization != null &&
                    await ConfigurationMain(() => IsPolicyStateCurrent(old.AccessDeviceId, state, generation, epoch) &&
                        IsConfigurationCurrent(configuration, authorization.Management)) &&
                    await WritePolicyAsync(Guid.NewGuid(), updated, old.Revision, epoch, time, authorization.Constraints);
            }
            catch (Exception) { success = false; }
        }
        _completed.Enqueue(() =>
        {
            _policyOperations--;
            if (!IsPolicyStateCurrent(old.AccessDeviceId, state, generation, epoch)) return;
            state.Writing = false;
            if (!success || authorization == null || !IsConfigurationCurrent(configuration, authorization.Management))
            { state.Policy = null; return; }
            state.Policy = updated;
            if (!_devices.TryGetEntry(targetEntity, out var target) || target.DeviceEpoch != targetEpoch || target.AuthorityEpoch != targetAuthority || !_devices.TryResolve(old.AccessDeviceId, out var access) || target.Owner != old.Owner || access.Owner != old.Owner || !TryComp<NCMetaNetNodeComponent>(nodeEntity, out var node)) return;
            var key = (node.ArchitectureId, node.NodeId);
            RemoveControlledNode(node.ControlledDeviceId, key); node.ControlledDeviceId = target.Id; AddControlledNode(target.Id, key);
            node.DataPath = updated.Nodes.Single(binding => binding.Id == node.NodeId).DataPath;
            _devices.TryInvalidate(nodeEntity); _devices.TryInvalidate(access.Entity);
        });
    }
    private bool TryBeginAudit(Session session, Pending pending, long duration)
    {
        if (!_nativeAccepting || _auditReservations >= 16 || _journalOperations >= 16 || !_persistence.TryCaptureTime(out var epoch, out var time) || !_devices.TryGetEntry(session.Deck, out var deck)) return false;
        var lease = _persistence.TryBeginNativeMutation(); if (lease == null) return false;
        var outcomeLease = _persistence.TryBeginNativeMutation(); if (outcomeLease == null) { lease.Dispose(); return false; }
        if (!_journalLocks.TryGetValue(session.Security.AccessDeviceId, out var gate))
        {
            if (_journalLocks.Count >= 128) { lease.Dispose(); outcomeLease.Dispose(); return false; }
            _journalLocks.Add(session.Security.AccessDeviceId, gate = new SemaphoreSlim(1, 1));
        }
        _auditReservations++; _journalOperations++;
        var entry = new AuditEntry(pending.Id, session.Id, session.Owner, deck.Id, session.Architecture, pending.Node, pending.Operation.ToString(), "Admitted", session.Trace, time.Ticks);
        _outcomes.Add(pending.Id, new OutcomeReservation(outcomeLease, epoch, time, entry, gate));
        _ = AdmitActionAsync(session.Id, pending.Id, session.Security.AccessDeviceId, entry, duration, gate, epoch, time, lease);
        return true;
    }
    private async Task AdmitActionAsync(Guid sessionId, Guid actionId, string accessId, AuditEntry entry, long duration,
        SemaphoreSlim gate, NCSessionEpoch epoch, NCActiveWorldTime time, IDisposable lease)
    {
        bool success;
        using (lease) { success = await AppendAuditAsync(accessId, entry, gate, epoch, time); }
        _completed.Enqueue(() =>
        {
            _journalOperations--;
            if (!_sessions.TryGetValue(sessionId, out var session) || session.Pending is not { } pending || pending.Id != actionId) return;
            if (pending.IoState != 3 || pending.Due <= _now) { CompleteError(session, pending, "Timeout"); return; }
            if (!success || !Current(session)) { CompleteError(session, pending, "ActionRejected"); return; }
            session.Pending = pending with { IoState = 0, Due = _now + duration };
            _actions.Enqueue((sessionId, actionId), _now + duration); session.Revision++; Publish(session.Deck, session.Actor);
        });
    }
    private void RecordOutcome(Session session, Pending pending, string outcome)
    {
        if (!pending.AuditReserved) return;
        _auditReservations--;
        if (!_outcomes.Remove(pending.Id, out var reserved)) return;
        var clockCurrent = _persistence.TryCaptureTime(out var epoch, out var time);
        if (!clockCurrent) { epoch = reserved.Epoch; time = reserved.Time; }
        var entry = reserved.Entry with { Outcome = outcome, Trace = session.Trace, ActiveTicks = time.Ticks, ClockCurrent = clockCurrent };
        _journalOperations++;
        _ = WriteOutcomeAsync(session.Security.AccessDeviceId, entry, reserved.Gate, epoch, time, reserved.Lease);
    }
    private async Task WriteOutcomeAsync(string accessId, AuditEntry entry, SemaphoreSlim gate, NCSessionEpoch epoch, NCActiveWorldTime time, IDisposable lease)
    {
        using (lease) if (!await AppendAuditAsync(accessId, entry, gate, epoch, time)) Log.Error($"NET outcome remains unconfirmed for {entry.ActionId} ({entry.Outcome}).");
        _completed.Enqueue(() => _journalOperations--);
    }
    private async Task<bool> AppendAuditAsync(string accessId, AuditEntry entry, SemaphoreSlim gate, NCSessionEpoch epoch, NCActiveWorldTime time)
    {
        await gate.WaitAsync();
        try
        {
            var previous = await _netSnapshots.ReadCurrentAsync("meta.net.journal", accessId);
            AuditJournal? journal = null;
            if (previous != null)
            {
                if (previous.Payload.Length > 131072) return false;
                journal = JsonSerializer.Deserialize<AuditJournal>(previous.Payload, NetJson);
                if (journal == null || journal.AccessDeviceId != accessId || journal.Revision != previous.AggregateVersion || journal.Entries.Length > 64) return false;
            }
            var next = new AuditJournal(accessId, (journal?.Entries ?? []).TakeLast(63).Append(entry).ToArray(), checked((journal?.Revision ?? 0) + 1));
            var payload = JsonSerializer.Serialize(next, NetJson); var request = Guid.NewGuid();
            var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(request, "meta.net.audit.append", "meta.owner", entry.Principal,
                Guid.NewGuid(), "meta.net.action.recorded", "meta.net.journal", accessId, journal?.Revision ?? 0, 1, payload, "{}", "{}", request, null, epoch, time, [], RequestFingerprint: NetFingerprint(payload)));
            return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
        }
        catch (Exception exception) { Log.Error($"NET journal write failed: {exception.GetType().Name}."); return false; }
        finally { gate.Release(); }
    }
    private static string NetFingerprint(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
