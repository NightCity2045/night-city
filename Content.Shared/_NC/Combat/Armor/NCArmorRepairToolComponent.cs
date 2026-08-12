// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Combat.Armor;

/// <summary>
/// Data-only interaction contract for a consumable armor repair tool.
/// Repair amount and restoration ceiling remain on the section's YAML repair profile.
/// </summary>
[RegisterComponent]
public sealed partial class NCArmorRepairToolComponent : Component
{
    [DataField(required: true)]
    public List<string> Capabilities = [];

    [DataField(required: true)]
    public TimeSpan RepairDelay;

    [DataField(required: true)]
    public float InteractionRange;

    [DataField]
    public int ChargeCost = 1;
}
