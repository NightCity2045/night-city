// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Vehicle.Components;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.GameObjects;
using Robust.Shared.Enums;

namespace Content.Client._NC.Vehicle;

/// <summary>Vehicle glass reduces world contrast while preserving readable UI and never revealing occluded entities.</summary>
public sealed class NCVehicleWindshieldOverlay(IEntityManager entities, IPlayerManager players) : Overlay
{
    public override OverlaySpace Space => OverlaySpace.WorldSpace;

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (!entities.TryGetComponent<EyeComponent>(players.LocalEntity, out var eye) || args.Viewport.Eye != eye.Eye ||
            !entities.TryGetComponent<NCVehicleOccupantComponent>(players.LocalEntity, out var seat) ||
            !entities.TryGetComponent<NCVehicleDriveComponent>(seat.Vehicle, out var drive) || drive.Driver != players.LocalEntity ||
            !entities.TryGetComponent<NCVehicleAccessoriesComponent>(seat.Vehicle, out var accessories) || accessories.WindshieldDirt <= 0)
            return;
        var tint = accessories.DirtTint;
        args.WorldHandle.DrawRect(args.WorldAABB, tint.WithAlpha(tint.A * Math.Clamp(accessories.WindshieldDirt, 0, 1)));
    }
}
