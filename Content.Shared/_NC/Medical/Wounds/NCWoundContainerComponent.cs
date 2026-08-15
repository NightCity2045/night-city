// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Wounds;

/// <summary>
/// Server-only detailed wound storage. It is intentionally not a networked component; clients receive
/// capability-filtered snapshots instead.
/// </summary>
[RegisterComponent]
public sealed partial class NCWoundContainerComponent : Component
{
    [DataField]
    public ProtoId<NCWoundSystemProfilePrototype> Profile;

    [DataField]
    public Dictionary<Guid, NCWoundRecord> Wounds = new();

    /// <summary>
    /// Permanent, compact outcomes are kept outside the active wound budget.
    /// </summary>
    [DataField]
    public List<NCScarRecord> Scars = [];

    /// <summary>
    /// Trauma beyond the detailed-record budget remains physiologically meaningful without allocating
    /// an unbounded number of records.
    /// </summary>
    [DataField]
    public Dictionary<ProtoId<NCTargetZonePrototype>, float> OverflowTraumaByZone = new();

    [DataField]
    public float OverflowPain;

    [ViewVariables]
    public HashSet<NCProcessedImpactKey> ProcessedImpacts = new();

    [ViewVariables]
    public Queue<NCProcessedImpactKey> ProcessedImpactOrder = new();

    /// <summary>
    /// Movement events only accumulate a bounded scalar. The central physiology scheduler consumes it in
    /// one pass, avoiding a wound iteration for every transform event.
    /// </summary>
    [ViewVariables]
    public float PendingMovementDistance;

    [ViewVariables]
    public TimeSpan LastProgressionPersistenceEventAt;
}
