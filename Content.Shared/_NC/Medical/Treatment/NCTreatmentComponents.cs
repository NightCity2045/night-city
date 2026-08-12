// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.ActionResolution;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Treatment;

/// <summary>
/// Server-owned first-aid state. Detailed treatment information is not networked; diagnostics can expose
/// capability-filtered summaries later. All cross-record references are persistence-safe identifiers.
/// </summary>
[RegisterComponent]
public sealed partial class NCTreatmentStateComponent : Component
{
    [DataField]
    public ProtoId<NCTreatmentProfilePrototype> Profile;

    [DataField]
    public Dictionary<Guid, NCWoundTreatmentRecord> WoundTreatments = new();

    [DataField]
    public Dictionary<ProtoId<NCTargetZonePrototype>, NCTourniquetRecord> Tourniquets = new();

    /// <summary>
    /// Bounded oldest-first list used for idempotency across persisted medical snapshots.
    /// </summary>
    [DataField]
    public List<NCActionResolutionId> ProcessedActions = [];

    [DataField]
    public List<NCTreatmentAuditRecord> AuditHistory = [];
}

/// <summary>
/// Data-only capability marker for a physical first-aid item.
/// </summary>
[RegisterComponent]
public sealed partial class NCTreatmentItemComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCTreatmentCapabilityPrototype> Capability;
}
