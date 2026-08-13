// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Wounds;

/// <summary>
/// Public, presentation-only wound mark. It deliberately excludes wound identity, forensic provenance,
/// internal bleeding and organ state; clients only receive what can be painted on the humanoid sprite.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCVisibleWoundMarker(
    ProtoId<NCTargetZonePrototype> Zone,
    NCWoundType Type,
    NCWoundSeverity Severity,
    NCWoundStage Stage,
    byte VisualX,
    byte VisualY,
    byte VisualDirection);

/// <summary>
/// Bounded PVS state used by the client world overlay. Authoritative wound records remain server-only.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class NCVisibleWoundComponent : Component
{
    [DataField, AutoNetworkedField]
    public List<NCVisibleWoundMarker> Markers = [];
}
