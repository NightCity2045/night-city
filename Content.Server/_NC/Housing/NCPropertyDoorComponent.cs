// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace Content.Server._NC.Housing;

/// <summary>
/// Set at runtime on the outer doors of rentable properties: only a current key card opens them from outside.
/// Rebuilt every round from the property markers, so nothing here is saved.
/// </summary>
[RegisterComponent]
public sealed partial class NCPropertyDoorComponent : Component
{
    [ViewVariables]
    public string PropertyId = string.Empty;

    [ViewVariables]
    public string DoorKey = string.Empty;
}
