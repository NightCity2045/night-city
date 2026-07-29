// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Robust.Shared.Network;

namespace Content.Shared._NC.Identity.Components;

/// <summary>
/// Runtime link between a persistent character profile, its account, and its mind entity.
/// Persistent systems must store <see cref="ProfileId"/>, never the owning entity UID.
/// </summary>
[RegisterComponent]
public sealed partial class CharacterIdentityComponent : Component
{
    /// <summary>
    /// Stable identifier of the character profile in the database.
    /// </summary>
    [ViewVariables]
    public ProfileId ProfileId;

    /// <summary>
    /// Account that owns the character profile.
    /// </summary>
    [ViewVariables]
    public NetUserId AccountId;
}
