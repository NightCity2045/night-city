// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Cognition;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Employment;

/// <summary>Bounds physical infrastructure repair performed through ordinary tools and RepairableSystem.</summary>
[Prototype("ncMaintenanceWorkPolicy")]
public sealed partial class NCMaintenanceWorkPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public ProtoId<NCCognitionCapabilityPrototype> Capability { get; private set; }
    [DataField] public float SearchRadius { get; private set; } = 8f;
    [DataField] public float InteractionRange { get; private set; } = 1.35f;
    [DataField] public int MaximumCandidates { get; private set; } = 12;
    [DataField] public TimeSpan CompletionTimeout { get; private set; } = TimeSpan.FromSeconds(20);
}
