// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Server._NC.Meta.Domains;

[RegisterComponent]
public sealed partial class NCMetaDroneSensorComponent : Component
{
    [DataField] public float Range = 6;
    [DataField] public float FieldOfView = 120;
    [DataField] public float MinimumExposure = 0.08f;
    public TimeSpan NextObservation;
}

[RegisterComponent]
public sealed partial class NCMetaDroneManipulatorComponent : Component
{
    [DataField] public string Hand = "gripper";
}

[RegisterComponent]
public sealed partial class NCMetaDroneWeaponComponent : Component
{
    [DataField] public float DegreesPerSecond = 90;
    [DataField] public float AimToleranceDegrees = 3;
}
