// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Runtime;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Marks real compatible cyberoptics hardware. It uses the module's existing power, heat, EMP and installed identity.</summary>
[RegisterComponent]
public sealed partial class NCMetaOpticsComponent : Component
{
    [DataField] public bool SupportsLowLight;
    [DataField] public float SensorRadius = 8;
    [DataField] public float LowLightLoad = 0.4f;
    public bool ManualOverride;
    public bool RequestedLowLight;
    public bool AddedVision;
    public EntityUid? Body;
    public EntityUid? LastInstalledBody;
    public bool Clearing;
    public float RuntimeLoad;
    public NCMetaRuntimeInstance? Controller;
    public TimeSpan NextCommand;
    public TimeSpan LeaseUntil;
}

/// <summary>Server-only verb routing for a physical body with compatible installed optics.</summary>
[RegisterComponent]
public sealed partial class NCMetaOpticsWearerComponent : Component;
