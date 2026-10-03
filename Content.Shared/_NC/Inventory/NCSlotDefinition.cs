// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared.Inventory;

/// <summary>
/// Night City inventory-template extensions for slots that may be provided by several equipment layers.
/// </summary>
public sealed partial class SlotDefinition
{
    /// <summary>
    /// Alternative equipment slots that may satisfy this slot's dependency.
    /// The first equipped provider that passes <see cref="DependsOnComponents"/> is accepted.
    /// </summary>
    [DataField]
    public string[] DependsOnAny { get; private set; } = [];
}
