// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Pharmacology.Lab;

/// <summary>A professional laboratory procedure that the general action-resolution layer may judge.</summary>
[Prototype("ncBiotechProcedure")]
public sealed partial class NCBiotechProcedurePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; }
}

/// <summary>
/// A request for the common action-resolution layer to decide how well a professional procedure went.
/// Pharmacology never rolls for skill itself: it asks, and applies the quality that comes back.
/// </summary>
public readonly record struct NCBiotechActionRequest(
    NCActionResolutionId ResolutionId,
    EntityUid Actor,
    ProtoId<NCBiotechProcedurePrototype> Procedure,
    EntityUid? Equipment,
    IReadOnlyList<Guid> InputBatchIds);

/// <summary>
/// Raised on the bus for each request. A future action-resolution system answers by setting
/// <see cref="Quality"/>; without one the procedure goes exactly as the equipment allows.
/// </summary>
[ByRefEvent]
public struct NCBiotechActionResolveEvent(NCBiotechActionRequest request)
{
    public readonly NCBiotechActionRequest Request = request;

    /// <summary>Multiplier on the procedure's own benefit. 1 means a plain, unmodified result.</summary>
    public float Quality = 1f;
}

public readonly record struct NCBiotechActionResult(NCActionResolutionId ResolutionId, float Quality);
