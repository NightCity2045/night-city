// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared.SurveillanceCamera.Components;

namespace Content.Shared.SurveillanceCamera;

public abstract partial class SharedSurveillanceCameraSystem
{
    public bool CanSample(Entity<SurveillanceCameraComponent?> camera) => Resolve(camera, ref camera.Comp, false) && camera.Comp.Active;
}
