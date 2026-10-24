// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Server._NC.Meta.Domains;

[RegisterComponent]
public sealed partial class NCMetaCommissioningToolComponent : Component
{
    public EntityUid? SelectedDevice;
}

[RegisterComponent]
public sealed partial class NCMetaWaypointComponent : Component
{
    [DataField] public string Name = "checkpointA";
}
