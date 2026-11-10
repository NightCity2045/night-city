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

    /// <summary>How much the procedure depends on a good grip of the hands, 0..1.</summary>
    [DataField]
    public float GripDemand { get; private set; }

    /// <summary>How much it depends on seeing clearly, 0..1.</summary>
    [DataField]
    public float VisionDemand { get; private set; }

    /// <summary>How much it depends on steady, controlled movement, 0..1.</summary>
    [DataField]
    public float SteadinessDemand { get; private set; }

    /// <summary>How much it depends on a clear head, 0..1.</summary>
    [DataField]
    public float AttentionDemand { get; private set; }
}

/// <summary>What an actor brings to a procedure, each as a share of full ability. 1 is a healthy, alert person.</summary>
public readonly record struct NCBiotechActorState(float Grip, float Vision, float Steadiness, float Attention);

/// <summary>
/// How well a procedure goes for the person doing it. It looks only at the body of the actor — hands, eyes,
/// steadiness and wakefulness — and at how much the procedure leans on each. It rolls nothing: the same actor
/// in the same state always gets the same quality, and a future skill system multiplies on top of it.
/// </summary>
public static class NCBiotechQualityMath
{
    /// <summary>Quality cannot fall below this share even for a barely able actor; they still do something.</summary>
    public const float Floor = 0.5f;

    public static float Resolve(NCBiotechProcedurePrototype procedure, NCBiotechActorState actor)
    {
        var loss = Loss(procedure.GripDemand, actor.Grip) +
                   Loss(procedure.VisionDemand, actor.Vision) +
                   Loss(procedure.SteadinessDemand, actor.Steadiness) +
                   Loss(procedure.AttentionDemand, actor.Attention);
        return Math.Clamp(1f - loss, Floor, 1f);
    }

    private static float Loss(float demand, float ability) =>
        Math.Clamp(demand, 0f, 1f) * (1f - Math.Clamp(float.IsFinite(ability) ? ability : 1f, 0f, 1f));
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
