// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>Data-driven limits for natural-language commitments shared by every Living City profession.</summary>
[Prototype("ncHumanDirectivePolicy")]
public sealed partial class NCHumanDirectivePolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public TimeSpan DefaultDuration { get; private set; } = TimeSpan.FromHours(4);
    [DataField] public int MaximumPriority { get; private set; } = 900;
    [DataField] public int MaximumOrderLength { get; private set; } = 128;
    [DataField] public bool AllowEqualRank { get; private set; }
    [DataField] public bool ApplyToCurrentGroup { get; private set; } = true;
    [DataField] public Dictionary<string, int> FactionRoleRanks { get; private set; } = [];
}
