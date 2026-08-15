// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Combat.Environment;

/// <summary>
/// Observable policy result for legacy negative physical damage. Typed treatment, surgery or trusted
/// rejuvenation is required; the compatibility scalar is never allowed to erase anatomy-aware wounds.
/// </summary>
[ByRefEvent]
public readonly record struct NCLegacyHealingRejectedEvent(
    ProtoId<DamageTypePrototype> DamageType,
    FixedPoint2 Amount);
