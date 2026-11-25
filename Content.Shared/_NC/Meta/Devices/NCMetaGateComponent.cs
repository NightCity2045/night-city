// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Shared._NC.Meta.Devices;

/// <summary>
/// Marks a device with a screen or a menu whose interface opens only if the program of the device agrees. A person who
/// tries to open it is cancelled in the ordinary activation path and turned into a <c>useRequested</c> event for the
/// device's META program; the program answers yes or no. With no program listening the device works as it always did.
/// </summary>
[RegisterComponent]
public sealed partial class NCMetaGateComponent : Component
{
    /// <summary>How long a program may take to answer before the person is turned away.</summary>
    [DataField] public float ResponseSeconds = 2f;
}
