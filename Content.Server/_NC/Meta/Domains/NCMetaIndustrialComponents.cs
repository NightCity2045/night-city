// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Runtime;

namespace Content.Server._NC.Meta.Domains;

[RegisterComponent]
public sealed partial class NCMetaLoadSensorComponent : Component
{
    [DataField] public float Radius = 0.65f;
    [DataField] public bool Operational = true;
    public TimeSpan LastSample;
    public double Kilograms;
    public bool SampleValid;
}

[RegisterComponent]
public sealed partial class NCMetaConveyorComponent : Component
{
    [DataField] public EntityUid? LoadSensor;
    [DataField] public bool Interlocked;
    public bool ManualOverride;
    public bool ApplyingCommand;
    public ulong ServiceRevision = 1;
    public NCMetaRuntimeInstance? Controller;
    public TimeSpan LeaseUntil;
    public TimeSpan NextCommand;
}
