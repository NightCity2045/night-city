// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Client._NC.NCUI.Core;
using Robust.Shared.Maths;

namespace Content.Client._NC.NCUI.Spatial;

/// <summary>
/// Allocation-free query boundary used by rendering, hit-testing and selection.
/// </summary>
public interface IUiSpatialIndex
{
    int Count { get; }
    float CellSize { get; }
    int LastBucketsQueried { get; }

    void Insert(UiNodeId id, UIBox2 bounds);
    void Remove(UiNodeId id, UIBox2 bounds);
    void Update(UiNodeId id, UIBox2 oldBounds, UIBox2 newBounds);
    int QueryPoint(Vector2 point, Span<UiNodeId> results, out bool truncated);
    int QueryArea(UIBox2 area, Span<UiNodeId> results, out bool truncated);
    void Clear();
}
