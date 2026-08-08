// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared.Roles;

public sealed partial class DepartmentPrototype
{
    /// <summary>
    /// Whether players may select this department as a character preference.
    /// </summary>
    [DataField("ncSelectable")]
    public bool NCSelectable { get; private set; }

    /// <summary>
    /// Initial job granted when a character without an employment record selects this department.
    /// </summary>
    [DataField("ncEntryJob")]
    public ProtoId<JobPrototype>? NCEntryJob { get; private set; }
}
