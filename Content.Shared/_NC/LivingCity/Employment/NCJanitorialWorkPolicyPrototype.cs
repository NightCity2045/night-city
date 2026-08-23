// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Cognition;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Employment;

/// <summary>
/// Data-driven bounds for real janitorial work. The server uses these limits for one local spatial query and then
/// delegates the actual cleaning to the normal interaction and fluid systems.
/// </summary>
[Prototype("ncJanitorialWorkPolicy")]
public sealed partial class NCJanitorialWorkPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public ProtoId<NCCognitionCapabilityPrototype> Capability { get; private set; }
    [DataField] public float SearchRadius { get; private set; } = 8f;
    [DataField] public float InteractionRange { get; private set; } = 1.35f;
    [DataField] public int MaximumCandidates { get; private set; } = 12;
}
