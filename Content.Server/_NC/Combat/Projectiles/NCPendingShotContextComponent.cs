// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Projectiles;

/// <summary>
/// Short-lived server-only snapshot created after all shot-cancellation hooks have accepted the action.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCProjectileContextSystem))]
public sealed partial class NCPendingShotContextComponent : Component
{
    [ViewVariables]
    public NCActionResolutionId ResolutionId;

    [ViewVariables]
    public EntityUid? Shooter;

    [ViewVariables]
    public EntityUid Weapon;

    [ViewVariables]
    public EntityUid? IntendedTarget;

    [ViewVariables]
    public NCTargetingMode TargetingMode;

    [ViewVariables]
    public ProtoId<NCTargetZonePrototype>? SelectedZone;

    [ViewVariables]
    public TimeSpan CreatedAt;
}
