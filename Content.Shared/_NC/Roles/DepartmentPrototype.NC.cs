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

    /// <summary>
    /// Jobs allowed to perform personnel actions for this organization.
    /// Persistent employment is checked server-side; carrying an access card alone is insufficient.
    /// </summary>
    [DataField("ncPersonnelManagers")]
    public List<ProtoId<JobPrototype>> NCPersonnelManagers { get; private set; } = new();

    /// <summary>
    /// Persistent position limits. Missing jobs are not available through a personnel console.
    /// </summary>
    [DataField("ncPositionLimits")]
    public Dictionary<ProtoId<JobPrototype>, int> NCPositionLimits { get; private set; } = new();

    /// <summary>
    /// Organizational rank used to prevent a manager from changing an equal or superior position.
    /// </summary>
    [DataField("ncPositionRanks")]
    public Dictionary<ProtoId<JobPrototype>, int> NCPositionRanks { get; private set; } = new();
}
