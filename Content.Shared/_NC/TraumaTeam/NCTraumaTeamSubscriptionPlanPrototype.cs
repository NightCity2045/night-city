// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.TraumaTeam;

/// <summary>
/// Data-driven commercial terms and future dispatch entitlements for one Trauma Team plan.
/// </summary>
[Prototype("ncTraumaTeamSubscriptionPlan")]
public sealed partial class NCTraumaTeamSubscriptionPlanPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Description { get; private set; } = default!;

    [DataField(required: true)]
    public long PricePerDay { get; private set; }

    [DataField(required: true)]
    public int DispatchPriority { get; private set; }

    [DataField(required: true)]
    public int TreatmentCoverageBasisPoints { get; private set; }

    [DataField(required: true)]
    public int MaximumThreatBasisPoints { get; private set; }

    [DataField]
    public string Color { get; private set; } = "#4FB9D7";
}
