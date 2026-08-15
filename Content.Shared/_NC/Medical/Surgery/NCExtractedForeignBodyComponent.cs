// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Surgery;

/// <summary>
/// Stable forensic data carried by an inert object materialized during surgery.
/// </summary>
[RegisterComponent]
public sealed partial class NCExtractedForeignBodyComponent : Component
{
    /// <summary>
    /// Matches the former embedded-object identifier on the wound.
    /// </summary>
    [DataField]
    public Guid ForeignBodyId;

    /// <summary>
    /// Prototype of the projectile that originally caused the retained foreign body.
    /// </summary>
    [DataField]
    public EntProtoId? SourceProjectilePrototype;
}
