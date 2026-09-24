// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.TraumaTeam;

[Prototype("ncTraumaResponseConfiguration")]
public sealed partial class NCTraumaResponseConfigurationPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public int MaximumDescriptionLength { get; private set; } = 160;

    [DataField]
    public TimeSpan RequestCooldown { get; private set; } = TimeSpan.FromSeconds(30);

    [DataField]
    public TimeSpan RequestLifetime { get; private set; } = TimeSpan.FromMinutes(10);

    [DataField]
    public int RecentCallLimit { get; private set; } = 10;

    [DataField(required: true)]
    public HashSet<ProtoId<JobPrototype>> ResponseJobs { get; private set; } = [];
}
