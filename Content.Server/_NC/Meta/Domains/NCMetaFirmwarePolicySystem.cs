// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.Vehicle;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared.Hands.EntitySystems;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Native physical service eligibility supplements the generic maintenance and durable installation workflow.</summary>
public sealed partial class NCMetaFirmwarePolicySystem : EntitySystem
{
    [Dependency] private NCMetaHardwareSystem _hardware = default!;
    [Dependency] private NCVehicleServiceSystem _vehicles = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private NCMetaDroneSystem _drones = default!;
    [Dependency] private NCMetaIndustrialSystem _industrial = default!;
    [Dependency] private NCMetaClinicalSystem _clinical = default!;
    [Dependency] private NCMetaMachineSystem _machines = default!;
    public override void Initialize()
    {
        base.Initialize();
        _hardware.RegisterFirmwarePolicy("vehicle-electronics", Vehicle);
        _hardware.RegisterFirmwarePolicy("installed-cyberware", Cyberware);
        _hardware.RegisterFirmwarePolicy("drone-maintenance", Drone);
        _hardware.RegisterFirmwarePolicy("conveyor-stopped", Conveyor);
        _hardware.RegisterFirmwarePolicy("clinical-idle", Clinical);
        _hardware.RegisterFirmwarePolicy("machine-idle", Machine);
    }
    private NCMetaFirmwareEligibility? Vehicle(EntityUid host, EntityUid user) => !HasComp<NCVehicleComponent>(host) ? null :
        _vehicles.CanServiceElectronics(host, user, out var revision)
            ? new(true, revision + ":" + _hands.GetActiveItem(user)) : new(false, string.Empty);
    private NCMetaFirmwareEligibility? Cyberware(EntityUid host, EntityUid user) => !HasComp<NCCyberwareComponent>(host) ? null :
        _cyberware.CanServiceFirmware(host, user, out var identity, out var revision)
            ? new(true, identity.ToString("N") + ":" + revision) : new(false, string.Empty);
    private NCMetaFirmwareEligibility? Drone(EntityUid host, EntityUid user) => !HasComp<NCMetaDroneComponent>(host) ? null :
        _drones.CanServiceFirmware(host, out var epoch) ? new(true, epoch.ToString()) : new(false, string.Empty);
    private NCMetaFirmwareEligibility? Conveyor(EntityUid host, EntityUid user) => !HasComp<NCMetaConveyorComponent>(host) ? null :
        _industrial.CanServiceFirmware(host, out var revision) ? new(true, revision.ToString()) : new(false, string.Empty);
    private NCMetaFirmwareEligibility? Clinical(EntityUid host, EntityUid user) => !HasComp<NCMetaClinicalComponent>(host) ? null :
        _clinical.CanServiceFirmware(host, out var epoch) ? new(true, epoch.ToString("N")) : new(false, string.Empty);
    private NCMetaFirmwareEligibility? Machine(EntityUid host, EntityUid user) => !HasComp<NCMetaMachineComponent>(host) ? null :
        _machines.CanServiceFirmware(host, out var epoch) ? new(true, epoch.ToString("N")) : new(false, string.Empty);
}
