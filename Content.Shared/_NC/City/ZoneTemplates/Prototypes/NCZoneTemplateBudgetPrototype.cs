// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.City.ZoneTemplates.Prototypes;

/// <summary>
/// Shared server-tick work limits for zone deployments. Values live in YAML so operators can tune them safely.
/// </summary>
[Prototype("ncZoneTemplateBudget")]
public sealed partial class NCZoneTemplateBudgetPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public int MaxTileChangesPerTick { get; private set; }

    [DataField(required: true)]
    public int MaxEntitySpawnsPerTick { get; private set; }

    [DataField(required: true)]
    public int MaxSerializationWorkPerTick { get; private set; }

    [DataField(required: true)]
    public float MaxDeploymentMillisecondsPerTick { get; private set; }

    [DataField(required: true)]
    public int MaxParallelDeployments { get; private set; }
}
