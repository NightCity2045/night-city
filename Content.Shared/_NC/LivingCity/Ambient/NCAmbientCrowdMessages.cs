// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Coordinates;
using Robust.Shared.Maths;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Ambient;

[Serializable, NetSerializable]
public readonly record struct NCAmbientDecorativeVisual(
    NCMapCoordinates Origin,
    uint Seed,
    Color Color,
    float Scale,
    float MotionRadius,
    float MotionSpeed);

/// <summary>
/// Per-client bounded visual projection. It carries no actor identity and cannot be used as gameplay authority.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCAmbientDecorativeSnapshot(
    bool active,
    NCAmbientDecorativeVisual[] visuals) : EntityEventArgs
{
    public bool Active { get; } = active;
    public NCAmbientDecorativeVisual[] Visuals { get; } = visuals;
}
