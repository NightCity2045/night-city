// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared.Body;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Vehicle.Components;

/// <summary>Independent crumple zones; there is deliberately no combined vehicle hit-point pool.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(raiseAfterAutoHandleState: true)]
public sealed partial class NCVehicleBodyComponent : Component
{
    [DataField(required: true)] public Dictionary<string, NCVehicleBodyZone> Zones = new();
    [AutoNetworkedField] public Dictionary<string, float> Deformation = new();
    [AutoNetworkedField] public bool AirbagsDeployed;
    [AutoNetworkedField] public bool Burning;
    [DataField] public float WeaponEnergyPerDamage = 2000;
    [DataField] public float PedestrianTransfer = 1;
    [DataField] public float AirbagDeltaV = 7;
    [DataField] public float AirbagTransfer = 0.65f;
    [DataField] public float ModerateImpactDeltaV = 8;
    [DataField] public float SevereImpactDeltaV = 15;
    [DataField] public float CatastrophicDeltaV = 15;
    [DataField] public float MaximumModuleDamagePerImpact = 0.45f;
    [DataField] public float RolloverAcceleration = 14;
    [DataField] public float RolloverMinimumSpeed = 5;
    [DataField] public float FireIgnitionTemperature = 423.15f;
    [DataField] public float FirePartWearPerSecond = 0.005f;
    [DataField] public HashSet<string> FireDamageSlots = new();
    [DataField] public DamageSpecifier CabinFireDamage = new();
    [DataField] public ProtoId<DamageTypePrototype> PenetratingDamageType = "Piercing";
    [DataField(required: true)] public float MinimumImpactSpeed;
    [DataField(required: true)] public float BeltTransfer;
    [DataField(required: true)] public float UnbeltedTransfer;
    [DataField(required: true)] public float InjuryThreshold;
    [DataField(required: true)] public float TraumaPerDeltaV;
    [DataField(required: true)] public float MaximumTrauma;
    [DataField(required: true)] public ProtoId<NCTraumaProfilePrototype> TraumaProfile;
    [DataField(required: true)] public ProtoId<NCTargetZonePrototype> InjuryZone;
    [DataField(required: true)] public ProtoId<OrganCategoryPrototype> InjuryRegion;
}

[DataDefinition]
public sealed partial class NCVehicleBodyZone
{
    [DataField(required: true)] public LocId Name;
    [DataField(required: true)] public Vector2 Position;
    [DataField(required: true)] public float EnergyCapacity;
    [DataField(required: true)] public float ModuleEnergyCapacity;
    [DataField] public bool Exterior = true;
    [DataField] public HashSet<string> ProtectedSlots = new();
    [DataField] public float CabinDamageTransfer;
    [DataField] public HashSet<string> ExposedSeats = new();
}
