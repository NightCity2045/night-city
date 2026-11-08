// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Prototypes;

namespace Content.Client.Weapons.Ranged.Systems;

public sealed partial class GunSystem
{
    /// <summary>Use the ordinary muzzle animation/light for a client-only visual source, without firing a gun.</summary>
    public void PlayLocalMuzzleFlash(EntityUid source, EntProtoId prototype, Angle angle)
    {
        if (!Exists(source) || !GetNetEntity(source).IsClientSide()) return;
        CreateEffect(source, new MuzzleFlashEvent(GetNetEntity(source), prototype.Id, angle));
    }
}
