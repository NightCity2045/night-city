// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared.Roles;

public sealed partial class JobPrototype
{
    /// <summary>
    /// Amount paid to the employed character during one organization payday.
    /// </summary>
    [DataField("salary")]
    public int Salary { get; private set; }
}
