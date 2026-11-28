// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Cyberware.AutoImplanter;

/// <summary>
/// Event auto-implanter: whoever uses it picks a piece of cyberware and gets it fitted on the spot, ready to work.
/// Admin-spawned only.
/// </summary>
[RegisterComponent]
public sealed partial class NCAutoImplanterComponent : Component;

[Serializable, NetSerializable]
public enum NCAutoImplanterUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum NCAutoImplanterGroup : byte
{
    Neural,
    Optics,
    Internal,
    Frame,
    Arms,
    Limbs,
}

[Serializable, NetSerializable]
public sealed record NCAutoImplanterEntry(string Item, NCAutoImplanterGroup Group);

[Serializable, NetSerializable]
public sealed class NCAutoImplanterUiState(List<NCAutoImplanterEntry> entries) : BoundUserInterfaceState
{
    public readonly List<NCAutoImplanterEntry> Entries = entries;
}

[Serializable, NetSerializable]
public sealed class NCAutoImplanterInstallMessage(string item) : BoundUserInterfaceMessage
{
    public readonly string Item = item;
}
