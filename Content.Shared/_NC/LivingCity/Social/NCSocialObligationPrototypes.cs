// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Social;

[DataDefinition]
public sealed partial class NCSocialObligationConsequence
{
    [DataField(required: true)] public NCSocialObligationKind Kind { get; private set; }
    [DataField(required: true)] public NCSocialObligationStatus Status { get; private set; }
    [DataField(required: true)] public ProtoId<NCRelationshipEffectPrototype> RelationshipEffect { get; private set; }
    [DataField(required: true)] public ProtoId<NCMemoryEventPrototype> MemoryEvent { get; private set; }
}

[Prototype("ncSocialObligationPolicy")]
public sealed partial class NCSocialObligationPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int MaximumObligationsPerParty { get; private set; } = 256;
    [DataField] public int MaximumQueryCount { get; private set; } = 50;
    [DataField] public long MaximumQuantity { get; private set; } = 1_000_000_000;
    [DataField] public TimeSpan MaximumDueTime { get; private set; } = TimeSpan.FromHours(8_760);
    [DataField(required: true)] public List<NCSocialObligationConsequence> Consequences { get; private set; } = [];
}

