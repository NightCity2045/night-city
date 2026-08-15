// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Combat.Targeting;

[Serializable, NetSerializable]
public enum NCTargetingMode : byte
{
    Automatic,
    SelectedZone,
}

/// <summary>
/// Player intent only. It never represents a confirmed hit or the actual impacted region.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCTargetingState(
    NCTargetingMode Mode,
    ProtoId<NCTargetZonePrototype>? SelectedZone);
