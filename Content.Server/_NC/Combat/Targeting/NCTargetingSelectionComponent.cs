// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Combat.Targeting;

/// <summary>
/// Private server-authoritative zone intent. Directed acknowledgements expose it only to the controlling session.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCTargetingSelectionSystem))]
public sealed partial class NCTargetingSelectionComponent : Component
{
    [ViewVariables]
    public NCTargetingMode Mode = NCTargetingMode.Automatic;

    [ViewVariables]
    public ProtoId<NCTargetZonePrototype>? SelectedZone;

    [ViewVariables]
    public uint Revision;
}
