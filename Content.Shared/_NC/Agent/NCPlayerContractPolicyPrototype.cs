// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Agent;

[Prototype("ncPlayerContractPolicy")]
public sealed partial class NCPlayerContractPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField(required: true)] public int MaximumTitleLength;
    [DataField(required: true)] public int MaximumTermsLength;
    [DataField(required: true)] public int MaximumRewardLength;
    [DataField(required: true)] public int MaximumDeadlineMinutes;
    [DataField(required: true)] public TimeSpan PublishCooldown;
    [DataField(required: true)] public int PageSize;
}
