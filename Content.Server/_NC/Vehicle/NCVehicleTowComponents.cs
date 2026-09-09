// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Utility;

namespace Content.Server._NC.Vehicle;

[RegisterComponent]
public sealed partial class NCVehicleTowCableComponent : Component
{
    [DataField(required: true)] public float MaximumLength;
    [DataField(required: true)] public float MinimumLength;
    [DataField(required: true)] public float Stiffness;
    [DataField(required: true)] public float Breakpoint;
    [DataField(required: true)] public float AttachSpeed;
    [DataField(required: true)] public SpriteSpecifier RopeSprite = default!;
    [ViewVariables] public EntityUid? First;
    [ViewVariables] public EntityUid? Holder;
}

[RegisterComponent, UnsavedComponent]
public sealed partial class NCVehicleTowLinkComponent : Component
{
    [ViewVariables] public EntityUid Other;
    [ViewVariables] public EntityUid Cable;
    [ViewVariables] public EntityUid Holder;
    [ViewVariables] public string Joint = string.Empty;
}
