// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Shared._NC.Meta.Network;

/// <summary>
/// One laid piece of META data cable. Connected pieces form a physical data bus; the graph lives in the server system.
/// Ports attach by standing on a cable tile or on a tile beside one.
/// </summary>
[RegisterComponent]
public sealed partial class NCMetaDataCableComponent : Component;
