// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Employment;

[Serializable, NetSerializable]
public enum NCLivelihoodCategory : byte
{
    Service,
    Trade,
    Logistics,
    Technical,
    Informal,
    Unemployed,
}

/// <summary>
/// Data-driven occupation for residents who do not hold a formal player job in an authored organization.
/// </summary>
[Prototype("ncLivelihoodProfile")]
public sealed partial class NCLivelihoodProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string DisplayName { get; private set; } = string.Empty;

    [DataField]
    public NCLivelihoodCategory Category { get; private set; }

    [DataField]
    public bool PublicFacing { get; private set; } = true;

    [DataField]
    public List<string> Capabilities { get; private set; } = [];
}

/// <summary>
/// Persistent livelihood identity. It is intentionally independent from employment and faction affiliation.
/// </summary>
public sealed record NCLivelihoodSnapshot(
    NCActorId ActorId,
    string ProfileId,
    NCActiveWorldTime AssignedAt,
    int DataVersion,
    Guid LastAppliedEventId);
