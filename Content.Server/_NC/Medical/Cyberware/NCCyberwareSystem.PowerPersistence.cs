// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Persistence;
using Content.Shared.Power.Components;
using Content.Shared.PowerCell.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    private static readonly EntProtoId LegacyCellPrototype = "NCCyberwarePowerCell";
    private static readonly EntProtoId PowerBusPrototype = "NCImplantPowerBusMk1";
    private static readonly ProtoId<NCCyberwarePrototype> PowerBusProfile = "NCPowerBusMk1";

    private NCCyberwarePowerBusSnapshot? CapturePowerBus(EntityUid uid)
    {
        return !HasComp<NCCyberwarePowerBusComponent>(uid) ? null : new NCCyberwarePowerBusSnapshot
        {
            Main = CapturePowerCell(GetBusCell(uid, NCCyberwarePowerBusComponent.MainSlot)),
            Reserve = CapturePowerCell(GetBusCell(uid, NCCyberwarePowerBusComponent.ReserveSlot)),
        };
    }

    private NCCyberwareCellSnapshot? CapturePowerCell(Entity<BatteryComponent>? cell)
    {
        if (cell is not { } source)
            return null;
        EnsureCellIdentity(source);
        return new NCCyberwareCellSnapshot
        {
            Prototype = MetaData(source).EntityPrototype!.ID,
            StableInstanceId = Comp<NCCyberwareCellComponent>(source).StableInstanceId,
            Charge = _battery.GetCharge(source.AsNullable()),
            RechargeDelay = TryComp<BatterySelfRechargerComponent>(source, out var recharger) &&
                            recharger.NextAutoRecharge is { } next
                ? TimeSpan.FromTicks(Math.Max(0L, (next - _timing.CurTime).Ticks)) : null,
        };
    }

    private bool ValidatePowerBus(EntityPrototype prototype, NCCyberwareStateSnapshot state)
    {
        if (!prototype.TryComp<NCCyberwarePowerBusComponent>(out _, _componentFactory))
            return state.PowerBus == null;
        if (state.PowerBus == null)
            return false;
        var identities = new HashSet<Guid> { state.StableInstanceId };
        foreach (var cell in new[] { state.PowerBus.Main, state.PowerBus.Reserve })
        {
            if (cell == null)
                continue;
            if (cell.StableInstanceId == Guid.Empty || !identities.Add(cell.StableInstanceId) ||
                string.IsNullOrWhiteSpace(cell.Prototype.Id) || cell.Prototype.Id.Length > 128 ||
                !_prototypes.TryIndex(cell.Prototype, out var entity) ||
                entity.Abstract ||
                !entity.TryComp<PowerCellComponent>(out _, _componentFactory) ||
                !entity.TryComp<BatteryComponent>(out var battery, _componentFactory) ||
                !Finite(cell.Charge, 0f, battery.MaxCharge) ||
                cell.RechargeDelay is { } delay && (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(3650)))
                return false;
        }
        return true;
    }

    private bool TryRestorePowerBus(EntityUid uid, NCCyberwarePowerBusSnapshot? snapshot)
    {
        if (!TryComp<NCCyberwarePowerBusComponent>(uid, out var bus))
            return snapshot == null;
        if (snapshot == null)
            return false;
        var states = new[]
        {
            (Slot: NCCyberwarePowerBusComponent.MainSlot, State: snapshot.Main),
            (Slot: NCCyberwarePowerBusComponent.ReserveSlot, State: snapshot.Reserve),
        };
        var previous = new Dictionary<string, EntityUid>();
        var spawned = new List<EntityUid>();
        var succeeded = false;
        bus.Restoring = true;
        try
        {
            foreach (var (slotId, _) in states)
            {
                if (!_powerSlots.TryGetSlot(uid, slotId, out var slot) || slot.ContainerSlot == null)
                    return false;
                if (slot.Item is not { } cell)
                    continue;
                previous.Add(slotId, cell);
                if (!_containers.Remove(cell, slot.ContainerSlot, destination: Transform(uid).Coordinates))
                    return false;
            }
            foreach (var (slotId, cellState) in states)
            {
                if (cellState == null)
                    continue;
                var cell = Spawn(cellState.Prototype, Transform(uid).Coordinates);
                spawned.Add(cell);
                EnsureComp<NCCyberwareCellComponent>(cell).StableInstanceId = cellState.StableInstanceId;
                _battery.SetCharge(cell, cellState.Charge);
                if (TryComp<BatterySelfRechargerComponent>(cell, out var recharger))
                {
                    recharger.NextAutoRecharge = cellState.RechargeDelay is { } delay ? _timing.CurTime + delay : null;
                    Dirty(cell, recharger);
                    _battery.RefreshChargeRate(cell);
                }
                if (!_powerSlots.TryGetSlot(uid, slotId, out var slot) || slot.ContainerSlot == null ||
                    !_containers.Insert(cell, slot.ContainerSlot))
                    return false;
            }
            succeeded = true;
            foreach (var cell in previous.Values)
                QueueDel(cell);
            return true;
        }
        finally
        {
            if (!succeeded)
            {
                foreach (var cell in spawned)
                {
                    if (_containers.TryGetContainingContainer((cell, null, null), out var container))
                        _containers.Remove(cell, container);
                    QueueDel(cell);
                }
                foreach (var (slotId, cell) in previous)
                {
                    if (_powerSlots.TryGetSlot(uid, slotId, out var slot) && slot.ContainerSlot != null && slot.Item != cell)
                        _containers.Insert(cell, slot.ContainerSlot);
                }
            }
            bus.Restoring = false;
        }
    }

    /// <summary>Called only by the pre-12 medical migration; current snapshots never create a battery.</summary>
    public bool TryMigrateLegacyPower(NCMedicalStateSnapshot snapshot)
    {
        var modules = snapshot.CyberwareModules.Concat(snapshot.Organs.Values
            .Where(organ => organ.Cyberware != null).Select(organ => organ.Cyberware!)).ToList();
        var energy = 0f;
        var needsBus = false;
        foreach (var module in modules)
        {
            if (string.IsNullOrWhiteSpace(module.Profile.Id) || !_prototypes.TryIndex(module.Profile, out var profile) ||
                !Finite(module.CurrentPower, 0f, profile.PowerCapacity) || module.PowerBus != null)
                return false;
            energy += module.CurrentPower;
            needsBus |= profile.RequiresPower;
            module.CurrentPower = 0f;
        }
        if (!needsBus)
            return true;
        if (!snapshot.Organs.ContainsKey("Thorax") || snapshot.CyberwareModules.Count >= MaximumCyberwarePerBody)
            return false;
        var sourceId = modules.Min(value => value.StableInstanceId);
        var holderId = new NCActionResolutionId(sourceId).DeriveGuid(0x43594255, 0);
        var cellId = new NCActionResolutionId(holderId).DeriveGuid(0x43454C4C, 0);
        var cellPrototype = _prototypes.Index(LegacyCellPrototype);
        if (!cellPrototype.TryComp<BatteryComponent>(out var battery, _componentFactory))
            return false;
        var holder = _prototypes.Index(PowerBusProfile);
        snapshot.CyberwareModules.Add(new NCCyberwareStateSnapshot
        {
            EntityPrototype = PowerBusPrototype.Id,
            Profile = PowerBusProfile,
            StableInstanceId = holderId,
            HostCategory = "Thorax",
            Slot = "NCSlotThoracicPower",
            MaximumIntegrity = holder.MaximumIntegrity,
            CurrentIntegrity = holder.MaximumIntegrity,
            NeuralIntegrity = 1f,
            NeuralState = NCCyberwareNeuralState.NotApplicable,
            Calibrated = true,
            PowerBus = new NCCyberwarePowerBusSnapshot
            {
                Main = new NCCyberwareCellSnapshot
                {
                    Prototype = LegacyCellPrototype,
                    StableInstanceId = cellId,
                    Charge = MathF.Min(energy, battery.MaxCharge),
                },
            },
        });
        return true;
    }

    public void ResetPowerClock(EntityUid body)
    {
        var state = EnsureComp<NCCyberwarePowerStateComponent>(body);
        state.Initialized = true;
        state.LastAdvancedAt = _timing.CurTime;
        RefreshBodyPower(body, _timing.CurTime);
        SchedulePowerBody(body);
    }
}
