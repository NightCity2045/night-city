// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Commerce;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Commerce;

namespace Content.Server._NC.Meta.Runtime;

public sealed partial class NCMetaRuntimeSystem
{
    private sealed class RunContactState(NCMetaRuntimeInstance instance, NCMetaRunContactRequirement proof)
    {
        public readonly NCMetaRuntimeInstance Instance = instance;
        public readonly NCMetaRunContactRequirement Proof = proof;
        public long LastOnlineTicks = proof.LastOnlineTicks;
        public long NextCheck;
        public long NextRenewal;
        public long ObservedAt = -1;
        public ulong RoutingVersion;
        public bool Online;
        public bool Renewing;
        public LinkedListNode<RunContactState>? Node;
    }
    [Dependency] private NCMetaMarketplaceSystem _runLicensing = default!;
    private readonly Dictionary<Guid, RunContactState> _runContacts = [];
    private readonly SortedSet<(long Ticks, Guid Generation)> _contactChecks = [];
    private readonly LinkedList<RunContactState> _contactScan = [];
    private LinkedListNode<RunContactState>? _contactCursor;
    private int _contactSweepRemaining;
    private int _contactRenewals;
    private ulong _contactRoutingVersion;

    private void InitializeMarketplaceContacts() => _network.RoutingChanged += OnMarketplaceRoutingChanged;
    private void ShutdownMarketplaceContacts() => _network.RoutingChanged -= OnMarketplaceRoutingChanged;
    private void OnMarketplaceRoutingChanged()
    { _contactRoutingVersion = unchecked(_contactRoutingVersion + 1); _contactSweepRemaining = _runContacts.Count; }

    private bool TryTrackMarketplaceContact(NCMetaRuntimeInstance instance)
    {
        if (instance.MarketplaceContact is not { } proof) return true;
        if (_runContacts.Count >= 4096 || proof.Owner != instance.Owner || proof.RunDeviceId != instance.Installation.HostDeviceId ||
            proof.RunAuthorityEpoch != instance.AuthorityEpoch || proof.ReleaseDigest != instance.Installation.Active.Digest ||
            proof.WorldEpoch != instance.WorldEpoch || proof.LicenseId == Guid.Empty || proof.AllocationId == Guid.Empty ||
            proof.Policy is not (MetaOfflinePolicy.OnlineRequired or MetaOfflinePolicy.GracePeriod) ||
            !_persistence.TryCaptureTime(out var epoch, out var now) || epoch != proof.WorldEpoch || proof.LastOnlineTicks > now.Ticks ||
            proof.LastOnlineTicks < 0 || proof.Policy == MetaOfflinePolicy.GracePeriod && proof.GraceTicks <= 0) return false;
        var state = new RunContactState(instance, proof) { NextCheck = checked(now.Ticks + ContactInterval(proof)) };
        if (!_runContacts.TryAdd(instance.Generation, state)) return false;
        if (!HasMarketplaceContact(instance)) { _runContacts.Remove(instance.Generation); return false; }
        state.Node = _contactScan.AddLast(state);
        _contactChecks.Add((state.NextCheck, instance.Generation));
        return true;
    }

    private void UntrackMarketplaceContact(NCMetaRuntimeInstance instance)
    {
        if (!_runContacts.Remove(instance.Generation, out var state)) return;
        _contactChecks.Remove((state.NextCheck, instance.Generation));
        if (state.Node is { } node)
        {
            if (_contactCursor == node) _contactCursor = node.Next;
            _contactScan.Remove(node);
        }
    }

    // Only licensing observations change here. Guest state, permissions, physical effects and scopes do not.
    private bool HasMarketplaceContact(NCMetaRuntimeInstance instance)
    {
        if (instance.MarketplaceContact == null) return true;
        if (!_runContacts.TryGetValue(instance.Generation, out var state) ||
            !_persistence.TryCaptureTime(out var epoch, out var now) || epoch != state.Proof.WorldEpoch) return false;
        if (state.ObservedAt != now.Ticks || state.RoutingVersion != _contactRoutingVersion)
        {
            state.Online = _runLicensing.CanMaintainRunContact(state.Proof);
            state.ObservedAt = now.Ticks; state.RoutingVersion = _contactRoutingVersion;
        }
        if (state.Online)
        {
            state.LastOnlineTicks = now.Ticks;
            if (state.Proof.Policy == MetaOfflinePolicy.GracePeriod && !state.Renewing && _contactRenewals < 8 && now.Ticks >= state.NextRenewal)
            {
                state.Renewing = true; _contactRenewals++;
                state.NextRenewal = checked(now.Ticks + ContactInterval(state.Proof));
                _ = RenewMarketplaceContactAsync(state);
            }
            return true;
        }
        var allowed = NCMetaMarketplaceSystem.HasOfflineRunGrace(state.Proof, state.LastOnlineTicks, now.Ticks);
        if (allowed)
        {
            var deadline = GraceDeadline(state);
            if (deadline < state.NextCheck)
            {
                _contactChecks.Remove((state.NextCheck, instance.Generation));
                state.NextCheck = deadline;
                if (state.Node != null) _contactChecks.Add((deadline, instance.Generation));
            }
        }
        return allowed;
    }

    private static long GraceDeadline(RunContactState state) => state.LastOnlineTicks > long.MaxValue - state.Proof.GraceTicks - 1
        ? long.MaxValue : state.LastOnlineTicks + state.Proof.GraceTicks + 1;

    private static long ContactInterval(NCMetaRunContactRequirement proof) => proof.Policy == MetaOfflinePolicy.GracePeriod
        ? Math.Max(1, Math.Min(TimeSpan.TicksPerSecond * 10, proof.GraceTicks / 4)) : TimeSpan.TicksPerSecond * 10;

    private async Task RenewMarketplaceContactAsync(RunContactState state)
    {
        var renewed = false;
        try { renewed = await _runLicensing.TryRenewRunContactAsync(state.Proof); }
        catch (Exception exception) { Log.Error($"META contact renewal failed: {exception.GetType().Name}."); }
        _tasks.RunOnMainThread(() =>
        {
            _contactRenewals--; state.Renewing = false;
            if (!renewed && _persistence.TryCaptureTime(out _, out var now))
                state.NextRenewal = checked(now.Ticks + TimeSpan.TicksPerSecond);
        });
    }

    /// <summary>Trusted scheduler gate; pure computation is subject to the same fresh native authority as host calls.</summary>
    private bool CanRunAuthorizedSlice(string processId) => _processes.TryGetValue(processId, out var instance) && IsCurrent(instance) &&
        (instance.InvocationAuthorization == null || instance.InvocationAuthorization()) &&
        (!instance.ActorAuthorities.TryGetValue(instance.Process.CurrentEventAuthorityToken, out var authority) || IsDeferredContextCurrent(authority));

    private void UpdateMarketplaceContacts()
    {
        if (!_persistence.TryCaptureTime(out _, out var now)) return;
        // A due-only heartbeat renews sleeping watches. The index contains only policies that require contact.
        for (var work = 0; work < 8 && _contactChecks.Count != 0; work++)
        {
            var due = _contactChecks.Min;
            if (due.Ticks > now.Ticks) break;
            _contactChecks.Remove(due);
            if (!_runContacts.TryGetValue(due.Generation, out var state)) continue;
            if (!HasMarketplaceContact(state.Instance)) { Stop(state.Instance, MetaStopReason.PermissionRevoked); continue; }
            state.NextCheck = checked(now.Ticks + ContactInterval(state.Proof));
            if (!state.Online && state.Proof.Policy == MetaOfflinePolicy.GracePeriod)
                state.NextCheck = Math.Min(state.NextCheck, GraceDeadline(state));
            _contactChecks.Add((state.NextCheck, due.Generation));
        }
        // Routing changes request a bounded rotating sweep, without copying/scanning all idle installations.
        for (var work = 0; work < 8 && _contactSweepRemaining > 0 && _contactScan.Count != 0; work++)
        {
            _contactSweepRemaining--;
            var node = _contactCursor ?? _contactScan.First!;
            _contactCursor = node.Next;
            if (!HasMarketplaceContact(node.Value.Instance)) Stop(node.Value.Instance, MetaStopReason.PermissionRevoked);
        }
    }
}
