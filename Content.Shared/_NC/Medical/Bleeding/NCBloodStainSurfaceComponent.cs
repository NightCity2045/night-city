// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared.Decals;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Bleeding;

/// <summary>
/// One compact, presentation-only blood mark attached to a structure. Coordinates and rotation are local to the
/// stained entity, so the mark follows moving doors instead of remaining suspended in world space.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCBloodSurfaceStain(
    ProtoId<DecalPrototype> Decal,
    Vector2 LocalPosition,
    float LocalRotation,
    Vector2 Size,
    Color Color);

/// <summary>
/// Bounded PVS state present only on structures that currently carry blood. It is runtime round state and is not
/// serialized into maps or character persistence.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true), UnsavedComponent]
public sealed partial class NCBloodStainSurfaceComponent : Component
{
    [DataField, AutoNetworkedField]
    public List<NCBloodSurfaceStain> Stains = [];
}
