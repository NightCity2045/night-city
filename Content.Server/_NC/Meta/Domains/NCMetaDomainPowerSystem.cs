// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Devices;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.PowerCell;
using Content.Shared.PowerCell.Components;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Physical power gates supplement APC power for portable, automotive and implanted controllers.</summary>
public sealed partial class NCMetaDomainPowerSystem : EntitySystem
{
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private PowerCellSystem _cells = default!;
    public override void Initialize()
    {
        base.Initialize(); _devices.RegisterPowerPolicy(CanPower);
        SubscribeLocalEvent<PowerCellSlotComponent, PowerCellSlotEmptyEvent>(OnEmpty);
        SubscribeLocalEvent<PowerCellSlotComponent, PowerCellChangedEvent>(OnChanged);
    }
    private bool? CanPower(EntityUid uid)
    {
        if (HasComp<PowerCellSlotComponent>(uid) && !_cells.HasCharge(uid, 1)) return false;
        if (TryComp<NCVehicleConditionComponent>(uid, out var vehicle) && (!float.IsFinite(vehicle.BatteryCharge) || vehicle.BatteryCharge <= 0)) return false;
        if (HasComp<NCMetaOpticsComponent>(uid) || HasComp<NCMetaFunctionalImplantComponent>(uid)) return TryComp<NCCyberwareComponent>(uid, out var cyberware) &&
            cyberware.Operational && cyberware.PowerGranted && !cyberware.ThermalShutdown;
        return null;
    }
    private void OnEmpty(Entity<PowerCellSlotComponent> ent, ref PowerCellSlotEmptyEvent args) => _devices.TryInvalidate(ent.Owner);
    private void OnChanged(Entity<PowerCellSlotComponent> ent, ref PowerCellChangedEvent args) => _devices.TryInvalidate(ent.Owner);
}
