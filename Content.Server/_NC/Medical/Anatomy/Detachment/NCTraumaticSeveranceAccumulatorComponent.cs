// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Anatomy.Detachment;

/// <summary>
/// Bounded server-runtime correlation for compound physical actions. It is intentionally unsaved: lasting damage
/// lives in the real organ graph and wounds, while an unfinished shotgun volley has no cross-round meaning.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCTraumaticSeveranceSystem))]
public sealed partial class NCTraumaticSeveranceAccumulatorComponent : Component
{
    public Dictionary<NCTraumaticSeveranceAccumulatorKey, float> Trauma = new();

    public Queue<NCTraumaticSeveranceAccumulatorKey> Order = new();
}

/// <summary>
/// Correlates only contacts from the same accepted action, damage mechanism and live anatomical region.
/// </summary>
public readonly record struct NCTraumaticSeveranceAccumulatorKey(
    Guid ResolutionId,
    ProtoId<OrganCategoryPrototype> Region,
    NCWoundType WoundType);
