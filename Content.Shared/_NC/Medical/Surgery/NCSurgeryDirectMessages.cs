// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Content.Shared.Body;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Surgery;

/// <summary>
/// Sanitized choice shown only when one direct tool interaction can begin several valid operations.
/// No patient, tool, organ entity or wound identity crosses the network boundary.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCSurgeryDirectProcedureOption(
    ProtoId<NCSurgeryProcedurePrototype> Procedure,
    ProtoId<OrganCategoryPrototype> TargetCategory,
    string TargetName);

[Serializable, NetSerializable]
public sealed class NCSurgeryDirectChoiceOffer(
    NCActionResolutionId offerId,
    List<NCSurgeryDirectProcedureOption> options) : EntityEventArgs
{
    public NCActionResolutionId OfferId { get; } = offerId;
    public List<NCSurgeryDirectProcedureOption> Options { get; } = options;
}

/// <summary>
/// Untrusted reply to a server-minted offer. The server accepts only a pair stored in the matching pending
/// interaction and revalidates all physical entities and medical conditions before starting the DoAfter.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCSurgeryDirectChoiceRequest(
    NCActionResolutionId offerId,
    ProtoId<NCSurgeryProcedurePrototype> procedure,
    ProtoId<OrganCategoryPrototype> targetCategory) : EntityEventArgs
{
    public NCActionResolutionId OfferId { get; } = offerId;
    public ProtoId<NCSurgeryProcedurePrototype> Procedure { get; } = procedure;
    public ProtoId<OrganCategoryPrototype> TargetCategory { get; } = targetCategory;
}
