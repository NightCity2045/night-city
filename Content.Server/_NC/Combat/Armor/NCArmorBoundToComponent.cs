// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.Combat.Armor;

/// <summary>
/// Runtime ownership marker maintained by inventory, cyberware or body integration.
/// It lets impacts validate a bounded candidate list without scanning the world or every inventory.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCArmorCoverageSystem))]
public sealed partial class NCArmorBoundToComponent : Component
{
    [ViewVariables]
    public EntityUid Target;
}

/// <summary>
/// Bounded source index stored on the protected target. It is rebuilt by explicit bind/unbind notifications,
/// never by an inventory or world scan during impact resolution.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCArmorCoverageSystem))]
public sealed partial class NCArmorRuntimeCacheComponent : Component
{
    [ViewVariables]
    public List<EntityUid> Sources = [];

    [ViewVariables]
    public uint Revision;
}
