// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.City.ZoneTemplates.Components;

/// <summary>
/// Round-local tag for template-created entities. Prepared snapshots receive fresh deployment tags when invoked.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCZoneDeploymentComponent : Component
{
    [DataField(required: true)]
    public Guid DeploymentId;

    [DataField(required: true, customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId TargetZoneId;

    [DataField(required: true)]
    public ProtoId<NCZoneTemplatePrototype> TemplateId;

    [DataField(required: true)]
    public int TemplateRevision;

    [DataField]
    public int SourceFloor;

    [DataField(required: true)]
    public NCZoneTemplateCleanupPolicy CleanupPolicy;
}
