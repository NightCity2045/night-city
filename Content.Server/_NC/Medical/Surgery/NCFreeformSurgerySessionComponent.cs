// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Surgery;

/// <summary>
/// Server-only operator session. Entity handles never cross the network boundary and are rebuilt whenever the
/// selected body region changes.
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCFreeformSurgerySessionComponent : Component
{
    [DataField]
    public NCActionResolutionId SessionId;

    [DataField]
    public EntityUid Patient;

    [DataField]
    public EntityUid Region;

    [DataField]
    public ProtoId<NCTargetZonePrototype> TargetZone;

    /// <summary>
    /// NC: The real incision currently displayed by the magnified operative view. Null keeps the body overview.
    /// </summary>
    [DataField]
    public Guid? ActiveIncisionId;

    [DataField]
    public NCWoundSurface Surface;

    [DataField]
    public float Depth;

    [DataField]
    public ProtoId<NCSurgeryToolCapabilityPrototype>? SelectedCapability;

    [DataField]
    public uint LastSequence;

    [DataField]
    public TimeSpan LastInputAt;

    [DataField]
    public TimeSpan LastRefreshAt;

    [DataField]
    public TimeSpan StartedAt;

    [ViewVariables]
    public readonly Dictionary<int, EntityUid> StructureHandles = [];

    [ViewVariables]
    public readonly Dictionary<int, EntityUid> AttachmentHandles = [];
}

/// <summary>Bounded active-round action receipts; not a saved authority or an item transfer ledger.</summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class NCFreeformSurgeryHistoryComponent : Component
{
    public readonly Queue<Guid> Completed = new();
}
