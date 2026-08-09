// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Missions.Prototypes;

/// <summary>
/// Data-driven run recipe. Layout, dynamic content and slot compatibility contain no C# balance constants.
/// </summary>
[Prototype("ncMissionDefinition")]
public sealed partial class NCMissionDefinitionPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public ProtoId<NCZoneTemplatePrototype> Template { get; private set; }

    [DataField]
    public int MaximumParticipants { get; private set; } = 8;

    /// <summary>
    /// Total real-world lifetime of one public deployment, including its final warning period.
    /// </summary>
    [DataField(required: true)]
    public TimeSpan RunDuration { get; private set; }

    /// <summary>
    /// The final part of <see cref="RunDuration"/> displayed as an evacuation warning.
    /// </summary>
    [DataField]
    public TimeSpan EvacuationTime { get; private set; } = TimeSpan.FromMinutes(2);

    [DataField]
    public Dictionary<string, List<EntProtoId>> DynamicSpawnPools { get; private set; } = [];
}
