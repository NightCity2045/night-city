// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.Medical.Persistence;
using Robust.Shared.Map.Events;

namespace Content.Server._NC.Medical.Cyberware;

/// <summary>Only the generic world/possession YAML path uses this clock. No calendar-time progression.</summary>
[RegisterComponent]
public sealed partial class NCCyberwareWorldSnapshotComponent : Component
{
    [ViewVariables] public bool Rejected;
    [ViewVariables] public bool Restored;
    [DataField] public bool Saved;
    [DataField] public TimeSpan CapturedAt;
}

public sealed partial class NCCyberwareWorldRestoreSystem : EntitySystem
{
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCyberwareWorldSnapshotComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCCyberwareWorldSnapshotComponent, MapInitEvent>(OnRestored,
            after: [typeof(NCCyberwareSystem)],
            before: [typeof(Abilities.NCCyberwareAbilitySystem)]);
    }

    private void OnStartup(Entity<NCCyberwareWorldSnapshotComponent> ent, ref ComponentStartup args) =>
        _cyberware.TryRestoreWorldEntity(ent.AsNullable());

    private void OnRestored(Entity<NCCyberwareWorldSnapshotComponent> ent, ref MapInitEvent args) =>
        _cyberware.TryRestoreWorldEntity(ent.AsNullable());
}

public sealed partial class NCCyberwareSystem
{
    // Event index, also includes loose replaceable parts and cells. Only persistence/admin boundaries enumerate it.
    private readonly HashSet<EntityUid> _physicalIdentities = [];
    private readonly HashSet<EntityUid> _rejectedWorldTrees = [];

    private void InitializeWorldPersistence()
    {
        SubscribeLocalEvent<NCCyberwareComponent, ComponentInit>(OnIdentityInit);
        SubscribeLocalEvent<NCCyberwarePartComponent, ComponentInit>(OnPartIdentityInit);
        SubscribeLocalEvent<NCCyberwareCellComponent, ComponentInit>(OnCellIdentityInit);
        SubscribeLocalEvent<NCCyberwarePartComponent, ComponentShutdown>(OnPartIdentityShutdown);
        SubscribeLocalEvent<NCCyberwareCellComponent, ComponentShutdown>(OnCellIdentityShutdown);
        SubscribeLocalEvent<BeforeSerializationEvent>(OnWorldSerialization);
        SubscribeLocalEvent<Content.Shared.GameTicking.RoundRestartCleanupEvent>(OnWorldRestart);
    }

    private void OnWorldRestart(Content.Shared.GameTicking.RoundRestartCleanupEvent args) => _rejectedWorldTrees.Clear();

    private void MarkRejectedWorldTree(EntityUid uid)
    {
        // Mark the pending load as invalid before its caller replaces any existing possessions.
        // Never delete a parent or container here. Only the conflicting new node is queued below.
        for (var depth = 0; depth < 256 && Exists(uid); depth++)
        {
            if (HasComp<Robust.Shared.Map.Components.MapComponent>(uid) ||
                HasComp<Robust.Shared.Map.Components.MapGridComponent>(uid)) break;
            _rejectedWorldTrees.Add(uid);
            uid = Transform(uid).ParentUid;
        }
    }

    private void OnIdentityInit(Entity<NCCyberwareComponent> ent, ref ComponentInit args) => _physicalIdentities.Add(ent);
    private void OnPartIdentityInit(Entity<NCCyberwarePartComponent> ent, ref ComponentInit args) => _physicalIdentities.Add(ent);
    private void OnCellIdentityInit(Entity<NCCyberwareCellComponent> ent, ref ComponentInit args) => _physicalIdentities.Add(ent);
    private void OnPartIdentityShutdown(Entity<NCCyberwarePartComponent> ent, ref ComponentShutdown args) => _physicalIdentities.Remove(ent);
    private void OnCellIdentityShutdown(Entity<NCCyberwareCellComponent> ent, ref ComponentShutdown args) => _physicalIdentities.Remove(ent);

    private Guid PhysicalIdentity(EntityUid uid)
    {
        if (TryComp<NCCyberwareComponent>(uid, out var module)) return module.StableInstanceId;
        if (TryComp<NCCyberwarePartComponent>(uid, out var part)) return part.StableInstanceId;
        return TryComp<NCCyberwareCellComponent>(uid, out var cell) ? cell.StableInstanceId : Guid.Empty;
    }

    private void OnWorldSerialization(BeforeSerializationEvent args)
    {
        // SerializeEntitiesRecursive includes descendants; do not touch unrelated items on the same map.
        var pending = new Stack<EntityUid>(args.Entities);
        var seen = new HashSet<EntityUid>();
        while (pending.TryPop(out var uid))
        {
            if (!seen.Add(uid) || TerminatingOrDeleted(uid)) continue;
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child)) pending.Push(child);
            if (!_physicalIdentities.Contains(uid)) continue;
            if (HasComp<NCCyberwareCellComponent>(uid)) EnsureCellIdentity(uid);
            var stamp = EnsureComp<NCCyberwareWorldSnapshotComponent>(uid);
            stamp.Saved = true;
            stamp.CapturedAt = _timing.CurTime;
            if (!TryComp<NCCyberwareComponent>(uid, out var module)) continue;
            if (TryResolveOwningBody(uid, out var body)) TryAdvanceBodyPower(body, _timing.CurTime);
            module.PersistenceCapturedAt = _timing.CurTime;
            if (TryComp<NCCyberwareAbilityComponent>(uid, out var ability))
            {
                ability.CooldownRemainingSeconds = NCCyberwareAbilityMath.Remaining(
                    ability.CooldownRemainingSeconds, ability.CooldownObservedAt, _timing.CurTime);
                ability.CooldownObservedAt = _timing.CurTime;
            }
        }
    }

    public bool TryRestoreWorldEntity(Entity<NCCyberwareWorldSnapshotComponent?> ent)
    {
        if (!Resolve(ent, ref ent.Comp, false)) return false;
        if (ent.Comp.Rejected) return false;
        if (ent.Comp.Restored) return true;
        // EnsureComp on an already initialized live entity raises MapInit before the writer fills the stamp.
        if (!ent.Comp.Saved) return true;
        var identity = PhysicalIdentity(ent);
        if (identity == Guid.Empty || _physicalIdentities.Any(other => other != ent.Owner &&
                !TerminatingOrDeleted(other) && PhysicalIdentity(other) == identity))
        {
            Log.Warning($"Rejected duplicated or missing physical cyberware identity {identity} while restoring {ToPrettyString(ent)}.");
            ent.Comp.Rejected = true;
            MarkRejectedWorldTree(ent);
            QueueDel(ent);
            return false;
        }
        if (!TryComp<NCCyberwareComponent>(ent, out var module))
        {
            ent.Comp.Restored = true;
            return true;
        }
        if (!NCCyberwareServiceHistory.Valid(module.Service) || !_prototypes.HasIndex(module.Service.Firmware) ||
            ent.Comp.CapturedAt < TimeSpan.Zero || module.PersistenceCapturedAt != ent.Comp.CapturedAt)
        {
            Log.Warning($"Rejected invalid cyberware service/clock state for {identity}.");
            ent.Comp.Rejected = true;
            MarkRejectedWorldTree(ent);
            QueueDel(ent);
            return false;
        }
        if (!NCCyberwarePersistenceClock.TryRebaseDeadline(module.EmpDisabledUntil, ent.Comp.CapturedAt,
                _timing.CurTime, out var deadline) ||
            TryComp<NCCyberwareAbilityComponent>(ent, out var clock) && !NCCyberwareAbilityMath.ValidCooldown(clock.CooldownRemainingSeconds))
        {
            Log.Warning($"Rejected invalid cyberware timer for {identity}.");
            ent.Comp.Rejected = true;
            MarkRejectedWorldTree(ent);
            QueueDel(ent);
            return false;
        }
        module.EmpDisabledUntil = deadline;
        ent.Comp.Restored = true;
        module.LastAdvancedAt = _timing.CurTime;
        module.Load = 0f;
        module.ActiveLoadUntil = null;
        module.PowerGranted = false;
        if (TryComp<NCCyberwareAbilityComponent>(ent, out var ability))
        {
            ability.CooldownObservedAt = _timing.CurTime;
            ability.Active = false;
            ability.ActiveUntil = null;
        }
        if (TryComp<NCCyberwareAssemblyComponent>(ent, out var assembly)) RefreshAssembly(assembly);
        if (TryGetProfile(module.Profile, out var profile)) UpdateOperational(ent, module, profile, _timing.CurTime);
        return true;
    }

    public bool CanUseRestoredWorldTree(EntityUid root)
    {
        if (_rejectedWorldTrees.Contains(root)) return false;
        var pending = new Stack<EntityUid>();
        pending.Push(root);
        while (pending.TryPop(out var uid))
        {
            if (TerminatingOrDeleted(uid) || TryComp<NCCyberwareWorldSnapshotComponent>(uid, out var stamp) && stamp.Rejected)
                return false;
            var children = Transform(uid).ChildEnumerator;
            while (children.MoveNext(out var child)) pending.Push(child);
        }
        return true;
    }

    /// <summary>Reject cross-owner collisions before medical graph mutation. Replacement of the target's own graph is allowed.</summary>
    public bool CanRestorePhysicalIdentities(EntityUid body, NCMedicalStateSnapshot snapshot)
    {
        var desired = new HashSet<Guid>();
        var states = snapshot.CyberwareModules.Concat(snapshot.Organs.Values
            .Where(organ => organ.Cyberware != null).Select(organ => organ.Cyberware!));
        foreach (var state in states)
        {
            if (!desired.Add(state.StableInstanceId)) return false;
            if (state.Assembly != null)
                foreach (var part in state.Assembly.Parts)
                    if (!desired.Add(part.StableInstanceId)) return false;
            if (!AddCells(state.PowerBus)) return false;
        }
        if (snapshot.BorgCore != null && !AddCells(snapshot.BorgCore.PowerBus)) return false;

        var owned = new HashSet<EntityUid>();
        foreach (var uid in CollectCyberware(body).Append(body))
        {
            owned.Add(uid);
            if (TryComp<NCCyberwareAssemblyComponent>(uid, out var assembly))
                foreach (var container in assembly.Parts.Values)
                    if (container.ContainedEntity is { } part) owned.Add(part);
            if (GetBusCell(uid, NCCyberwarePowerBusComponent.MainSlot) is { } main) owned.Add(main.Owner);
            if (GetBusCell(uid, NCCyberwarePowerBusComponent.ReserveSlot) is { } reserve) owned.Add(reserve.Owner);
        }
        foreach (var uid in _physicalIdentities)
        {
            if (TerminatingOrDeleted(uid) || !desired.Contains(PhysicalIdentity(uid)) || owned.Contains(uid)) continue;
            Log.Warning($"Rejected medical restore for {ToPrettyString(body)}: hardware {PhysicalIdentity(uid)} already exists at {ToPrettyString(uid)}.");
            return false;
        }
        return true;

        bool AddCells(NCCyberwarePowerBusSnapshot? bus)
        {
            return (bus?.Main == null || desired.Add(bus.Main.StableInstanceId)) &&
                   (bus?.Reserve == null || desired.Add(bus.Reserve.StableInstanceId));
        }
    }
}
