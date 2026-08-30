// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Robust.Shared.Maths;

namespace Content.Server._NC.Medical.Bleeding;

/// <summary>
/// Bounded server-only aggregation of external blood loss by grid tile. It stores no gameplay logic and exists
/// only so repeated scheduler pulses upgrade one decal instead of creating new puddle entities.
/// </summary>
[RegisterComponent]
public sealed partial class NCBloodPoolGridComponent : Component
{
    [DataField]
    public Dictionary<Vector2i, NCBloodPoolTileRecord> Tiles = new();
}

/// <summary>
/// Data for one aggregated, cleanable floor mark.
/// </summary>
[DataDefinition]
public sealed partial class NCBloodPoolTileRecord
{
    [DataField]
    public float Volume;

    [DataField]
    public int Stage = -1;

    [DataField]
    public uint DecalId;

    [DataField]
    public Vector2 Position;

    [DataField]
    public Angle Rotation;

    [DataField]
    public TimeSpan ModifiedAt;
}
