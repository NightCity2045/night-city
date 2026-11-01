// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server.Database;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Asynchronous;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Meta.Commerce;

/// <summary>Joins physical item custody and WithDevice licenses in the existing economy transaction.</summary>
public sealed partial class NCMetaAssetTransferSystem : EntitySystem, INCItemOwnershipParticipant
{
    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;
    [Dependency] private NCMetaMarketplaceSystem _marketplace = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ITaskManager _tasks = default!;

    public new string Name => "meta-device-software";

    public override void Initialize()
    {
        base.Initialize();
        _assets.RegisterItemOwnershipParticipant(this);
    }

    public override void Shutdown()
    {
        _assets.UnregisterItemOwnershipParticipant(this);
        base.Shutdown();
    }

    public async Task<NCItemOwnershipPreparation?> PrepareAsync(NCItemOwnershipContext context)
    {
        if (context.PreviousOwner == context.NewOwner) return new([], () => Task.FromResult(true));
        var drain = _persistence.TryBeginNativeMutation();
        if (drain == null) return null;
        try
        {
            var preparations = new List<NCMetaDeviceTransferPreparation>();
            var writes = new List<NCPersistentAggregateWrite>();
            foreach (var item in context.Parts.Prepend(context.Root))
            {
                var mappedId = await _persistence.ReadEconomicDeviceIdAsync(item.Snapshot.ItemId);
                if (mappedId == null && await _persistence.HasEconomicDeviceMappingAsync(item.Snapshot.ItemId)) return Reject();
                var candidate = await Main(() => FindCandidate(item));
                if (candidate.Invalid) return Reject();
                if (mappedId != null && candidate.Id != mappedId) return Reject();
                if (candidate.Id == null)
                {
                    // An unloaded prototype may use a custom ID. Absence of a derived hash is never evidence of no software.
                    if (mappedId != null || candidate.MetaHardware) return Reject();
                    continue;
                }
                if (candidate.Owner.Length == 0)
                {
                    if (mappedId != null || await _persistence.ReadDeviceAsync(candidate.Id) != null) return Reject();
                    continue; // Never claimed: no private program or license authority exists to transfer.
                }
                if (candidate.Owner != context.PreviousOwner.ToString() || !candidate.Ready) return Reject();
                // Legacy claimed hardware can establish its reverse identity while live. The enclosing root/part CAS
                // and META device CAS attest both sides; unloaded custom identities never use this fallback.
                if (mappedId == null)
                {
                    var mapping = await _persistence.PrepareEconomicDeviceMappingAsync(item.Snapshot.ItemId, candidate.Id);
                    if (!mapping.Valid) return Reject();
                    if (mapping.Write != null) writes.Add(mapping.Write);
                }
                var prepared = await _marketplace.PrepareDeviceTransferAsync(candidate.Id, context.PreviousOwner.ToString(),
                    context.NewOwner.ToString(), candidate.AuthorityEpoch, context.OperationId);
                var installations = prepared == null ? null : await _persistence.PrepareInstallationOwnerTransferAsync(
                    candidate.Id, context.PreviousOwner.ToString(), context.NewOwner.ToString(), prepared.PreserveInstallationIds);
                if (prepared == null || installations == null) return Reject();
                preparations.Add(prepared);
                writes.AddRange(prepared.Writes);
                writes.AddRange(installations);
            }
            var captured = preparations.ToArray();
            return new(writes, () => Main(() =>
            {
                if (!_persistence.IsAcceptingCommands || captured.Any(value => !_marketplace.CanCommitDeviceTransfer(value))) return false;
                foreach (var prepared in captured)
                {
                    if (!_devices.TrySuspendForOwnerReconciliation(prepared.DeviceId, context.OperationId)) return false;
                    if (_devices.TryResolve(prepared.DeviceId, out var entry)) _runtime.StopHost(entry.Entity);
                }
                return true;
            }), drain);
        }
        catch { drain.Dispose(); throw; }

        NCItemOwnershipPreparation? Reject() { drain.Dispose(); return null; }
    }

    public async Task ReconcileAsync(NCItemOwnershipContext context)
    {
        if (context.PreviousOwner == context.NewOwner) return;
        // Fresh economic and META authority must agree. On uncertainty the native entry stays suspended.
        foreach (var previous in context.Parts.Prepend(context.Root))
        {
            var item = await _assets.GetItemAsync(previous.Snapshot.ItemId, true);
            var id = await _persistence.ReadEconomicDeviceIdAsync(previous.Snapshot.ItemId);
            if (id == null)
            {
                if (await _persistence.HasEconomicDeviceMappingAsync(previous.Snapshot.ItemId))
                    throw new InvalidOperationException("META physical identity mapping is invalid.");
                continue;
            }
            var durable = await _persistence.ReadDeviceAsync(id);
            if (item == null || durable == null || durable.Owner != item.Snapshot.Owner.ToString())
                throw new InvalidOperationException("META device owner could not be reconciled with durable physical custody.");
            if (!await Main(() =>
                {
                    var entry = _devices.GetRegistered().FirstOrDefault(value => value.Id == id);
                    if (entry == null) return true; // No live entry points; restore verifies durable ownership.
                    if (entry.Ready && entry.Available && entry.Owner == durable.Owner && entry.AuthorityEpoch == durable.AuthorityEpoch) return true;
                    _runtime.StopHost(entry.Entity);
                    return _devices.GetEconomicItemId(entry.Entity) == item.Snapshot.ItemId &&
                           _devices.TryReconcileDurableOwner(id, context.OperationId, durable);
                }))
                throw new InvalidOperationException("META native device remains quarantined pending durable owner reconciliation.");
        }
    }

    private Candidate FindCandidate(NCItemInstanceRecord item)
    {
        var entries = _devices.GetRegistered().Where(value => _devices.GetEconomicItemId(value.Entity) == item.Snapshot.ItemId).Take(2).ToArray();
        if (entries.Length > 1) return new(null, "", 0, false, true, true);
        if (entries.Length == 1)
        {
            var entry = entries[0];
            return new(entry.Id, entry.Owner, entry.AuthorityEpoch, entry.Ready && entry.Available, true, false);
        }
        var hardware = !_prototypes.TryIndex<NCItemDefinitionPrototype>(item.Snapshot.DefinitionId, out var definition) ||
                       definition.MaterializedPrototype is { } prototype &&
                       (!_prototypes.TryIndex<EntityPrototype>(prototype, out var entity) || entity.Components.ContainsKey("NCMetaHost") ||
                           entity.Components.ContainsKey("NCMetaDevice"));
        return new(null, "", 0, false, hardware, false);
    }

    private Task<T> Main<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() => { try { completion.SetResult(action()); } catch (Exception exception) { completion.SetException(exception); } });
        return completion.Task;
    }

    private sealed record Candidate(string? Id, string Owner, ulong AuthorityEpoch, bool Ready, bool MetaHardware, bool Invalid);
}
