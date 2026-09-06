// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Missions.Prototypes;

/// <summary>Presentation and ordering of combat tiers; enemy equipment remains in entity prototypes.</summary>
[Prototype("ncMissionDifficulty")]
public sealed partial class NCMissionDifficultyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public LocId Name { get; private set; }
    [DataField(required: true)] public int Order { get; private set; }
}
