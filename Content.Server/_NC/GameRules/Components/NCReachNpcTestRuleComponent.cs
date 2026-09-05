// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Actors;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.GameRules.Components;

/// <summary>
/// Data-driven roster for the isolated Reach GPT-NPC test shift.
/// </summary>
[RegisterComponent, Access(typeof(NCReachNpcTestRuleSystem))]
public sealed partial class NCReachNpcTestRuleComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCRandomResidentGenerationProfilePrototype> GenerationProfile;

    [DataField(required: true)]
    public Dictionary<ProtoId<JobPrototype>, int> Roster = [];

    [DataField]
    public int NpcCount = 30;

    [DataField]
    public float SpawnRadius = 2f;
}
