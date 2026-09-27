// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.Power;

/// <summary>
/// Makes every APC power receiver on this map or grid operate without external power.
/// </summary>
[RegisterComponent]
public sealed partial class AlwaysPoweredComponent : Component;

/// <summary>
/// Stores the authored receiver flags while <see cref="AlwaysPoweredComponent"/> overrides them.
/// Keeping this state on the entity makes component removal and entity deletion naturally memory-safe.
/// </summary>
[RegisterComponent]
public sealed partial class AlwaysPoweredReceiverStateComponent : Component
{
    [DataField]
    public bool NeedsPower;

    [DataField]
    public bool PowerDisabled;
}
