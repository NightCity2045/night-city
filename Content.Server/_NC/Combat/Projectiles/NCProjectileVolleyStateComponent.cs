// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Projectiles;

/// <summary>
/// Bounded target-local memory for compound projectile volleys. It contains no persistent or networked state.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCProjectileVolleySystem))]
public sealed partial class NCProjectileVolleyStateComponent : Component
{
    public Dictionary<Guid, ProtoId<NCTargetZonePrototype>> PrimaryZones = new();

    public Queue<Guid> Order = new();
}
