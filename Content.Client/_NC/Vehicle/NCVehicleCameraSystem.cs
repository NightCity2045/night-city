// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.Vehicle.Components;
using Content.Shared._NC.Vehicle.Prototypes;
using Content.Shared.Camera;
using Robust.Client.Graphics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Vehicle;

/// <summary>Moves the driver's view toward the road ahead without altering steering or vehicle physics.</summary>
public sealed partial class NCVehicleCameraSystem : EntitySystem
{
    [Dependency] private IEyeManager _eye = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCVehicleOccupantComponent, GetEyeOffsetEvent>(OnEyeOffset);
    }

    private void OnEyeOffset(Entity<NCVehicleOccupantComponent> ent, ref GetEyeOffsetEvent args)
    {
        if (!TryComp<NCVehicleDriveComponent>(ent.Comp.Vehicle, out var drive) || drive.Driver != ent.Owner ||
            !TryComp<PhysicsComponent>(ent.Comp.Vehicle, out var body) ||
            !_prototypes.TryIndex(drive.Handling, out NCVehicleHandlingPrototype? tuning))
            return;
        var speed = body.LinearVelocity.Length();
        if (speed <= tuning.CameraLookAheadStartSpeed)
            return;
        var fraction = Math.Clamp((speed - tuning.CameraLookAheadStartSpeed) /
            (tuning.CameraLookAheadMaximumSpeed - tuning.CameraLookAheadStartSpeed), 0, 1);
        var worldOffset = Vector2.Normalize(body.LinearVelocity) * tuning.CameraLookAheadMaximum * fraction;
        args.Offset += (-_eye.CurrentEye.Rotation).RotateVec(worldOffset);
    }
}
