// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Meta.Devices;

namespace Content.Client._NC.Meta.Devices;

/// <summary>The decision is asynchronous and server-owned, so the client never predicts a person opening the door.</summary>
public sealed class NCMetaDoorAccessSystem : SharedNCMetaDoorAccessSystem
{
    protected override bool TryHandleOpenAttempt(Entity<NCMetaDoorAccessComponent> door, EntityUid user) => false;
}
