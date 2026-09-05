// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Ambient;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Ambient.Components;

/// <summary>
/// Round-local identity of an interactive ambient entity. It intentionally contains no persistent ActorId.
/// </summary>
[RegisterComponent]
public sealed partial class NCAmbientCrowdMemberComponent : Component
{
    [ViewVariables]
    public ProtoId<NCAmbientCrowdProfilePrototype> Profile;

    [ViewVariables]
    public NCAmbientCrowdKind Kind;

    [ViewVariables]
    public Guid ClusterId;
}
