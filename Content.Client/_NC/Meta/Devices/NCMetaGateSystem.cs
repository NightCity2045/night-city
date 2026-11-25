// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Meta.Devices;

namespace Content.Client._NC.Meta.Devices;

/// <summary>The decision is asynchronous and server-owned, so the client never predicts a gated interface opening.</summary>
public sealed class NCMetaGateSystem : SharedNCMetaGateSystem
{
    protected override bool TryHandleOpenAttempt(Entity<NCMetaGateComponent> device, EntityUid user) => false;
}
