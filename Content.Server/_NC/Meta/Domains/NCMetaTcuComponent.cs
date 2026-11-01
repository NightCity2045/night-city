// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Runtime;

namespace Content.Server._NC.Meta.Domains;

[RegisterComponent]
public sealed partial class NCMetaTcuComponent : Component
{
    [DataField] public bool ElectronicActuator;
    [DataField] public bool SupportsEconomy = true;
    [DataField] public int LowMaximumGear = 2;
    public bool ManualOverride;
    public int Strategy;
    public int RequestedGear;
    public EntityUid? Driver;
    public uint DriverAuthority;
    public uint InputSequence;
    public NCMetaRuntimeInstance? Controller;
    public TimeSpan LeaseUntil;
    public TimeSpan NextCommand;
}
