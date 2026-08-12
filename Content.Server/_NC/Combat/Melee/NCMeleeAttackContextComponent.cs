// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Melee;

/// <summary>
/// Short-lived server-only snapshot for one already accepted vanilla melee attack.
/// </summary>
[RegisterComponent, Access(typeof(NCMeleeAttackAdapterSystem))]
public sealed partial class NCMeleeAttackContextComponent : Component
{
    public NCActionResolutionId ResolutionId;
    public EntityUid User;
    public EntityUid? IntendedTarget;
    public NCTargetingMode TargetingMode;
    public ProtoId<NCTargetZonePrototype>? SelectedZone;
    public uint NextImpactSequence;
    public HashSet<EntityUid> ResolvedTargets = [];
}
